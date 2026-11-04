using System.Buffers;
using StalkerGPS.Memory;

namespace StalkerGPS.Game;

/// <summary>The kinds of world objects whose state is tracked.</summary>
public enum RecordKind
{
    /// <summary>A door; its state is whether it's locked.</summary>
    Door,

    /// <summary>An item lying in the world (a "world spawn"); its state is whether it's been picked up.</summary>
    Spawn,

    /// <summary>A stash; its state is whether it's marked on the in-game map and whether it's been looted.</summary>
    Stash,
}

/// <summary>The state of a tracked object; which flags apply depends on its <see cref="RecordKind"/>.</summary>
[Flags]
public enum ObjectState
{
    None = 0,

    /// <summary>A door that's locked.</summary>
    Locked = 1,

    /// <summary>A world spawn that's been picked up.</summary>
    Collected = 2,

    /// <summary>A stash shown on the in-game map.</summary>
    Marked = 4,

    /// <summary>A stash that's been opened and has nothing left in it.</summary>
    Looted = 8,
}

/// <summary>A record found by <see cref="WorldRecords.Scan"/>: where it is and the object it holds.</summary>
public readonly record struct WorldRecord(RecordKind Kind, nint Address, uint Id, string Guid, nint AllocationBase);

/// <summary>
/// Finds Stalker 2's native records ("models") for doors, world spawns and stashes and reads their state.
/// </summary>
/// <remarks>
/// <para>
/// Stalker 2 keeps a record for every object in the world, loaded or not, separate from the actor
/// that exists only near the player. None of it is in Unreal's reflection data, so the layouts
/// below were found by watching records change in game (barricade and padlock broken, door opened,
/// stash emptied and looted, gun picked up) and may need updating after a game patch. Every record
/// starts with an object ID at +0x08 whose top byte is the object type, followed by a state dword at +0x0C.
/// </para>
/// <list type="bullet">
/// <item>Doors (type 0x78): flags at +0x50, where 0x4 = locked (0x1 = open); GUID at +0x54.</item>
/// <item>
/// Containers (type 0x38): world spawns and stashes share this record. State 0x10 normally; GUID at
/// +0xBC; two interface pointers (+0x78, +0x90) point into the game module.
/// </item>
/// <item>
/// A world spawn's state becomes 0x20 once it's been picked up. That record lasts until the game is
/// restarted: a save loaded in a fresh game has no record for it.
/// </item>
/// <item>
/// A stash shown on the in-game map has a pointer at +0x98 to its map marker, a record of type 0x20
/// whose +0x38 holds the stash's object ID. Looting the stash clears the pointer.
/// </item>
/// <item>
/// A stash's items are only created when it's first opened, which sets 0x2 in its flags at +0x198
/// (this survives restarts). The items are a list at +0x110 (pointer, then count at +0x118) of
/// 16-byte entries starting with the item's ID, or -1 once it's been taken. A stash is looted when
/// it's been opened and no entry holds an item; after a restart a looted stash's list is empty.
/// </item>
/// </list>
/// <para>
/// Each type keeps its records in a pool: a linked list of chunks, each a header (pointer to the next
/// chunk, then a 32-bit record count) followed by the records at a fixed stride (0xD8 for doors,
/// 0x1D0 for containers). The first chunk sits in the game module's own data, so
/// <see cref="ScanPools"/> finds it there (record 0, ID type&lt;&lt;24, right after its header) and
/// walks the list in milliseconds. If that fails, e.g. after a patch changes the layout,
/// <see cref="Scan"/> searches the whole heap for the GUIDs of objects we already know about (from
/// the map's data), which takes a minute or two. Either way, records are then re-read in place.
/// </para>
/// </remarks>
public static class WorldRecords
{
    private const int IdOffset = 0x08;
    private const int IdTypeByte = IdOffset + 3; // the ID is little-endian, so its top byte comes last
    private const int StateOffset = 0x0C;
    private const int RecordAlignment = 8;

    private const byte DoorType = 0x78;
    private const int DoorFlagsOffset = 0x50;
    private const int DoorGuidOffset = 0x54;
    private const int DoorLockedFlag = 0x4;

    private const byte ContainerType = 0x38;
    private const int ContainerGuidOffset = 0xBC;
    private static readonly int[] ContainerModulePointers = [0x78, 0x90];
    private const uint ContainerPresent = 0x10, SpawnCollected = 0x20;

    private const int StashMarkerOffset = 0x98;
    private const byte MarkerType = 0x20;
    private const int MarkerOwnerOffset = 0x38;

    private const int StashItemsOffset = 0x110;
    private const int StashItemCountOffset = 0x118;
    private const int ItemEntrySize = 16;
    private const int TakenItem = -1;
    private const int MaxItems = 500;
    private const int StashFlagsOffset = 0x198;
    private const int StashOpenedFlag = 0x2;
    private const int StashRecordSize = StashFlagsOffset + 4;

    // Large enough for the door and container layouts the scan matches on.
    private const int RecordSize = ContainerGuidOffset + 16;
    private const int ChunkSize = 16 * 1024 * 1024;

    private const int DoorStride = 0xD8;
    private const int ContainerStride = 0x1D0;
    private const int PoolHeaderSize = 0x10; // next chunk pointer, then the record count
    private const int MaxPoolChunk = 0x10000;
    private const int MaxPoolChunks = 1000;

    /// <summary>
    /// Finds the records of the given doors, spawns and stashes by walking the game's record pools.
    /// Returns null if a pool couldn't be found, in which case use <see cref="Scan"/>.
    /// </summary>
    public static List<WorldRecord>? ScanPools(ProcessMemory mem, IReadOnlyCollection<string> doorGuids, IReadOnlyCollection<string> spawnGuids,
        IReadOnlyCollection<string> stashGuids)
    {
        var doors = Lookup(doorGuids);
        var spawns = Lookup(spawnGuids);
        var stashes = Lookup(stashGuids);
        var found = new Dictionary<(RecordKind, string), WorldRecord>();
        var sections = mem.GetSections().Where(s => s.IsWritableData).ToList();

        if (doors is not null)
        {
            bool walked = WalkPool(mem, sections, DoorType, DoorStride, (address, record) =>
            {
                if (TryMatch(record[DoorGuidOffset..], doors, out var guid))
                    found.TryAdd((RecordKind.Door, guid), new WorldRecord(RecordKind.Door, address, BitConverter.ToUInt32(record[IdOffset..]), guid, 0));
            });
            if (!walked)
                return null;
        }
        if (spawns is not null || stashes is not null)
        {
            bool walked = WalkPool(mem, sections, ContainerType, ContainerStride, (address, record) =>
            {
                var at = record[ContainerGuidOffset..];
                RecordKind kind;
                if (TryMatch(at, spawns, out var guid))
                    kind = RecordKind.Spawn;
                else if (TryMatch(at, stashes, out guid))
                    kind = RecordKind.Stash;
                else
                    return;
                if (IsContainerRecord(mem, record))
                    found.TryAdd((kind, guid), new WorldRecord(kind, address, BitConverter.ToUInt32(record[IdOffset..]), guid, 0));
            });
            if (!walked)
                return null;
        }
        return [.. found.Values];
    }

    private delegate void RecordVisitor(nint address, ReadOnlySpan<byte> record);

    /// <summary>
    /// Finds a pool's first chunk in the module's data and visits every record in its chunk list.
    /// Returns false if no first chunk was found or the list didn't hold records of the type.
    /// </summary>
    private static bool WalkPool(ProcessMemory mem, List<ModuleSection> sections, byte type, int stride, RecordVisitor visit)
    {
        foreach (var head in FindPoolHeads(mem, sections, type))
        {
            int visited = 0;
            bool valid = true;
            nint chunk = head;
            for (int n = 0; chunk != 0 && n < MaxPoolChunks && valid; n++)
            {
                var header = mem.ReadBytes(chunk, PoolHeaderSize);
                int count = header is null ? -1 : BitConverter.ToInt32(header, 8);
                var records = count is > 0 and <= MaxPoolChunk ? mem.ReadBytes(chunk + PoolHeaderSize, count * stride) : null;
                if (header is null || records is null)
                {
                    valid = false;
                    break;
                }
                for (int r = 0; r < count; r++)
                {
                    var record = records.AsSpan(r * stride, stride);
                    if (record[IdTypeByte] != type)
                        continue;
                    visit(chunk + PoolHeaderSize + r * stride, record);
                    visited++;
                }
                chunk = (nint)BitConverter.ToInt64(header, 0);
            }
            if (valid && visited > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Candidate first chunks of a pool: a chunk header (next pointer, plausible count) directly
    /// followed by record 0 of the type, whose ID is the bare type byte and whose state is known.
    /// </summary>
    private static IEnumerable<nint> FindPoolHeads(ProcessMemory mem, List<ModuleSection> sections, byte type)
    {
        uint firstId = (uint)type << 24;
        foreach (var section in sections)
        {
            var data = mem.ReadBytes(section.Address, section.Size);
            if (data is null)
                continue;
            int recordOffset = PoolHeaderSize;
            for (int i = recordOffset + IdOffset; i + 8 <= data.Length; i += RecordAlignment)
            {
                if (BitConverter.ToUInt32(data, i) != firstId
                    || BitConverter.ToUInt32(data, i + 4) is not (ContainerPresent or SpawnCollected))
                    continue;
                int header = i - IdOffset - PoolHeaderSize;
                long next = BitConverter.ToInt64(data, header);
                int count = BitConverter.ToInt32(data, header + 8);
                if (count is > 0 and <= MaxPoolChunk && (next == 0 || next > 0x10000))
                    yield return section.Address + header;
            }
        }
    }

    /// <summary>
    /// Scans writable memory for the records of the given doors, spawns and stashes. Pass
    /// <paramref name="allocations"/> to scan only those allocations (where records were found
    /// last time) instead of the whole heap.
    /// </summary>
    public static List<WorldRecord> Scan(ProcessMemory mem, IReadOnlyCollection<string> doorGuids, IReadOnlyCollection<string> spawnGuids,
        IReadOnlyCollection<string> stashGuids, IReadOnlySet<nint>? allocations, CancellationToken ct)
    {
        var doors = Lookup(doorGuids);
        var spawns = Lookup(spawnGuids);
        var stashes = Lookup(stashGuids);
        var found = new Dictionary<(RecordKind, string), WorldRecord>();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + RecordSize);
        try
        {
            foreach (var region in mem.GetWritableRegions())
            {
                if (allocations is not null && !allocations.Contains(region.AllocationBase))
                    continue;
                // Chunks overlap by one record so none is split between two reads.
                for (long offset = 0; offset < region.Size; offset += ChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int length = (int)Math.Min(ChunkSize + RecordSize, region.Size - offset);
                    var chunk = buffer.AsSpan(0, length);
                    if (!mem.TryRead(region.Address + (nint)offset, chunk))
                        continue;
                    nint chunkAddress = region.Address + (nint)offset;
                    // A record near the end of a region may be shorter than RecordSize; doors are, so check them up to their own size.
                    for (int i = 0; i + DoorGuidOffset + 16 <= length; i += RecordAlignment)
                    {
                        RecordKind kind;
                        string? guid;
                        var record = chunk[i..];
                        if (record[IdTypeByte] == DoorType)
                        {
                            kind = RecordKind.Door;
                            if (!TryMatch(record[DoorGuidOffset..], doors, out guid))
                                continue;
                        }
                        else if (record[IdTypeByte] == ContainerType && i + RecordSize <= length)
                        {
                            var at = record[ContainerGuidOffset..];
                            if (TryMatch(at, spawns, out guid))
                                kind = RecordKind.Spawn;
                            else if (TryMatch(at, stashes, out guid))
                                kind = RecordKind.Stash;
                            else
                                continue;
                            if (!IsContainerRecord(mem, record))
                                continue;
                        }
                        else
                        {
                            continue;
                        }
                        found.TryAdd((kind, guid), new WorldRecord(kind, chunkAddress + i, BitConverter.ToUInt32(record[IdOffset..]), guid, region.AllocationBase));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return [.. found.Values];
    }

    /// <summary>
    /// Re-reads a record: for a door whether it's locked, for a spawn whether it's been picked up,
    /// for a stash whether it's marked on the in-game map and whether it's been looted. Returns null
    /// if the record no longer holds the same object (freed or reused) or its state isn't one we know.
    /// </summary>
    public static ObjectState? Read(ProcessMemory mem, WorldRecord record)
    {
        int guidOffset = record.Kind == RecordKind.Door ? DoorGuidOffset : ContainerGuidOffset;
        Span<byte> bytes = stackalloc byte[record.Kind == RecordKind.Stash ? StashRecordSize : guidOffset + 16];
        if (!mem.TryRead(record.Address, bytes)
            || BitConverter.ToUInt32(bytes[IdOffset..]) != record.Id
            || !bytes.Slice(guidOffset, 16).SequenceEqual(GuidBytes(record.Guid)))
            return null;
        switch (record.Kind)
        {
            case RecordKind.Door:
                return (BitConverter.ToInt32(bytes[DoorFlagsOffset..]) & DoorLockedFlag) != 0 ? ObjectState.Locked : ObjectState.None;
            case RecordKind.Spawn:
                return BitConverter.ToUInt32(bytes[StateOffset..]) switch
                {
                    ContainerPresent => ObjectState.None,
                    SpawnCollected => ObjectState.Collected,
                    _ => null,
                };
            default:
                var state = ObjectState.None;
                if (IsMarker(mem, BitConverter.ToInt64(bytes[StashMarkerOffset..]), record.Id))
                    state |= ObjectState.Marked;
                if ((BitConverter.ToInt32(bytes[StashFlagsOffset..]) & StashOpenedFlag) != 0
                    && HasNoItems(mem, BitConverter.ToInt64(bytes[StashItemsOffset..]), BitConverter.ToInt32(bytes[StashItemCountOffset..])))
                    state |= ObjectState.Looted;
                return state;
        }
    }

    /// <summary>Whether a stash's item list holds nothing: no entries, or only taken ones.</summary>
    private static bool HasNoItems(ProcessMemory mem, long list, int count)
    {
        if (count <= 0 || list == 0)
            return true;
        int n = Math.Min(count, MaxItems);
        var entries = mem.ReadBytes((nint)list, n * ItemEntrySize);
        if (entries is null)
            return false; // can't tell; don't report it as looted
        for (int i = 0; i < n; i++)
        {
            if (BitConverter.ToInt32(entries, i * ItemEntrySize) != TakenItem)
                return false;
        }
        return true;
    }

    /// <summary>Whether <paramref name="pointer"/> leads to a map marker for the object <paramref name="owner"/>.</summary>
    private static bool IsMarker(ProcessMemory mem, long pointer, uint owner)
    {
        if (pointer == 0)
            return false;
        Span<byte> marker = stackalloc byte[MarkerOwnerOffset + 4];
        return mem.TryRead((nint)pointer, marker)
            && marker[IdTypeByte] == MarkerType
            && BitConverter.ToUInt32(marker[MarkerOwnerOffset..]) == owner;
    }

    /// <summary>Container records have a known state and interface pointers into the game module; lookup tables that hold the same GUIDs don't.</summary>
    private static bool IsContainerRecord(ProcessMemory mem, ReadOnlySpan<byte> record)
    {
        uint state = BitConverter.ToUInt32(record[StateOffset..]);
        if (state is not (ContainerPresent or SpawnCollected))
            return false;
        foreach (int offset in ContainerModulePointers)
        {
            if (!mem.IsInModule(BitConverter.ToInt64(record[offset..])))
                return false;
        }
        return true;
    }

    /// <summary>GUIDs keyed by their first 8 bytes in memory, with the other 8 to compare.</summary>
    private static Dictionary<ulong, (string Guid, ulong Tail)>? Lookup(IReadOnlyCollection<string> guids)
    {
        if (guids.Count == 0)
            return null;
        var lookup = new Dictionary<ulong, (string, ulong)>(guids.Count);
        foreach (var guid in guids)
        {
            var bytes = GuidBytes(guid);
            lookup.TryAdd(BitConverter.ToUInt64(bytes), (guid, BitConverter.ToUInt64(bytes, 8)));
        }
        return lookup;
    }

    private static bool TryMatch(ReadOnlySpan<byte> at, Dictionary<ulong, (string Guid, ulong Tail)>? lookup, out string guid)
    {
        guid = "";
        if (lookup is null || !lookup.TryGetValue(BitConverter.ToUInt64(at), out var entry) || BitConverter.ToUInt64(at[8..]) != entry.Tail)
            return false;
        guid = entry.Guid;
        return true;
    }

    /// <summary>
    /// The in-memory bytes of a GUID written like Unreal's FGuid::ToString(): four uint32s as
    /// 8 hex digits each, stored little-endian.
    /// </summary>
    private static byte[] GuidBytes(string guid)
    {
        var bytes = new byte[16];
        for (int i = 0; i < 4; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), Convert.ToUInt32(guid.Substring(i * 8, 8), 16));
        return bytes;
    }
}
