using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StalkerGPS;

/// <summary>
/// Window frame styling for the custom (WindowChrome) title bar: dark mode, Windows 11 rounded
/// corners and a border color matching the app. Border color and corners need Windows 11.
/// </summary>
internal static partial class TitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmcpRound = 2;
    private const int SmCxFrame = 32;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;

    public static void Apply(Window window, string border)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
            return;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        int round = DwmcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        SetColor(hwnd, DwmwaBorderColor, border);
    }

    /// <summary>How far a maximized frameless window extends past the screen edge, in WPF units.</summary>
    public static Thickness MaximizedFrameThickness()
    {
        // GetSystemMetrics returns physical pixels for the primary monitor's DPI.
        double scale = 96.0 / GetDpiForSystem();
        double x = (GetSystemMetrics(SmCxFrame) + GetSystemMetrics(SmCxPaddedBorder)) * scale;
        double y = (GetSystemMetrics(SmCyFrame) + GetSystemMetrics(SmCxPaddedBorder)) * scale;
        return new Thickness(x, y, x, y);
    }

    private static void SetColor(nint hwnd, int attribute, string hex)
    {
        // COLORREF is 0x00BBGGRR.
        var rgb = Convert.ToInt32(hex.TrimStart('#'), 16);
        int colorRef = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
