using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StalkerGPS.Services;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public sealed class TrackerSettings
{
    /// <summary>Process names to look for, without ".exe" (Steam and Game Pass builds).</summary>
    public List<string> ProcessNames { get; set; } = ["Stalker2-Win64-Shipping", "Stalker2-WinGDK-Shipping"];

    /// <summary>
    /// When false (default), the player is found automatically through the engine's reflection data.
    /// When true, the manual pointer chain below is used instead.
    /// </summary>
    public bool UseManualPointerChain { get; set; }

    /// <summary>Offset from the game module's base to the first pointer, as hex (e.g. "0x8A1B2C0").</summary>
    public string BaseOffset { get; set; } = "0x0";

    /// <summary>Pointer chain offsets as hex. The final offset should point at the X component of the location.</summary>
    public List<string> Offsets { get; set; } = [];

    /// <summary>How often to read the position while attached to the game.</summary>
    public int PollIntervalMs { get; set; } = 50;

    /// <summary>How often to look for the game process while it isn't running.</summary>
    public int GameCheckIntervalSeconds { get; set; } = 5;

    /// <summary>Loopback port the stalker.vodka map connects to (ws://localhost:port).</summary>
    public int WebSocketPort { get; set; } = 41347;

    /// <summary>Hide the window to the notification area when minimized.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Open minimized (to the tray, if <see cref="MinimizeToTray"/> is on).</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Language code, or null to follow Windows' display language.</summary>
    public string? Language { get; set; }

    /// <summary>Show the map in a small window over the game.</summary>
    public bool OverlayEnabled { get; set; }

    /// <summary>The overlay's width and height.</summary>
    public int OverlaySize { get; set; } = 420;

    /// <summary>How opaque the overlay is, in percent.</summary>
    public int OverlayOpacity { get; set; } = 90;

    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;

    /// <summary>Where the window was when the app last closed, which also identifies the monitor.</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    /// <summary>Hide the rest of the app, leaving only the map controls tray.</summary>
    public bool TrayCompact { get; set; }

    /// <summary>The overlay's on/off map controls that the user has changed, by control ID.</summary>
    public Dictionary<string, bool> MapToggles { get; set; } = [];

    /// <summary>The overlay's numeric map controls that the user has changed, by control ID.</summary>
    public Dictionary<string, double> MapRanges { get; set; } = [];

    public static bool TryParseHex(string text, out long value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Loads and saves settings to %LocalAppData%\StalkerGPS\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _lock = new();

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StalkerGPS",
        "settings.json");

    public TrackerSettings Current { get; private set; }

    /// <summary>Raised after settings are saved, on the caller's thread.</summary>
    public event Action? Saved;

    public string FilePath => _path;

    public SettingsStore()
    {
        Current = Load();
    }

    private TrackerSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(_path)) ?? new TrackerSettings();
        }
        catch (JsonException)
        {
            // Fall back to defaults if the file is malformed.
        }
        return new TrackerSettings();
    }

    /// <summary>Changes some settings and saves them all.</summary>
    public void Update(Action<TrackerSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            Write();
        }
        Saved?.Invoke();
    }

    /// <summary>The remembered map controls, as booleans and doubles.</summary>
    public Dictionary<string, object> MapControlValues()
    {
        lock (_lock)
        {
            return Current.MapToggles.Select(kv => (kv.Key, Value: (object)kv.Value))
                .Concat(Current.MapRanges.Select(kv => (kv.Key, Value: (object)kv.Value)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    /// <summary>
    /// Saves one map control. <see cref="Saved"/> isn't raised, because nothing else depends on these
    /// and the map can report them often.
    /// </summary>
    public void RememberMapControl(string id, object value) => Remember(settings =>
    {
        if (value is bool on)
            settings.MapToggles[id] = on;
        else if (value is double number)
            settings.MapRanges[id] = number;
    });

    /// <summary>Saves where the window is, without raising <see cref="Saved"/>.</summary>
    public void RememberWindowPosition(double left, double top) => Remember(settings =>
    {
        settings.WindowLeft = left;
        settings.WindowTop = top;
    });

    // If the file is busy, the next save writes the change.
    private void Remember(Action<TrackerSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            try
            {
                Write();
            }
            catch (IOException)
            {
            }
        }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
