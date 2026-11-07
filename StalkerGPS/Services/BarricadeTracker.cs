using StalkerGPS.Game;
using StalkerGPS.Memory;

namespace StalkerGPS.Services;

/// <summary>How many of a barricade's planks have been destroyed.</summary>
public readonly record struct BarricadeState(string Id, int Planks, int Destroyed, bool Cleared);

/// <summary>
/// Background loop that tracks how much of each boarded-up opening has been destroyed.
/// Raises <see cref="Changed"/> from a background thread.
/// </summary>
/// <remarks>
/// Like a picked-up spawn, a destroyed plank might have no record at all in a game restarted since,
/// so a missing plank counts as destroyed, unless it only appears through a quest and may just not
/// have appeared yet.
/// </remarks>
public sealed class BarricadeTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private const int MaxFailedReads = 10;

    // Missing planks only count as destroyed if at least this share of them was found; fewer means
    // the records aren't loaded (e.g. mid-load), not that the planks are gone.
    private const double MinimumPlankShare = 0.2;

    private readonly SettingsStore _settings;
    private readonly PositionTracker _tracker;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Every barricade on the map; empty when there is no player or its state is unknown.</summary>
    public IReadOnlyList<BarricadeState> Barricades { get; private set; } = [];

    /// <summary>Raised when a plank is destroyed or the state becomes known or unknown.</summary>
    public event Action? Changed;

    public BarricadeTracker(SettingsStore settings, PositionTracker tracker)
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
        IReadOnlyList<Barricade> barricades = [];
        List<string> plankIds = [];
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
                    }

                    if (_tracker.Status != TrackerStatus.Attached)
                    {
                        Publish([]);
                    }
                    else if ((memory ??= ProcessMemory.TryOpen(_settings.Current.ProcessNames, out _)) is { } game)
                    {
                        if (barricades.Count == 0)
                        {
                            barricades = await BarricadeData.LoadAsync(ct);
                            plankIds = [.. barricades.SelectMany(b => b.Planks).Distinct()];
                        }
                        if (pool == 0)
                            pool = WorldRecords.FindPlankPool(game);

                        if (pool == 0 || barricades.Count == 0)
                        {
                            Publish([]);
                            delay = RetryInterval;
                        }
                        else if (WorldRecords.ReadPlanks(game, pool, plankIds) is { } planks)
                        {
                            failedReads = 0;
                            Publish(planks.Count >= plankIds.Count * MinimumPlankShare ? States(barricades, planks) : []);
                        }
                        else
                        {
                            // A load empties the pool for a moment; only a lasting failure means it moved.
                            Publish([]);
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
                    Publish([]);
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

    private static List<BarricadeState> States(IReadOnlyList<Barricade> barricades, Dictionary<string, bool> planks)
    {
        var states = new List<BarricadeState>(barricades.Count);
        foreach (var barricade in barricades)
        {
            int destroyed = barricade.Planks.Count(id => planks.TryGetValue(id, out var gone) ? gone : !barricade.QuestPlanks);
            states.Add(new BarricadeState(barricade.Id, barricade.Planks.Count, destroyed, destroyed == barricade.Planks.Count));
        }
        return states;
    }

    private void Publish(IReadOnlyList<BarricadeState> barricades)
    {
        if (barricades.SequenceEqual(Barricades))
            return;
        Barricades = barricades;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
