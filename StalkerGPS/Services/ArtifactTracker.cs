using StalkerGPS.Memory;
using StalkerGPS.Unreal;

namespace StalkerGPS.Services;

/// <summary>
/// Background loop that lists the artifacts the game has loaded around the player. They are spawned
/// while playing and jump around, so unlike the map's fixed spawns they can't be known in advance.
/// Raises <see cref="Changed"/> from a background thread.
/// </summary>
public sealed class ArtifactTracker : IDisposable
{
    // Each check walks the engine's whole object table, which takes a few tenths of a second.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settings;
    private readonly PositionTracker _tracker;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>The artifacts loaded right now; empty when there is no player.</summary>
    public IReadOnlyList<LiveArtifact> Artifacts { get; private set; } = [];

    /// <summary>Raised when an artifact appears, moves or goes away.</summary>
    public event Action? Changed;

    public ArtifactTracker(SettingsStore settings, PositionTracker tracker)
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
        UnrealResolver? resolver = null;
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
                        resolver = null;
                    }

                    if (_tracker.Status != TrackerStatus.Attached)
                    {
                        Publish([]);
                    }
                    else
                    {
                        memory ??= ProcessMemory.TryOpen(_settings.Current.ProcessNames, out _);
                        if (memory is not null)
                            resolver ??= UnrealResolver.TryCreate(memory, out _);
                        if (resolver is null)
                            delay = RetryInterval;
                        else
                            Publish(resolver.ReadArtifacts());
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    memory?.Dispose();
                    memory = null;
                    resolver = null;
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

    private void Publish(IReadOnlyList<LiveArtifact> artifacts)
    {
        if (artifacts.SequenceEqual(Artifacts))
            return;
        Artifacts = artifacts;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
