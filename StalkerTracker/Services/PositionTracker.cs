using StalkerTracker.Memory;

namespace StalkerTracker.Services;

public enum TrackerStatus
{
    Stopped,
    WaitingForGame,
    Attached,
    PointerInvalid,
    Error,
}

public readonly record struct PlayerPosition(double X, double Y, double Z);

/// <summary>
/// Background loop that attaches to the game and polls the player's position.
/// Raises <see cref="Changed"/> from a background thread.
/// </summary>
public sealed class PositionTracker : IDisposable
{
    private readonly SettingsStore _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TrackerStatus Status { get; private set; } = TrackerStatus.Stopped;
    public string? Message { get; private set; }
    public PlayerPosition? Position { get; private set; }
    public int? ProcessId { get; private set; }
    public nint ModuleBase { get; private set; }
    public nint ResolvedAddress { get; private set; }

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
        Set(TrackerStatus.Stopped, null);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        ProcessMemory? memory = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, _settings.Current.PollIntervalMs)));
            do
            {
                if (memory is null || memory.HasExited)
                {
                    memory?.Dispose();
                    memory = ProcessMemory.TryOpen(_settings.Current.ProcessNames, out var error);
                    ProcessId = memory?.Process.Id;
                    ModuleBase = memory?.ModuleBase ?? 0;
                    if (memory is null)
                    {
                        Position = null;
                        Set(TrackerStatus.WaitingForGame, error);
                        continue;
                    }
                }

                Poll(memory);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Set(TrackerStatus.Error, ex.Message);
        }
        finally
        {
            memory?.Dispose();
            ProcessId = null;
        }
    }

    private void Poll(ProcessMemory memory)
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
            ResolvedAddress = address;
            Position = new PlayerPosition(v.X, v.Y, v.Z);
            Set(TrackerStatus.Attached, null);
        }
        else
        {
            // Normal while in menus or loading screens: the pawn doesn't exist yet.
            ResolvedAddress = 0;
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
