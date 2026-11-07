using StalkerGPS.Game;
using StalkerGPS.Localization;
using StalkerGPS.Memory;

namespace StalkerGPS.Services;

/// <summary>
/// Background loop that tracks the state of the map's objects while the player is in the world:
/// whether each door is locked, whether each world spawn has been picked up, and whether each
/// stash is marked on the in-game map or looted. Finds their records (by walking the game's record
/// pools, or a heap search of a minute or two if that fails; see <see cref="WorldRecords"/>), then
/// re-reads them every couple of seconds and reports changes.
/// Raises <see cref="KindChanged"/> and <see cref="Changed"/> from a background thread.
/// </summary>
/// <remarks>
/// A picked-up spawn keeps its record (marked collected) until the game is restarted; a save
/// loaded in a fresh game has no record for it at all. So a spawn with no record counts as
/// collected, except one that only appears through a quest, which may just not have appeared yet.
/// A quest's spawn is different once picked up: the game deletes it outright. So one whose record
/// was seen since the last scan, and is now gone, counts as collected.
/// </remarks>
public sealed class WorldStateTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    // Walking the pools is cheap, so it's redone now and then to pick up records the game adds
    // later (e.g. quest rewards). The full heap search is only redone when records go missing.
    private static readonly TimeSpan PoolRefreshInterval = TimeSpan.FromSeconds(30);

    // Spawns without a record only count as collected if the scan found at least this share of
    // the map's spawns; fewer means the scan missed them (e.g. mid-load), not that they're gone.
    private const double MinimumSpawnShare = 0.2;

    private static readonly RecordKind[] Kinds = Enum.GetValues<RecordKind>();

    private static readonly Func<Strings, string> WaitingForPlayer = s => s.WorldWaitingForPlayer;

    private readonly SettingsStore _settings;
    private readonly PositionTracker _tracker;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IReadOnlyDictionary<string, ObjectState>[] _states = EmptyStates();
    private int _recordCount;

    // Quest spawns whose record has been read since the records were last found.
    private readonly HashSet<string> _seenQuestSpawns = [];

    /// <summary>Each door found (<see cref="ObjectState.Locked"/>), by GUID.</summary>
    public IReadOnlyDictionary<string, ObjectState> Doors => _states[(int)RecordKind.Door];

    /// <summary>Each world spawn found (<see cref="ObjectState.Collected"/>), by ID.</summary>
    public IReadOnlyDictionary<string, ObjectState> Spawns => _states[(int)RecordKind.Spawn];

    /// <summary>Each stash found (<see cref="ObjectState.Marked"/>, <see cref="ObjectState.Looted"/>), by GUID.</summary>
    public IReadOnlyDictionary<string, ObjectState> Stashes => _states[(int)RecordKind.Stash];

    /// <summary>What the tracker is doing or why it has nothing, for display in the current language.</summary>
    public Func<Strings, string>? Message { get; private set; }

    /// <summary>
    /// Raised when the states of one kind change: after a scan, or when a door is locked or
    /// unlocked, an item picked up, or a stash marked, unmarked or looted.
    /// </summary>
    public event Action<RecordKind>? KindChanged;

    /// <summary>Raised when anything shown in the UI changes.</summary>
    public event Action? Changed;

    public WorldStateTracker(SettingsStore settings, PositionTracker tracker)
    {
        _settings = settings;
        _tracker = tracker;
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false })
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        ProcessMemory? memory = null;
        List<WorldRecord> records = [];
        HashSet<nint>? allocations = null;
        var ids = MapData.Empty;
        bool fromPools = false;
        var lastScan = DateTime.MinValue;
        try
        {
            while (true)
            {
                var delay = PollInterval;
                try
                {
                    if (memory is not null && memory.HasExited)
                    {
                        memory.Dispose();
                        memory = null;
                        records = [];
                        allocations = null;
                        Publish(NewStates());
                    }

                    // Records only exist once a save is loaded, i.e. when there's a player.
                    if (_tracker.Status != TrackerStatus.Attached)
                    {
                        SetMessage(records.Count == 0 ? WaitingForPlayer : null);
                    }
                    else if ((memory ??= ProcessMemory.TryOpen(_settings.Current.ProcessNames, out _)) is { } game)
                    {
                        if (records.Count == 0 && ids.IsEmpty)
                        {
                            SetMessage(s => s.WorldDownloading);
                            ids = await MapData.LoadAsync(ct);
                            if (ids.IsEmpty)
                            {
                                SetMessage(s => s.WorldDownloadFailed);
                                delay = RetryInterval;
                            }
                        }
                        if (records.Count == 0 && !ids.IsEmpty)
                        {
                            records = Scan(game, ids, ref allocations, out fromPools, ct);
                            lastScan = DateTime.UtcNow;
                            if (records.Count == 0)
                            {
                                SetMessage(s => s.WorldNothingFound);
                                delay = RetryInterval;
                            }
                            else
                            {
                                Publish(ReadAll(game, records, ids, out _));
                                SetMessage(null);
                            }
                        }
                        else if (records.Count > 0)
                        {
                            if (fromPools && DateTime.UtcNow - lastScan > PoolRefreshInterval
                                && WorldRecords.ScanPools(game, ids.DoorGuids, ids.SpawnIds, ids.StashGuids, ids.Devices) is { Count: > 0 } refreshed)
                            {
                                records = refreshed;
                                lastScan = DateTime.UtcNow;
                            }
                            var states = ReadAll(game, records, ids, out int missing);
                            // Many records gone means the game rebuilt them (e.g. another save was
                            // loaded); find them again. A few can come and go with DLC levels.
                            if (missing > records.Count / 10)
                                records = [];
                            else
                                PublishChanges(states);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    records = [];
                    SetMessage(s => string.Format(s.WorldFailed, ex.Message));
                    delay = RetryInterval;
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            memory?.Dispose();
        }
    }

    /// <summary>
    /// Walks the game's record pools, which takes milliseconds. If they can't be found (e.g. a patch
    /// changed their layout), searches where records were last time, then the whole heap.
    /// </summary>
    private List<WorldRecord> Scan(ProcessMemory memory, MapData ids, ref HashSet<nint>? allocations, out bool fromPools, CancellationToken ct)
    {
        _seenQuestSpawns.Clear();
        if (WorldRecords.ScanPools(memory, ids.DoorGuids, ids.SpawnIds, ids.StashGuids, ids.Devices) is { Count: > 0 } pooled)
        {
            fromPools = true;
            _recordCount = pooled.Count;
            return pooled;
        }

        fromPools = false;
        SetMessage(s => s.WorldFinding);
        // Spawns without a record are reported as collected, so a scan that misses records would
        // report them wrongly: anything short of last time's count gets a full scan.
        var records = allocations is null ? [] : WorldRecords.Scan(memory, ids.DoorGuids, ids.SpawnIds, ids.StashGuids, allocations, ct);
        if (records.Count == 0 || records.Count < _recordCount)
            records = WorldRecords.Scan(memory, ids.DoorGuids, ids.SpawnIds, ids.StashGuids, null, ct);
        allocations = records.Select(r => r.AllocationBase).ToHashSet();
        _recordCount = records.Count;
        return records;
    }

    private Dictionary<string, ObjectState>[] ReadAll(ProcessMemory memory, List<WorldRecord> records, MapData ids, out int missing)
    {
        var states = NewStates();
        var spawnRecords = new HashSet<string>();
        var readSpawns = new HashSet<string>();
        missing = 0;
        foreach (var record in records)
        {
            if (record.Kind == RecordKind.Spawn)
                spawnRecords.Add(record.Guid);
            if (WorldRecords.Read(memory, record) is { } state)
            {
                states[(int)record.Kind][record.Guid] = state;
                if (record.Kind == RecordKind.Spawn)
                    readSpawns.Add(record.Guid);
            }
            else
                missing++;
        }

        // A quest's spawn is deleted when picked up (see the class remarks).
        foreach (var id in ids.QuestSpawnIds)
        {
            if (readSpawns.Contains(id))
                _seenQuestSpawns.Add(id);
            else if (_seenQuestSpawns.Contains(id))
                states[(int)RecordKind.Spawn][id] = ObjectState.Collected;
        }

        // Spawns with no record at all have been picked up (see the class remarks).
        if (spawnRecords.Count >= ids.SpawnIds.Count * MinimumSpawnShare)
        {
            var questSpawns = ids.QuestSpawnIds.ToHashSet();
            foreach (var id in ids.SpawnIds)
            {
                if (!spawnRecords.Contains(id) && !questSpawns.Contains(id))
                    states[(int)RecordKind.Spawn][id] = ObjectState.Collected;
            }
        }
        return states;
    }

    private void PublishChanges(Dictionary<string, ObjectState>[] current)
    {
        var previous = _states;
        var changed = Kinds.Where(kind =>
        {
            var now = current[(int)kind];
            var before = previous[(int)kind];
            bool differs = now.Any(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value);
            // Records that went missing keep their last known state rather than being reported as gone.
            foreach (var (id, state) in before)
                now.TryAdd(id, state);
            return differs;
        }).ToList();
        if (changed.Count == 0)
            return;
        _states = [.. Kinds.Select(kind => changed.Contains(kind) ? current[(int)kind] : previous[(int)kind])];
        foreach (var kind in changed)
            KindChanged?.Invoke(kind);
        Changed?.Invoke();
    }

    private void Publish(Dictionary<string, ObjectState>[] states)
    {
        _states = states;
        foreach (var kind in Kinds)
            KindChanged?.Invoke(kind);
        Changed?.Invoke();
    }

    private static Dictionary<string, ObjectState>[] NewStates() => [.. Kinds.Select(_ => new Dictionary<string, ObjectState>())];

    private static IReadOnlyDictionary<string, ObjectState>[] EmptyStates() => NewStates();

    private void SetMessage(Func<Strings, string>? message)
    {
        if (message == Message)
            return;
        Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
