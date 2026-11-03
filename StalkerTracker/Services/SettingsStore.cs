using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StalkerTracker.Services;

public sealed class TrackerSettings
{
    /// <summary>Process names to look for, without ".exe" (Steam and Game Pass builds).</summary>
    public List<string> ProcessNames { get; set; } = ["Stalker2-Win64-Shipping", "Stalker2-WinGDK-Shipping"];

    /// <summary>Offset from the game module's base to the first pointer, as hex (e.g. "0x8A1B2C0").</summary>
    public string BaseOffset { get; set; } = "0x0";

    /// <summary>Pointer chain offsets as hex. The final offset should point at the X component of the location.</summary>
    public List<string> Offsets { get; set; } = [];

    public int PollIntervalMs { get; set; } = 50;

    public static bool TryParseHex(string text, out long value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Loads and saves settings to %LocalAppData%\StalkerTracker\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StalkerTracker",
        "settings.json");

    public TrackerSettings Current { get; private set; }

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
    }
}
