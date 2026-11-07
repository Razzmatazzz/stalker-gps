using System.Buffers;
using StalkerGPS.Memory;

namespace StalkerGPS.Game;

/// <summary>The kinds of world objects whose state is tracked.</summary>
public enum RecordKind
{
    /// <summary>
    /// A door; its state is whether it's locked. Also a device a quest switches on, such as a bunker
    /// door or a lever (see <see cref="WorldRecords"/>), which counts as locked until it's on.
    /// </summary>
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
/// <summary>A marker the game shows for a quest. X, Y and Z are in cm.</summary>
public readonly record struct QuestMarker(double X, double Y, double Z, uint? Owner, uint Quest, int Stage);

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
/// <item>
/// A map marker has its position at +0x20 (three floats) and its kind in the byte at +0xA8, which is 2
/// for a quest marker; the 3 bytes after it can hold anything. A quest marker with no owner (+0x38 is -1) sits at a fixed place; one with an owner follows
/// that object, e.g. an NPC to kill. Marker IDs are reused, so they say nothing about the quest.
/// +0x90 is the same for every marker of a journal quest, and +0x94 is the marker's stage: its place in
/// that quest's list of stages. What +0x90 stands for is unknown, so it has to be learned from markers
/// that can be identified another way. A finished objective's marker can stay in place with +0x90 at 0
/// and its state set to exactly 0x20; live markers' states can hold anything, even all 32 bits.
/// </item>
/// <item>
/// The game keeps a table of its objects, separate from their records, in an allocation of its own:
/// 0x1C-byte entries holding the object's ID, its GUID at +4, and the ID without its type byte at
/// +0x18. The first entry is a dummy. A marker's owner is such an ID, so this leads from the marker to
/// the GUID the quest data names.
/// </item>
/// <item>
/// A device that a quest's signal switches on, such as a bunker door or a lever, is a record of
/// type 0x80 (stride 0x78) with its GUID at +0x50 and flags at +0x60. Bit 0 is set once it's on,
/// e.g. the door opened; it's set for both the lever and the door it opens when the lever is pulled.
/// Only some devices have a record.
/// </item>
/// <item>
/// A plank nailed across an opening is a record of type 0x60 with its GUID at +0x7C. Destroying it
/// sets 0x20 in its state.
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
    private const int MarkerPositionOffset = 0x20;
    private const int MarkerKindOffset = 0xA8;
    private const int MarkerQuestOffset = 0x90;
    private const int MarkerStageOffset = 0x94;
    private const byte QuestMarkerKind = 2;
    private const uint MarkerFinished = 0x20;

    // A marker the game still shows points at something at +0; a finished quest's leftover can be null there.
    private const int MarkerLinkOffset = 0x00;

    private const int ObjectEntrySize = 0x1C;
    private const int ObjectEntryGuidOffset = 4;
    private const int ObjectEntryIndexOffset = 0x18;
    private const uint ObjectIndexMask = 0xFFFFFF;
    private const long MinObjectTableSize = 64 * 1024, MaxObjectTableSize = 64 * 1024 * 1024;

    // A region is the table if nearly all of its first entries are valid; some may have been freed.
    private const int ObjectTableProbeEntries = 128, ObjectTableProbeMinValid = 100;
    private const int MarkerStride = 0xB0;
    private const uint NoOwner = 0xFFFFFFFF;

    private const byte DeviceType = 0x80;
    private const int DeviceStride = 0x78;
    private const int DeviceGuidOffset = 0x50;
    private const int DeviceFlagsOffset = 0x60;
    private const int DeviceOnFlag = 0x1;

    private const byte PlankType = 0x60;
    private const int PlankStride = 0x98;
    private const int PlankGuidOffset = 0x7C;
    private const uint PlankDestroyed = 0x20;

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
        IReadOnlyCollection<string> stashGuids, IReadOnlyCollection<string>? deviceGuids = null)
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
        // Devices are reported as doors. Few have records, so a missing pool isn't a failure.
        if (Lookup(deviceGuids ?? []) is { } devices)
        {
            WalkPool(mem, sections, DeviceType, DeviceStride, (address, record) =>
            {
                if (TryMatch(record[DeviceGuidOffset..], devices, out var guid))
                    found.TryAdd((RecordKind.Door, guid), new WorldRecord(RecordKind.Door, address, BitConverter.ToUInt32(record[IdOffset..]), guid, 0));
            });
        }
        return [.. found.Values];
    }

    /// <summary>Finds the map marker pool, for <see cref="ReadQuestMarkers"/>. Returns 0 if there isn't one.</summary>
    public static nint FindMarkerPool(ProcessMemory mem) => FindPool(mem, MarkerType, MarkerStride);

    /// <summary>Finds the plank pool, for <see cref="ReadPlanks"/>. Returns 0 if there isn't one.</summary>
    public static nint FindPlankPool(ProcessMemory mem) => FindPool(mem, PlankType, PlankStride);

    private static nint FindPool(ProcessMemory mem, byte type, int stride)
    {
        var sections = mem.GetSections().Where(s => s.IsWritableData).ToList();
        foreach (var head in FindPoolHeads(mem, sections, type))
        {
            if (WalkChunks(mem, head, type, stride, (_, _) => { }))
                return head;
        }
        return 0;
    }

    /// <summary>
    /// Reads whether the given planks are destroyed, by GUID, from the pool; a plank with no record
    /// is left out. Returns null if the pool can't be read.
    /// </summary>
    public static Dictionary<string, bool>? ReadPlanks(ProcessMemory mem, nint pool, IReadOnlyCollection<string> guids)
    {
        var lookup = Lookup(guids);
        var planks = new Dictionary<string, bool>();
        bool walked = WalkChunks(mem, pool, PlankType, PlankStride, (_, record) =>
        {
            if (TryMatch(record[PlankGuidOffset..], lookup, out var guid))
                planks[guid] = (BitConverter.ToUInt32(record[StateOffset..]) & PlankDestroyed) != 0;
        });
        return walked ? planks : null;
    }

    /// <summary>Reads the game's quest markers from the pool, or returns null if it can't be read.</summary>
    public static List<QuestMarker>? ReadQuestMarkers(ProcessMemory mem, nint pool)
    {
        var markers = new List<QuestMarker>();
        bool walked = WalkChunks(mem, pool, MarkerType, MarkerStride, (_, record) =>
        {
            uint quest = BitConverter.ToUInt32(record[MarkerQuestOffset..]);
            if (record[MarkerKindOffset] != QuestMarkerKind || quest == 0
                || BitConverter.ToUInt64(record[MarkerLinkOffset..]) == 0
                || BitConverter.ToUInt32(record[StateOffset..]) == MarkerFinished)
                return;
            float x = BitConverter.ToSingle(record[MarkerPositionOffset..]);
            float y = BitConverter.ToSingle(record[(MarkerPositionOffset + 4)..]);
            float z = BitConverter.ToSingle(record[(MarkerPositionOffset + 8)..]);
            // A marker that isn't in use has no position.
            if (x != 0 || y != 0)
            {
                uint owner = BitConverter.ToUInt32(record[MarkerOwnerOffset..]);
                markers.Add(new QuestMarker(x, y, z, owner == NoOwner ? null : owner, quest, BitConverter.ToInt32(record[MarkerStageOffset..])));
            }
        });
        return walked ? markers : null;
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
            if (WalkChunks(mem, head, type, stride, visit))
                return true;
        }
        return false;
    }

    /// <summary>Visits every record in a pool's chunk list. Returns false if the list didn't hold records of the type.</summary>
    private static bool WalkChunks(ProcessMemory mem, nint head, byte type, int stride, RecordVisitor visit)
    {
        int visited = 0;
        nint chunk = head;
        for (int n = 0; chunk != 0 && n < MaxPoolChunks; n++)
        {
            var header = mem.ReadBytes(chunk, PoolHeaderSize);
            int count = header is null ? -1 : BitConverter.ToInt32(header, 8);
            var records = count is > 0 and <= MaxPoolChunk ? mem.ReadBytes(chunk + PoolHeaderSize, count * stride) : null;
            if (header is null || records is null)
                return false;
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
        return visited > 0;
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

    /// <summary>Finds the game's object table, for <see cref="ReadObjectGuids"/>. Returns null if there isn't one.</summary>
    public static MemoryRegion? FindObjectTable(ProcessMemory mem)
    {
        var probe = new byte[(ObjectTableProbeEntries + 1) * ObjectEntrySize];
        MemoryRegion? table = null;
        foreach (var region in mem.GetWritableRegions())
        {
            if (region.Size is < MinObjectTableSize or > MaxObjectTableSize || !mem.TryRead(region.Address, probe))
                continue;
            int valid = 0;
            for (int i = 1; i <= ObjectTableProbeEntries; i++)
            {
                if (IsObjectEntry(probe.AsSpan(i * ObjectEntrySize)))
                    valid++;
            }
            // Smaller arrays can look the same.
            if (valid >= ObjectTableProbeMinValid && region.Size > (table?.Size ?? 0))
                table = region;
        }
        return table;
    }

    /// <summary>
    /// Reads the objects with the given GUIDs from the table, by object ID. Returns null if the table
    /// can't be read or isn't there any more.
    /// </summary>
    public static Dictionary<uint, string>? ReadObjectGuids(ProcessMemory mem, MemoryRegion table, IReadOnlyCollection<string> guids)
    {
        var lookup = Lookup(guids);
        var objects = new Dictionary<uint, string>();
        int size = (int)table.Size;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var entries = buffer.AsSpan(0, size);
            if (!mem.TryRead(table.Address, entries))
                return null;
            bool any = false;
            for (int i = ObjectEntrySize; i + ObjectEntrySize <= size; i += ObjectEntrySize)
            {
                var entry = entries.Slice(i, ObjectEntrySize);
                if (!IsObjectEntry(entry))
                    continue;
                any = true;
                if (TryMatch(entry[ObjectEntryGuidOffset..], lookup, out var guid))
                    objects[BitConverter.ToUInt32(entry)] = guid;
            }
            return any ? objects : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsObjectEntry(ReadOnlySpan<byte> entry)
    {
        uint id = BitConverter.ToUInt32(entry);
        return id != 0 && id != NoOwner && (id & ObjectIndexMask) == BitConverter.ToUInt32(entry[ObjectEntryIndexOffset..])
            && BitConverter.ToUInt64(entry[ObjectEntryGuidOffset..]) != 0;
    }

    /// <summary>
    /// Re-reads a record: for a door whether it's locked, for a spawn whether it's been picked up,
    /// for a stash whether it's marked on the in-game map and whether it's been looted. Returns null
    /// if the record no longer holds the same object (freed or reused) or its state isn't one we know.
    /// </summary>
    public static ObjectState? Read(ProcessMemory mem, WorldRecord record)
    {
        bool device = record.Id >> 24 == DeviceType;
        int guidOffset = device ? DeviceGuidOffset : record.Kind == RecordKind.Door ? DoorGuidOffset : ContainerGuidOffset;
        Span<byte> bytes = stackalloc byte[record.Kind == RecordKind.Stash ? StashRecordSize : device ? DeviceFlagsOffset + 4 : guidOffset + 16];
        if (!mem.TryRead(record.Address, bytes)
            || BitConverter.ToUInt32(bytes[IdOffset..]) != record.Id
            || !bytes.Slice(guidOffset, 16).SequenceEqual(GuidBytes(record.Guid)))
            return null;
        switch (record.Kind)
        {
            case RecordKind.Door when device:
                return (BitConverter.ToInt32(bytes[DeviceFlagsOffset..]) & DeviceOnFlag) != 0 ? ObjectState.None : ObjectState.Locked;
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
