using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StalkerGPS.Localization;
using StalkerGPS.Services;

namespace StalkerGPS;

/// <summary>
/// The website's map in a small always-on-top window over the game. Clicks pass through to the game
/// until the user turns that off, so the map is driven by hotkeys and the main window's map controls.
/// It only shows while the game (or this app, to use the map controls) is in front, and steps aside
/// while a game menu such as the inventory or PDA is open: the game hides the mouse cursor while the
/// player is in the world and shows it in its menus.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const double EdgeMargin = 24;

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;
    private const uint LwaAlpha = 2, ModAlt = 1, ModControl = 2, ModNoRepeat = 0x4000;
    private const int WmStyleChanging = 0x7C;
    private const int WmHotKey = 0x312;
    private const int CursorShowing = 0x1;
    private static readonly TimeSpan ForegroundCheckInterval = TimeSpan.FromMilliseconds(150);

    private enum Hotkey
    {
        Toggle = 1,
        ZoomIn,
        ZoomOut,
        Follow,
        Interact,
    }

    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly PositionServer _server;
    private readonly WebView2 _web = new();
    private nint _hwnd;
    private string? _url;
    private bool _interactive;
    private bool _hiddenByUser, _hiddenAway;
    private readonly DispatcherTimer _foregroundCheck;
    private nint _lastForeground;
    private FrontWindow _lastForegroundKind;

    private enum FrontWindow { Other, Game, ThisApp }

    public OverlayWindow(SettingsStore settings, Localizer localizer, PositionServer server)
    {
        _settings = settings;
        _localizer = localizer;
        _server = server;

        Title = "Stalker Vodka GPS";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x0F));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xD2, 0x5B));
        Content = _web;

        _web.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StalkerGPS", "overlay"),
        };
        Loaded += OnLoaded;
        ApplyPlacement();
        _foregroundCheck = new DispatcherTimer(ForegroundCheckInterval, DispatcherPriority.Background, (_, _) => CheckForeground(), Dispatcher);
    }

    /// <summary>
    /// Hides the map while another program is in front, or the game is in front with its cursor
    /// showing, i.e. a menu is open.
    /// </summary>
    private void CheckForeground()
    {
        bool hide = InFront() switch
        {
            FrontWindow.Other => true,
            FrontWindow.Game => !_interactive && CursorShown(),
            _ => false,
        };
        if (hide == _hiddenAway)
            return;
        _hiddenAway = hide;
        UpdateVisibility();
    }

    private static bool CursorShown()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0;
    }

    private FrontWindow InFront()
    {
        var foreground = GetForegroundWindow();
        if (foreground == _lastForeground)
            return _lastForegroundKind;
        _lastForeground = foreground;
        _lastForegroundKind = FrontWindow.Other;
        if (GetWindowThreadProcessId(foreground, out uint pid) == 0)
            return _lastForegroundKind;
        if (pid == Environment.ProcessId)
            return _lastForegroundKind = FrontWindow.ThisApp;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (_settings.Current.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                _lastForegroundKind = FrontWindow.Game;
        }
        catch (ArgumentException)
        {
            // The process has exited.
        }
        return _lastForegroundKind;
    }

    private void UpdateVisibility() => Visibility = _hiddenByUser || _hiddenAway ? Visibility.Hidden : Visibility.Visible;

    /// <summary>Applies the current settings and language.</summary>
    public void ApplySettings()
    {
        ApplyPlacement();
        if (_hwnd != 0)
            ApplyWindowStyle();
        Navigate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        core.PermissionRequested += OnPermissionRequested;
        core.NavigationStarting += OnNavigationStarting;
        // The window only ever shows the map.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        Navigate();
    }

    private void Navigate()
    {
        if (_web.CoreWebView2 is not { } core)
            return;
        string url = MapSite.OverlayUrl(_settings.Current.WebSocketPort, _localizer.Code);
        if (url == _url)
            return;
        _url = url;
        core.Navigate(url);
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(MapSite.Origin + "/", StringComparison.OrdinalIgnoreCase))
            e.Cancel = true;
    }

    // Browsers ask before a website may reach localhost, under a kind WebView2 has no name for.
    // Nothing else is granted, and nobody could answer a prompt in a click-through window.
    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = e.PermissionKind == CoreWebView2PermissionKind.UnknownPermission && e.Uri.StartsWith(MapSite.Origin + "/", StringComparison.OrdinalIgnoreCase)
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;

    private void ApplyPlacement()
    {
        var settings = _settings.Current;
        Width = Height = settings.OverlaySize;
        var area = SystemParameters.WorkArea;
        Left = settings.OverlayCorner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.Left + EdgeMargin
            : area.Right - settings.OverlaySize - EdgeMargin;
        Top = settings.OverlayCorner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Top + EdgeMargin
            : area.Bottom - settings.OverlaySize - EdgeMargin;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        ApplyWindowStyle();
        _foregroundCheck.Start();
        RegisterHotKey(_hwnd, (int)Hotkey.Toggle, ModControl | ModAlt | ModNoRepeat, 0x74);   // Ctrl+Alt+F5
        RegisterHotKey(_hwnd, (int)Hotkey.ZoomIn, ModControl | ModAlt, 0x75);                 // Ctrl+Alt+F6
        RegisterHotKey(_hwnd, (int)Hotkey.ZoomOut, ModControl | ModAlt, 0x76);                // Ctrl+Alt+F7
        RegisterHotKey(_hwnd, (int)Hotkey.Follow, ModControl | ModAlt | ModNoRepeat, 0x77);   // Ctrl+Alt+F8
        RegisterHotKey(_hwnd, (int)Hotkey.Interact, ModControl | ModAlt | ModNoRepeat, 0x78); // Ctrl+Alt+F9
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WPF removes the layered style from any window without per-pixel transparency, which can't
        // host WebView2, and leaves it out when it writes its own styles. So every change keeps the
        // style, and this message doesn't reach WPF. Its STYLESTRUCT holds the old style, then the new.
        if (message == WmStyleChanging && (int)wParam == GwlExStyle)
        {
            Marshal.WriteInt32(lParam, sizeof(int), Marshal.ReadInt32(lParam, sizeof(int)) | (int)WsExLayered);
            handled = true;
            return 0;
        }
        if (message != WmHotKey)
            return 0;
        switch ((Hotkey)(int)wParam)
        {
            case Hotkey.Toggle:
                _hiddenByUser = !_hiddenByUser;
                UpdateVisibility();
                break;
            case Hotkey.ZoomIn:
                _server.SendMapCommand("zoom", delta: 1);
                break;
            case Hotkey.ZoomOut:
                _server.SendMapCommand("zoom", delta: -1);
                break;
            case Hotkey.Follow:
                _server.SendMapCommand("followPlayer", _server.MapControls?.Find("followPlayer")?.IsOn != true);
                break;
            case Hotkey.Interact:
                _interactive = !_interactive;
                ApplyWindowStyle();
                break;
        }
        handled = true;
        return 0;
    }

    /// <summary>Sets the window's opacity and whether clicks pass through.</summary>
    private void ApplyWindowStyle()
    {
        long style = GetWindowLongPtr(_hwnd, GwlExStyle) | WsExLayered | WsExToolWindow;
        style = _interactive ? style & ~(WsExTransparent | WsExNoActivate) : style | WsExTransparent | WsExNoActivate;
        SetWindowLongPtr(_hwnd, GwlExStyle, (nint)style);
        SetLayeredWindowAttributes(_hwnd, 0, (byte)(Math.Clamp(_settings.Current.OverlayOpacity, 10, 100) * 255 / 100), LwaAlpha);
        // A border shows that the mouse is on the map, not the game.
        BorderThickness = new Thickness(_interactive ? 2 : 0);
    }

    protected override void OnClosed(EventArgs e)
    {
        _foregroundCheck.Stop();
        _web.Dispose();
        base.OnClosed(e);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public int X, Y;
    }
}
