using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StalkerGPS.Services;

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

    public void Save(TrackerSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        Current = settings;
        Saved?.Invoke();
    }
}
