namespace StalkerGPS.Localization;

/// <summary>
/// All text the app shows, in English. The JSON files in this folder hold the other languages,
/// using these property names as keys; anything missing from a file stays in English.
/// Text with numbers uses "Label: {0}" so that translations don't need plural forms.
/// </summary>
public sealed class Strings
{
    public string StatusTracking { get; init; } = "Tracking";
    public string StatusWaitingForGame { get; init; } = "Waiting for game";
    public string StatusLocating { get; init; } = "Locating player";
    public string StatusNoPlayer { get; init; } = "No player";
    public string StatusError { get; init; } = "Error";
    public string StatusStarting { get; init; } = "Starting";

    public string WorldLoaded { get; init; } = "Doors and items loaded. Doors: {0} (locked: {1}) · Items: {2} (picked up: {3}) · Stashes: {4} (on your map: {5}, looted: {6})";
    public string WorldWaitingForPlayer { get; init; } = "Waiting for the player.";
    public string WorldDownloading { get; init; } = "Downloading the map's data...";
    public string WorldDownloadFailed { get; init; } = "Couldn't download the map's data. Retrying...";
    public string WorldNothingFound { get; init; } = "Nothing found yet. Retrying...";
    public string WorldFinding { get; init; } = "Finding doors and items...";
    public string WorldFailed { get; init; } = "Tracking doors and items failed: {0} Retrying...";

    public string ServerPortInUse { get; init; } = "Port {0} is already in use. Choose another port in Settings.";
    public string ServerListenFailed { get; init; } = "Could not listen on port {0}: {1}";

    public string Heading { get; init; } = "Heading";

    public string Settings { get; init; } = "Settings";
    public string SavedTo { get; init; } = "Saved to {0}";
    public string Language { get; init; } = "Language";
    public string LanguageAutomatic { get; init; } = "Automatic (Windows language)";
    public string ProcessNames { get; init; } = "Process names (comma separated, no .exe)";
    public string PollInterval { get; init; } = "Position update interval while the game is running (ms)";
    public string GameCheckInterval { get; init; } = "Check for the game every (seconds)";
    public string MapPort { get; init; } = "Map connection port";
    public string MapPortHelp { get; init; } = "The stalker.vodka map connects to ws://localhost on this port.";
    public string MinimizeToTray { get; init; } = "Minimize to tray";
    public string StartMinimized { get; init; } = "Start minimized";
    public string UseManualChain { get; init; } = "Use a manual pointer chain instead of finding the player automatically";
    public string ManualChainHelp { get; init; } = "Cheat Engine style chain: {0} → player location (3 doubles).";
    public string BaseOffset { get; init; } = "Base offset (hex)";
    public string Offsets { get; init; } = "Offsets (hex, comma separated)";
    public string OffsetsExample { get; init; } = "Example: {0}";
    public string Save { get; init; } = "Save";
    public string SettingsSaved { get; init; } = "Settings saved.";
    public string InvalidHex { get; init; } = "'{0}' is not a valid hex value.";

    public string MapTitle { get; init; } = "Map";
    public string OpenMapInBrowser { get; init; } = "Open Map in Browser";
    public string OverlaySection { get; init; } = "Overlay";
    public string OverlayShow { get; init; } = "Show the map over the game";
    public string OverlaySize { get; init; } = "Size";
    public string OverlayOpacity { get; init; } = "Opacity (%)";
    public string OverlayCorner { get; init; } = "Screen corner";
    public string CornerTopLeft { get; init; } = "Top left";
    public string CornerTopRight { get; init; } = "Top right";
    public string CornerBottomLeft { get; init; } = "Bottom left";
    public string CornerBottomRight { get; init; } = "Bottom right";
    public string OverlayHotkeys { get; init; } = "Hotkeys";
    public string HotkeyToggle { get; init; } = "Show or hide the overlay";
    public string HotkeyZoomIn { get; init; } = "Zoom in";
    public string HotkeyZoomOut { get; init; } = "Zoom out";
    public string HotkeyFollow { get; init; } = "Keep the player centered";
    public string HotkeyInteract { get; init; } = "Switch the mouse between the game and the overlay";
    public string MapControlsTitle { get; init; } = "Map controls";
    public string MapControlsWaiting { get; init; } = "Waiting for the map...";
    public string TrayShowControlsOnly { get; init; } = "Show only the map controls";
    public string TrayShowApp { get; init; } = "Show the full app";

    public string Minimize { get; init; } = "Minimize";
    public string Maximize { get; init; } = "Maximize";
    public string Restore { get; init; } = "Restore";
    public string Close { get; init; } = "Close";
    public string TrayShow { get; init; } = "Show";
    public string TrayExit { get; init; } = "Exit";

    public string UnhandledError { get; init; } = "An unhandled error has occurred.";
    public string Reload { get; init; } = "Reload";
}
