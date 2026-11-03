using StalkerGPS.Memory;
using StalkerGPS.Unreal;

namespace StalkerGPS.Services;

public enum TrackerStatus
{
    Stopped,
    WaitingForGame,
    Locating,
    Attached,
    PointerInvalid,
    Error,
}

/// <summary>
/// Location in Unreal units (cm). Yaw is the camera's heading in degrees, 0 to 360: 0 faces +X and
/// 90 faces +Y (clockwise seen from above). It is null when unknown, e.g. with a manual pointer chain.
/// </summary>
public readonly record struct PlayerPosition(double X, double Y, double Z, double? Yaw = null);

/// <summary>
/// Background loop that waits for the game, attaches to it, and polls the player's position.
/// Checks for the game slowly while it isn't running and polls quickly once attached.
/// Raises <see cref="Changed"/> from a background thread.
/// </summary>
public sealed class PositionTracker : IDisposable
{
    private static readonly TimeSpan LocateRetryInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TrackerStatus Status { get; private set; } = TrackerStatus.Stopped;
    public string? Message { get; private set; }
    public PlayerPosition? Position { get; private set; }
    public int? ProcessId { get; private set; }
    public nint ModuleBase { get; private set; }

    /// <summary>What the automatic resolver found, or null in manual mode.</summary>
    public string? EngineSummary { get; private set; }
    public string? PawnClass { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    public event Action? Changed;

    public PositionTracker(SettingsStore settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (IsRunning)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null)
            return;
        await _cts.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        Position = null;
        EngineSummary = null;
        PawnClass = null;
        Set(TrackerStatus.Stopped, null);
    }

    /// <summary>Restarts the loop so changed settings take effect.</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        ProcessMemory? memory = null;
        UnrealResolver? resolver = null;
        try
        {
            while (true)
            {
                var settings = _settings.Current;
                var delay = TimeSpan.FromMilliseconds(Math.Max(10, settings.PollIntervalMs));
                try
                {
                    string? openError = null;
                    if (memory is null || memory.HasExited)
                    {
                        memory?.Dispose();
                        resolver = null;
                        EngineSummary = null;
                        PawnClass = null;
                        memory = ProcessMemory.TryOpen(settings.ProcessNames, out openError);
                        ProcessId = memory?.Process.Id;
                        ModuleBase = memory?.ModuleBase ?? 0;
                    }

                    if (memory is null)
                    {
                        Position = null;
                        var checkInterval = Math.Max(1, settings.GameCheckIntervalSeconds);
                        Set(TrackerStatus.WaitingForGame, $"{openError} Checking every {checkInterval} s.");
                        delay = TimeSpan.FromSeconds(checkInterval);
                    }
                    else if (settings.UseManualPointerChain)
                    {
                        PollManual(memory);
                    }
                    else
                    {
                        if (resolver is null)
                        {
                            // Locating walks the whole object table and can fail while the game is
                            // still starting up, so retry on a slower interval.
                            Set(TrackerStatus.Locating, "Reading engine data from the game...");
                            resolver = UnrealResolver.TryCreate(memory, out var error);
                            EngineSummary = resolver?.Summary;
                            if (resolver is null)
                            {
                                Set(TrackerStatus.Locating, $"{error} Retrying...");
                                delay = LocateRetryInterval;
                            }
                        }
                        if (resolver is not null)
                            PollAutomatic(resolver);
                    }
                }
                catch (Exception ex)
                {
                    // Nothing restarts the loop by hand any more, so recover instead of exiting.
                    memory?.Dispose();
                    memory = null;
                    resolver = null;
                    Position = null;
                    Set(TrackerStatus.Error, $"{ex.Message} Retrying...");
                    delay = LocateRetryInterval;
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
            ProcessId = null;
        }
    }

    private void PollAutomatic(UnrealResolver resolver)
    {
        if (resolver.TryReadPosition(out var position))
        {
            Position = position;
            PawnClass = resolver.PawnClass;
            Set(TrackerStatus.Attached, null);
        }
        else
        {
            // Normal while in menus or loading screens: there is no player pawn.
            Position = null;
            Set(TrackerStatus.PointerInvalid, "Attached, but there is no player right now (menu or loading screen).");
        }
    }

    private void PollManual(ProcessMemory memory)
    {
        var settings = _settings.Current;
        if (!TrackerSettings.TryParseHex(settings.BaseOffset, out var baseOffset))
        {
            Set(TrackerStatus.Error, $"Invalid base offset '{settings.BaseOffset}'.");
            return;
        }

        var offsets = new List<long>(settings.Offsets.Count);
        foreach (var text in settings.Offsets)
        {
            if (!TrackerSettings.TryParseHex(text, out var offset))
            {
                Set(TrackerStatus.Error, $"Invalid offset '{text}'.");
                return;
            }
            offsets.Add(offset);
        }

        if (memory.TryResolveChain(baseOffset, offsets, out var address)
            && memory.TryReadVector3d(address, out var v))
        {
            Position = new PlayerPosition(v.X, v.Y, v.Z);
            Set(TrackerStatus.Attached, null);
        }
        else
        {
            Position = null;
            Set(TrackerStatus.PointerInvalid, "Attached, but the pointer chain did not resolve (menu, loading, or outdated offsets).");
        }
    }

    private void Set(TrackerStatus status, string? message)
    {
        Status = status;
        Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
