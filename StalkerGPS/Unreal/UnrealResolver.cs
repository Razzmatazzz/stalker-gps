using System.Text;
using StalkerGPS.Memory;
using StalkerGPS.Services;

namespace StalkerGPS.Unreal;

/// <summary>
/// Finds the local player's position by reading Unreal Engine's own data structures.
/// Nothing is hardcoded per game build: the name table (GNames), object table (GObjects) and
/// GWorld are located by their shape in the module's data sections, and field offsets are looked
/// up by name in the engine's reflection data. This survives game patches and most engine upgrades.
/// </summary>
/// <remarks>
/// Path: GWorld → OwningGameInstance → LocalPlayers[0] → PlayerController → AcknowledgedPawn
/// → RootComponent → RelativeLocation (FVector of doubles). The facing direction is the
/// PlayerController's ControlRotation (FRotator of doubles: pitch, yaw, roll), i.e. where the camera looks.
/// </remarks>
/// <summary>
/// An artifact in the world. <see cref="Id"/> is its ID on the map (e.g. GArtifactWrenched); a quest
/// artifact made from another artifact's blueprint has that artifact's ID. X, Y and Z are in cm.
/// </summary>
public readonly record struct LiveArtifact(string Id, double X, double Y, double Z);

public sealed class UnrealResolver
{
    private const int ObjectsPerChunk = 64 * 1024;
    private const int ClassDefaultObjectFlag = 0x10;
    private const int TemplateObjectFlags = ClassDefaultObjectFlag | 0x20;

    // Artifacts' blueprints are named BP_<ID>_C, except these.
    private static readonly Dictionary<string, string> ArtifactIdsByClass = new()
    {
        ["BP_AArtifactBall_C"] = "AArtifactWeirdBall",
        ["BP_AArtifactWater_C"] = "AArtifactWeirdWater",
        ["BP_AArtifactNut_C"] = "AArtifactWeirdNut",
        ["BP_AArtifactFlower_C"] = "AArtifactWeirdFlower",
        ["BP_AArtifactBolt_C"] = "AArtifactWeirdBolt",
        ["BP_AArtifactKettle_C"] = "AArtifactWeirdKettle",
        ["BP_QArtifact_Heart_C"] = "QuestArtifactHeartofChornobyl",
        ["BP_QArtifact_Chimera_C"] = "DLC01_ArtifactHeartofChimera",
        ["BP_AArtifact_CNPP_BigShard_C"] = "AArtifactCNPPShard",
        ["BP_AArtifact_CNPP_FinalRestored_C"] = "AArtifactCNPP_MainFinal",
        ["BP_AArtifact_CNPP_SmallShard_2_C"] = "AArtifactCNPP_SmallShard_2",
        ["BP_AArtifact_CNPP_SmallShard_3_C"] = "AArtifactCNPP_SmallShard_3",
        ["BP_AArtifact_CNPP_SmallShard_4_C"] = "AArtifactCNPP_SmallShard_4",
        ["BP_AArtifact_CNPP_SmallShard_5_C"] = "AArtifactCNPP_SmallShard_5",
        ["BP_AArtifact_CNPP_SmallShard_6_C"] = "AArtifactCNPP_SmallShard_6",
    };

    // UObject layout, stable since UE4.
    private const int ObjFlags = 0x08;
    private const int ObjClass = 0x10;
    private const int ObjName = 0x18;
    private const int ObjOuter = 0x20;

    private readonly ProcessMemory _mem;
    private readonly IReadOnlyList<ModuleSection> _dataSections;
    private readonly nint _nameBlocks;
    private readonly nint _objectArray;
    private readonly int _objectItemSize;
    private readonly Dictionary<int, string> _nameCache = [];

    private ReflectionLayout? _layout;
    private nint _worldClass;
    private Offsets _offsets;
    private nint _artifactClass;
    private readonly Dictionary<nint, bool> _artifactClasses = [];
    private List<nint> _gworldCandidates = [];
    private nint _lastPawn;
    private DateTime _lastWorldSearch;

    /// <summary>Human-readable description of what was found, for display.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Class name of the pawn being tracked, e.g. BP_Stalker2Character_C.</summary>
    public string? PawnClass { get; private set; }

    private record struct Offsets(
        int OwningGameInstance, int LocalPlayers, int PlayerController,
        int AcknowledgedPawn, int RootComponent, int RelativeLocation, int ControlRotation);

    private UnrealResolver(ProcessMemory mem, IReadOnlyList<ModuleSection> dataSections, nint nameBlocks, nint objectArray, int objectItemSize)
    {
        _mem = mem;
        _dataSections = dataSections;
        _nameBlocks = nameBlocks;
        _objectArray = objectArray;
        _objectItemSize = objectItemSize;
    }

    /// <summary>Locates the engine structures. Takes a few seconds; call from a background thread.</summary>
    public static UnrealResolver? TryCreate(ProcessMemory mem, out string? error)
    {
        var dataSections = mem.GetSections().Where(s => s.IsWritableData).ToList();
        if (dataSections.Count == 0)
        {
            error = "Could not read the game's module headers.";
            return null;
        }

        nint nameBlocks = 0, objectArray = 0;
        int itemSize = 0;
        foreach (var section in dataSections)
        {
            var data = mem.ReadBytes(section.Address, section.Size);
            if (data is null)
                continue;
            for (int i = 0; i + 0x20 <= data.Length; i += 8)
            {
                if (nameBlocks == 0 && IsNamePoolBlocks(mem, data, i))
                    nameBlocks = section.Address + i;
                if (objectArray == 0 && IsObjectArray(mem, data, i, out itemSize))
                    objectArray = section.Address + i;
            }
        }

        if (nameBlocks == 0 || objectArray == 0)
        {
            error = nameBlocks == 0 ? "Engine name table not found yet." : "Engine object table not found yet.";
            return null;
        }

        var resolver = new UnrealResolver(mem, dataSections, nameBlocks, objectArray, itemSize);
        return resolver.Initialize(out error) ? resolver : null;
    }

    /// <summary>Reads the player's position. Returns false in menus, loading screens, or if the world changed.</summary>
    public bool TryReadPosition(out PlayerPosition position)
    {
        position = default;

        var world = CurrentWorld(out var gameInstance);
        if (world == 0)
            return false;
        var localPlayers = _mem.ReadPointer(gameInstance + _offsets.LocalPlayers);
        if (localPlayers == 0 || _mem.ReadInt32(gameInstance + _offsets.LocalPlayers + 8) < 1) return false;
        var localPlayer = _mem.ReadPointer(localPlayers);
        if (localPlayer == 0) return false;
        var controller = _mem.ReadPointer(localPlayer + _offsets.PlayerController);
        if (controller == 0) return false;
        var pawn = _mem.ReadPointer(controller + _offsets.AcknowledgedPawn);
        if (pawn == 0) return false;
        var root = _mem.ReadPointer(pawn + _offsets.RootComponent);
        if (root == 0) return false;

        if (pawn != _lastPawn)
        {
            _lastPawn = pawn;
            PawnClass = ObjectName(_mem.ReadPointer(pawn + ObjClass));
        }

        if (!_mem.TryReadVector3d(root + _offsets.RelativeLocation, out var v))
            return false;
        // The rotator's components are pitch, yaw, roll; only yaw matters on a map.
        double? yaw = _mem.TryReadVector3d(controller + _offsets.ControlRotation, out var r) ? NormalizeDegrees(r.Y) : null;
        position = new PlayerPosition(v.X, v.Y, v.Z, yaw);
        return true;
    }

    /// <summary>Lists the artifacts in the world. Walks every object, so call it from a background thread, and not often.</summary>
    public List<LiveArtifact> ReadArtifacts()
    {
        var artifacts = new List<LiveArtifact>();
        if (_artifactClass == 0)
            return artifacts;
        foreach (var (address, cls) in ReadAllObjects())
        {
            if (!IsArtifactClass(cls) || (_mem.ReadInt32(address + ObjFlags) & TemplateObjectFlags) != 0)
                continue;
            var root = _mem.ReadPointer(address + _offsets.RootComponent);
            if (root == 0 || !_mem.TryReadVector3d(root + _offsets.RelativeLocation, out var v))
                continue;
            artifacts.Add(new LiveArtifact(ArtifactId(ObjectName(cls)), Math.Round(v.X), Math.Round(v.Y), Math.Round(v.Z)));
        }
        artifacts.Sort((a, b) => (a.Id, a.X, a.Y).CompareTo((b.Id, b.X, b.Y)));
        return artifacts;
    }

    private static string ArtifactId(string className) =>
        ArtifactIdsByClass.TryGetValue(className, out var id) ? id
            : className.StartsWith("BP_") && className.EndsWith("_C") ? className[3..^2]
            : className;

    private bool IsArtifactClass(nint cls)
    {
        if (_artifactClasses.TryGetValue(cls, out var known))
            return known;
        bool isArtifact = false;
        for (var c = cls; c != 0 && !isArtifact; c = _mem.ReadPointer(c + _layout!.SuperStruct))
            isArtifact = c == _artifactClass;
        return _artifactClasses[cls] = isArtifact;
    }

    private bool Initialize(out string? error)
    {
        error = null;
        var objects = ReadAllObjects();

        // UClass "Class" is the one object whose class is itself.
        var classClass = objects.FirstOrDefault(o => o.Address == o.Class).Address;
        if (classClass == 0)
        {
            error = "Engine class table not found.";
            return false;
        }

        // Only artifacts need it, so the position still works without it.
        _artifactClass = objects.FirstOrDefault(o => o.Class == classClass && ObjectName(o.Address) == "Artifact").Address;

        var wanted = new HashSet<string> { "World", "GameInstance", "Player", "Controller", "PlayerController", "Actor", "SceneComponent" };
        var classes = new Dictionary<string, nint>();
        foreach (var o in objects)
        {
            if (o.Class != classClass)
                continue;
            var name = ObjectName(o.Address);
            if (wanted.Contains(name) && !classes.ContainsKey(name) && ObjectName(_mem.ReadPointer(o.Address + ObjOuter)) == "/Script/Engine")
                classes[name] = o.Address;
        }
        var missing = wanted.Except(classes.Keys).ToList();
        if (missing.Count > 0)
        {
            error = $"Engine classes not found: {string.Join(", ", missing)}.";
            return false;
        }

        if (!ReflectionLayout.TryDetect(this, classes["SceneComponent"], out var layout))
        {
            error = "Unrecognized engine reflection layout.";
            return false;
        }

        _layout = layout;

        int Find(string cls, string prop) => layout.FindPropertyOffset(this, classes[cls], prop);
        _offsets = new Offsets(
            Find("World", "OwningGameInstance"),
            Find("GameInstance", "LocalPlayers"),
            Find("Player", "PlayerController"),
            Find("PlayerController", "AcknowledgedPawn"),
            Find("Actor", "RootComponent"),
            Find("SceneComponent", "RelativeLocation"),
            Find("Controller", "ControlRotation"));
        if (_offsets.OwningGameInstance < 0 || _offsets.LocalPlayers < 0 || _offsets.PlayerController < 0
            || _offsets.AcknowledgedPawn < 0 || _offsets.RootComponent < 0 || _offsets.RelativeLocation < 0
            || _offsets.ControlRotation < 0)
        {
            error = $"Some engine fields were not found ({_offsets}).";
            return false;
        }

        _worldClass = classes["World"];
        if (!FindGWorld(objects))
        {
            error = "No active world yet.";
            return false;
        }

        Summary = $"GNames +0x{_nameBlocks - _mem.ModuleBase:X}, GObjects +0x{_objectArray - _mem.ModuleBase:X} ({objects.Count:N0} objects), "
            + $"GWorld {string.Join(" / ", _gworldCandidates.Select(g => $"+0x{g - _mem.ModuleBase:X}"))}";
        return true;
    }

    /// <summary>
    /// Returns the active World (one that owns a GameInstance) through the first GWorld candidate
    /// that currently holds one, or 0 while loading. Re-searches if every candidate went stale.
    /// </summary>
    private nint CurrentWorld(out nint gameInstance)
    {
        gameInstance = 0;
        bool anyNonNull = false;
        for (int i = 0; i < _gworldCandidates.Count; i++)
        {
            var world = _mem.ReadPointer(_gworldCandidates[i]);
            if (world == 0)
                continue;
            anyNonNull = true;
            if (_mem.ReadPointer(world + ObjClass) != _worldClass)
                continue;
            gameInstance = _mem.ReadPointer(world + _offsets.OwningGameInstance);
            if (gameInstance == 0)
                continue;
            if (i > 0)
            {
                // Remember which global worked so the next poll checks it first.
                (_gworldCandidates[0], _gworldCandidates[i]) = (_gworldCandidates[i], _gworldCandidates[0]);
            }
            return world;
        }

        // All null is a loading screen. Otherwise our candidates are wrong: search again,
        // but not on every poll, since that walks every object.
        if (anyNonNull && DateTime.UtcNow - _lastWorldSearch > TimeSpan.FromSeconds(5))
            FindGWorld();
        return 0;
    }

    /// <summary>
    /// Finds globals that point at the active World (the one that owns a GameInstance). Some other
    /// globals briefly hold the same pointer, so candidates that change over a short window are dropped.
    /// </summary>
    private bool FindGWorld(List<(nint Address, nint Class)>? objects = null)
    {
        _lastWorldSearch = DateTime.UtcNow;
        objects ??= ReadAllObjects();
        var worlds = objects
            .Where(o => o.Class == _worldClass && (_mem.ReadInt32(o.Address + ObjFlags) & ClassDefaultObjectFlag) == 0)
            .Where(o => _mem.ReadPointer(o.Address + _offsets.OwningGameInstance) != 0)
            .Select(o => (long)o.Address)
            .ToHashSet();

        var candidates = new List<(nint Global, long World)>();
        foreach (var section in _dataSections)
        {
            var data = _mem.ReadBytes(section.Address, section.Size);
            if (data is null)
                continue;
            for (int i = 0; i + 8 <= data.Length; i += 8)
            {
                long p = BitConverter.ToInt64(data, i);
                if (worlds.Contains(p))
                    candidates.Add((section.Address + i, p));
            }
        }

        for (int sample = 0; sample < 5 && candidates.Count > 1; sample++)
        {
            Thread.Sleep(50);
            var stable = candidates.Where(c => _mem.ReadInt64(c.Global) == c.World).ToList();
            if (stable.Count == 0)
                break;
            candidates = stable;
        }

        _gworldCandidates = candidates.Select(c => c.Global).ToList();
        return _gworldCandidates.Count > 0;
    }

    private List<(nint Address, nint Class)> ReadAllObjects()
    {
        var chunks = _mem.ReadPointer(_objectArray);
        int numElements = _mem.ReadInt32(_objectArray + 20);
        var result = new List<(nint, nint)>(numElements);
        for (int chunk = 0; chunk * ObjectsPerChunk < numElements; chunk++)
        {
            int count = Math.Min(ObjectsPerChunk, numElements - chunk * ObjectsPerChunk);
            var chunkPtr = _mem.ReadPointer(chunks + chunk * 8);
            var items = chunkPtr == 0 ? null : _mem.ReadBytes(chunkPtr, count * _objectItemSize);
            if (items is null)
                continue;
            for (int k = 0; k < count; k++)
            {
                var obj = (nint)BitConverter.ToInt64(items, k * _objectItemSize);
                if (obj != 0)
                    result.Add((obj, _mem.ReadPointer(obj + ObjClass)));
            }
        }
        return result;
    }

    /// <summary>Unreal rotations accumulate past ±180°; maps want 0 to 360.</summary>
    private static double NormalizeDegrees(double degrees)
    {
        double d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }

    internal string ObjectName(nint obj) => obj == 0 ? "" : Name(_mem.ReadInt32(obj + ObjName));

    internal ProcessMemory Memory => _mem;

    /// <summary>Resolves an FName comparison index through FNamePool (UE 4.23+ / UE5 entry format).</summary>
    internal string Name(int index)
    {
        if (_nameCache.TryGetValue(index, out var cached))
            return cached;

        var block = _mem.ReadPointer(_nameBlocks + (index >> 16) * 8);
        var name = "";
        if (block != 0)
        {
            var entry = block + (index & 0xFFFF) * 2;
            var header = _mem.ReadBytes(entry, 2) is { } h ? BitConverter.ToUInt16(h) : 0;
            int length = header >> 6;
            bool wide = (header & 1) != 0;
            if (length is > 0 and <= 1024 && _mem.ReadBytes(entry + 2, wide ? length * 2 : length) is { } bytes)
                name = wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
        }
        return _nameCache[index] = name;
    }

    /// <summary>FNamePool.Blocks[0] starts with the entries "None" then "ByteProperty".</summary>
    private static bool IsNamePoolBlocks(ProcessMemory mem, byte[] data, int i)
    {
        long p = BitConverter.ToInt64(data, i);
        if (!IsUserPointer(p))
            return false;
        var b = mem.ReadBytes((nint)p, 20);
        return b is not null
            && Encoding.ASCII.GetString(b, 2, 4) == "None"
            && Encoding.ASCII.GetString(b, 8, 12) == "ByteProperty";
    }

    /// <summary>
    /// FChunkedFixedUObjectArray: Objects**, PreAllocatedObjects*, MaxElements, NumElements, MaxChunks, NumChunks,
    /// whose first items are the objects with InternalIndex 0, 1, 2.
    /// </summary>
    private static bool IsObjectArray(ProcessMemory mem, byte[] data, int i, out int itemSize)
    {
        itemSize = 0;
        long objects = BitConverter.ToInt64(data, i);
        int max = BitConverter.ToInt32(data, i + 16), num = BitConverter.ToInt32(data, i + 20);
        int maxChunks = BitConverter.ToInt32(data, i + 24), numChunks = BitConverter.ToInt32(data, i + 28);
        if (!IsUserPointer(objects) || num < 1000 || num > max || max > 0x4000000
            || numChunks < 1 || numChunks > maxChunks || (long)numChunks * ObjectsPerChunk < num)
            return false;

        var chunk0 = mem.ReadPointer((nint)objects);
        if (chunk0 == 0)
            return false;
        foreach (var size in new[] { 24, 16, 32 })
        {
            var items = mem.ReadBytes(chunk0, size * 3);
            if (items is null)
                continue;
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++)
            {
                long obj = BitConverter.ToInt64(items, k * size);
                ok = IsUserPointer(obj) && mem.ReadInt32((nint)obj + 0xC) == k;
            }
            if (ok)
            {
                itemSize = size;
                return true;
            }
        }
        return false;
    }

    private static bool IsUserPointer(long p) => p > 0x10000 && p < 0x7FFFFFFFFFFF;
}
