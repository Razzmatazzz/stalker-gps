using StalkerGPS.Game;
using StalkerGPS.Memory;

namespace StalkerGPS.Services;

/// <summary>An objective the game has a marker for right now.</summary>
public readonly record struct ActiveObjective(string Id, string JournalQuest);

/// <summary>Where the game shows a marker. In cm.</summary>
public readonly record struct MarkerPlace(double X, double Y, double Z);

/// <summary>
/// An objective with a fixed marker, and where the game shows it. A stage can list several places
/// in the map's data when the quest picks one of them, so <see cref="Markers"/> says which it is.
/// </summary>
public readonly record struct LiveObjective(string Id, string JournalQuest, IReadOnlyList<MarkerPlace> Markers)
{
    public bool Equals(LiveObjective other) => (Id, JournalQuest) == (other.Id, other.JournalQuest) && Markers.SequenceEqual(other.Markers);

    public override int GetHashCode() => HashCode.Combine(Id, JournalQuest);
}

/// <summary>
/// A quest marker that isn't at a known objective location, usually an NPC to deal with. In cm.
/// <see cref="Objectives"/> are what it's for: one objective when the marker could be identified,
/// otherwise each one that follows its object, or none if nothing is known.
/// </summary>
public readonly record struct QuestTarget(double X, double Y, double Z, IReadOnlyList<ActiveObjective> Objectives)
{
    public bool Equals(QuestTarget other) => (X, Y, Z) == (other.X, other.Y, other.Z) && Objectives.SequenceEqual(other.Objectives);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
}

/// <summary>
/// Background loop that works out the active objectives from the game's quest markers. A marker
/// without an owner sits at a fixed place, which gives the objectives there through the map's data.
/// One with an owner follows that object, so it's reported as a target, with the objectives that
/// follow that object. A marker also holds its stage and a value for its journal quest, so markers
/// of a quest narrow each other down to that quest, and then to the one stage. What each value means
/// is learned as quests are identified. An objective with no marker can't be seen at all.
/// Raises <see cref="Changed"/> from a background thread.
/// </summary>
public sealed class QuestTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private const int MaxFailedReads = 15;

    // Objective locations match their markers almost exactly; this only allows for rounding.
    private const double MatchDistance = 20;

    private static readonly TimeSpan TableSearchInterval = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settings;
    private readonly PositionTracker _tracker;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MemoryRegion? _objectTable;
    private DateTime _nextTableSearch;

    // What each marker quest value has turned out to mean, for this run of the game.
    private readonly Dictionary<uint, string> _journalQuests = [];

    /// <summary>The objectives with a fixed marker; empty when there is no player.</summary>
    public IReadOnlyList<LiveObjective> Objectives { get; private set; } = [];

    /// <summary>The quest markers on moving or unknown objects; empty when there is no player.</summary>
    public IReadOnlyList<QuestTarget> Targets { get; private set; } = [];

    /// <summary>Raised when an objective or target appears, moves or goes away.</summary>
    public event Action? Changed;

    public QuestTracker(SettingsStore settings, PositionTracker tracker)
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
        ObjectiveMap map = new([], [], []);
        nint pool = 0;
        int failedReads = 0;
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
                        pool = 0;
                        _journalQuests.Clear();
                    }

                    if (_tracker.Status != TrackerStatus.Attached)
                    {
                        Publish([], []);
                    }
                    else if ((memory ??= ProcessMemory.TryOpen(_settings.Current.ProcessNames, out _)) is { } game)
                    {
                        if (map.Locations.Count == 0)
                            map = await ObjectiveData.LoadAsync(ct);
                        if (pool == 0)
                            pool = WorldRecords.FindMarkerPool(game);

                        if (pool == 0 || map.Locations.Count == 0)
                        {
                            Publish([], []);
                            delay = RetryInterval;
                        }
                        else if (WorldRecords.ReadQuestMarkers(game, pool) is { } markers)
                        {
                            failedReads = 0;
                            Match(markers, map, ReadOwners(game, markers, map));
                        }
                        else
                        {
                            // A load empties the pool for a moment; only a lasting failure means it moved.
                            Publish([], []);
                            if (++failedReads >= MaxFailedReads)
                            {
                                pool = 0;
                                failedReads = 0;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    memory?.Dispose();
                    memory = null;
                    pool = 0;
                    Publish([], []);
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
    /// The GUIDs of the objects that have a marker, by object ID. An object's ID can change with a
    /// load, so the table is read each time rather than remembered. The table can move too, so an
    /// owner it doesn't hold means looking for it again, though kill targets may never be in it.
    /// </summary>
    private Dictionary<uint, string> ReadOwners(ProcessMemory game, List<QuestMarker> markers, ObjectiveMap map)
    {
        var wanted = markers.Where(m => m.Owner is not null).Select(m => m.Owner!.Value).ToList();
        if (map.Targets.Count == 0 || wanted.Count == 0)
            return [];

        var owners = _objectTable is { } table ? WorldRecords.ReadObjectGuids(game, table, map.Targets.Keys) : null;
        if ((owners is null || !wanted.All(owners.ContainsKey)) && DateTime.UtcNow >= _nextTableSearch)
        {
            _nextTableSearch = DateTime.UtcNow + TableSearchInterval;
            _objectTable = WorldRecords.FindObjectTable(game);
            if (_objectTable is { } found)
                owners = WorldRecords.ReadObjectGuids(game, found, map.Targets.Keys);
        }
        return owners ?? [];
    }

    private void Match(List<QuestMarker> markers, ObjectiveMap map, Dictionary<uint, string> owners)
    {
        var found = markers.Select(m => (Marker: m, Possible: Possible(m, map, owners))).ToList();
        var quests = JournalQuests(found.Select(f => (f.Marker, ThisStage(f.Possible, f.Marker, map))));

        var objectives = new SortedDictionary<ActiveObjective, SortedSet<MarkerPlace>>(Comparer<ActiveObjective>.Create((a, b) =>
            (a.JournalQuest, a.Id).CompareTo((b.JournalQuest, b.Id))));
        var targets = new List<QuestTarget>();
        foreach (var (marker, possible) in found)
        {
            var identified = Identify(marker, possible, map, quests);
            var place = new MarkerPlace(Math.Round(marker.X), Math.Round(marker.Y), Math.Round(marker.Z));
            if (marker.Owner is null && possible.Count > 0)
            {
                foreach (var objective in identified)
                {
                    if (!objectives.TryGetValue(objective, out var places))
                        objectives[objective] = places = new(Comparer<MarkerPlace>.Create((a, b) => (a.X, a.Y, a.Z).CompareTo((b.X, b.Y, b.Z))));
                    places.Add(place);
                }
            }
            else
                targets.Add(new QuestTarget(place.X, place.Y, place.Z, identified));
        }
        targets.Sort((a, b) => (a.X, a.Y, a.Z).CompareTo((b.X, b.Y, b.Z)));
        Publish([.. objectives.Select(o => new LiveObjective(o.Key.Id, o.Key.JournalQuest, [.. o.Value]))], targets);
    }

    /// <summary>The objectives a marker's place or owner allow; a location shared by several lists each of them.</summary>
    private static IReadOnlyList<ActiveObjective> Possible(QuestMarker marker, ObjectiveMap map, Dictionary<uint, string> owners)
    {
        if (marker.Owner is { } owner)
            return owners.TryGetValue(owner, out var guid) && map.Targets.TryGetValue(guid, out var followers) ? followers : [];
        return [.. map.Locations
            .Where(l => Math.Abs(l.X - marker.X) <= MatchDistance && Math.Abs(l.Y - marker.Y) <= MatchDistance)
            .Select(l => new ActiveObjective(l.Id, l.JournalQuest))];
    }

    /// <summary>Those of the objectives that are the marker's stage of their quest, or all of them if none is.</summary>
    private static IReadOnlyList<ActiveObjective> ThisStage(IReadOnlyList<ActiveObjective> objectives, QuestMarker marker, ObjectiveMap map)
    {
        var matching = objectives.Where(o => map.StageAt(o.JournalQuest, marker.Stage) == o).ToList();
        return matching.Count > 0 ? matching : objectives;
    }

    /// <summary>
    /// The journal quests each marker quest value can be, learning those that come down to one.
    /// Every marker of a value is in the same quest, so that quest must be possible for all of them.
    /// </summary>
    private Dictionary<uint, HashSet<string>> JournalQuests(IEnumerable<(QuestMarker Marker, IReadOnlyList<ActiveObjective> Possible)> found)
    {
        var quests = new Dictionary<uint, HashSet<string>>();
        foreach (var (marker, possible) in found)
        {
            if (possible.Count == 0)
                continue;
            var journalQuests = possible.Select(o => o.JournalQuest).ToHashSet();
            if (quests.TryGetValue(marker.Quest, out var shared))
                shared.IntersectWith(journalQuests);
            else
                quests[marker.Quest] = journalQuests;
        }

        foreach (var (value, possible) in quests)
        {
            if (possible.Count == 1)
                _journalQuests[value] = possible.Single();
        }
        foreach (var (value, possible) in quests)
        {
            // What's already known wins, and each quest has only one value.
            if (_journalQuests.TryGetValue(value, out var known) && possible.Contains(known))
            {
                possible.IntersectWith([known]);
            }
            else if (possible.Count > 1)
            {
                var left = possible.Except(_journalQuests.Values).ToList();
                if (left.Count > 0)
                    possible.IntersectWith(left);
            }
        }
        foreach (var (value, known) in _journalQuests)
            quests.TryAdd(value, [known]);
        return quests;
    }

    /// <summary>
    /// What's known about what a marker is for: its stage if its quest is known, otherwise the
    /// objectives still possible. Nothing here ever rules out all of them; if the markers disagree,
    /// the place or owner is more reliable.
    /// </summary>
    private static IReadOnlyList<ActiveObjective> Identify(QuestMarker marker, IReadOnlyList<ActiveObjective> possible,
        ObjectiveMap map, Dictionary<uint, HashSet<string>> quests)
    {
        if (!quests.TryGetValue(marker.Quest, out var journalQuests))
            return ThisStage(possible, marker, map);

        var inQuest = possible.Where(o => journalQuests.Contains(o.JournalQuest)).ToList();
        if (possible.Count > 0 && inQuest.Count == 0)
            return ThisStage(possible, marker, map);
        if (journalQuests.Count == 1 && map.StageAt(journalQuests.Single(), marker.Stage) is { } stage)
            return [stage];
        return ThisStage(inQuest, marker, map);
    }

    private void Publish(IReadOnlyList<LiveObjective> objectives, IReadOnlyList<QuestTarget> targets)
    {
        if (objectives.SequenceEqual(Objectives) && targets.SequenceEqual(Targets))
            return;
        Objectives = objectives;
        Targets = targets;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
