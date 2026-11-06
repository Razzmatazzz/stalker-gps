namespace StalkerGPS.Services;

/// <summary>Where the website's map is, and the addresses that open it connected to this app.</summary>
public static class MapSite
{
    public const string Origin = "https://stalker.vodka";

    public static string BrowserUrl(int port, string language) =>
        $"{Origin}/map?connect={port}&lang={Uri.EscapeDataString(language)}";

    public static string OverlayUrl(int port, string language) =>
        $"{Origin}/map?overlay&connect={port}&lang={Uri.EscapeDataString(language)}";
}
