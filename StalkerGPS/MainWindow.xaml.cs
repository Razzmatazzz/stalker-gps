using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using StalkerGPS.Localization;
using StalkerGPS.Services;
using Forms = System.Windows.Forms;

namespace StalkerGPS;

public partial class MainWindow : Window
{
    private readonly ServiceProvider _services;
    // Keep in step with .sv-shell-tray in app.css.
    private const double TrayWidth = 300;
    private const double AppMinWidth = 480;

    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly Forms.ToolStripMenuItem _trayShow = new();
    private readonly Forms.ToolStripMenuItem _trayExit = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private OverlayWindow? _overlay;
    private bool _trayOpen;
    private bool _compact;
    private double _appWidth;

    public MainWindow()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddMudServices();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<Localizer>();
        services.AddSingleton<PositionTracker>();
        services.AddSingleton<WorldStateTracker>();
        services.AddSingleton<ArtifactTracker>();
        services.AddSingleton<QuestTracker>();
        services.AddSingleton<BarricadeTracker>();
        services.AddSingleton<PositionServer>();
        services.AddSingleton<MapControlMemory>();

        _services = services.BuildServiceProvider();
        Resources.Add("services", _services);

        InitializeComponent();
        SourceInitialized += (_, _) => TitleBar.Apply(this, border: "#303329");

        _settings = _services.GetRequiredService<SettingsStore>();
        _settings.Saved += () => Dispatcher.Invoke(OnSettingsSaved);
        _trayIcon = CreateTrayIcon();
        _localizer = _services.GetRequiredService<Localizer>();
        _localizer.Changed += () => Dispatcher.Invoke(ApplyLanguage);
        ApplyLanguage();
        ApplyTraySetting();
        ApplyMapTraySetting();

        if (_settings.Current.StartMinimized)
            StartMinimized();
        else
            SourceInitialized += (_, _) => ApplySavedPosition();

        // Tracking and the map server run for the app's whole lifetime; tracking waits for the game on its own.
        _services.GetRequiredService<PositionTracker>().Start();
        _services.GetRequiredService<WorldStateTracker>().Start();
        _services.GetRequiredService<ArtifactTracker>().Start();
        _services.GetRequiredService<QuestTracker>().Start();
        _services.GetRequiredService<BarricadeTracker>().Start();
        _services.GetRequiredService<MapControlMemory>();
        _services.GetRequiredService<PositionServer>().Start();
        ApplyOverlaySetting();
    }

    private void OnSettingsSaved()
    {
        ApplyTraySetting();
        var server = _services.GetRequiredService<PositionServer>();
        if (server.Port != _settings.Current.WebSocketPort)
            server.Start();
        ApplyOverlaySetting();
        ApplyMapTraySetting();
    }

    /// <summary>
    /// Sizes the window for the map controls tray: wider while it's out, and only as wide as the tray
    /// when it's the only thing showing. Going to and from that keeps the tray where it is on screen.
    /// </summary>
    private void ApplyMapTraySetting()
    {
        bool tray = _settings.Current.OverlayEnabled;
        bool compact = tray && _settings.Current.TrayCompact;
        if (tray == _trayOpen && compact == _compact)
            return;

        // The tray is laid out for its own width, so it can't share a maximized window's sizing.
        if (compact && WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;

        // Read first: resizing can move the window's center onto another monitor.
        var area = CurrentWorkArea();
        double oldWidth = Width;
        double appWidth = _compact ? _appWidth : Width - (_trayOpen ? TrayWidth : 0);
        bool compactChanged = compact != _compact;
        _appWidth = appWidth;
        _trayOpen = tray;
        _compact = compact;
        MinWidth = compact ? TrayWidth : AppMinWidth + (tray ? TrayWidth : 0);
        Width = compact ? TrayWidth : appWidth + (tray ? TrayWidth : 0);

        // Not placed on screen yet at startup.
        if (double.IsNaN(Left))
            return;
        if (compactChanged)
            Left += oldWidth - Width;
        Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
    }

    /// <summary>The usable part of the monitor the window is on, in the units of Left and Width.</summary>
    private Rect CurrentWorkArea() =>
        PresentationSource.FromVisual(this)?.CompositionTarget is null
            ? SystemParameters.WorkArea
            : WorkArea(Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle));

    private Rect WorkArea(Forms.Screen screen)
    {
        var area = screen.WorkingArea;
        var fromDevice = PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice;
        return new Rect(
            fromDevice.Transform(new Point(area.Left, area.Top)),
            fromDevice.Transform(new Point(area.Right, area.Bottom)));
    }

    /// <summary>
    /// Puts the window back where it last closed. If that monitor has gone or moved, the title bar
    /// would not be on any screen, so the window stays where Windows placed it.
    /// </summary>
    private void ApplySavedPosition()
    {
        if (_settings.Current.WindowLeft is not { } left || _settings.Current.WindowTop is not { } top)
            return;

        var titleBar = new Point(left + Width / 2, top + 16);
        var screens = Forms.Screen.AllScreens.Select(WorkArea).ToList();
        int index = screens.FindIndex(s => s.Contains(titleBar));
        if (index >= 0)
        {
            var area = screens[index];
            Left = Math.Max(area.Left, Math.Min(left, area.Right - Width));
            Top = Math.Max(area.Top, Math.Min(top, area.Bottom - Height));
        }
    }

    private void ApplyOverlaySetting()
    {
        if (_settings.Current.OverlayEnabled && _overlay is null)
        {
            _overlay = new OverlayWindow(_settings, _localizer, _services.GetRequiredService<PositionServer>());
            _overlay.Show();
        }
        else if (!_settings.Current.OverlayEnabled && _overlay is not null)
        {
            // Closed rather than hidden: the map takes a lot of memory.
            _overlay.Close();
            _overlay = null;
        }
        else
        {
            _overlay?.ApplySettings();
        }
    }

    /// <summary>
    /// WebView2 can't create its renderer in a window that is minimized when it loads, so the window
    /// loads off-screen without taking focus, then minimizes (or hides to the tray) once it has rendered.
    /// </summary>
    private void StartMinimized()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        ShowActivated = false;
        ShowInTaskbar = false;

        ContentRendered += OnFirstRender;
        void OnFirstRender(object? sender, EventArgs e)
        {
            ContentRendered -= OnFirstRender;
            if (_settings.Current.MinimizeToTray)
                Hide();
            WindowState = WindowState.Minimized;
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            ApplySavedPosition();
            ShowInTaskbar = true;
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _trayShow.Click += (_, _) => RestoreFromTray();
        _trayExit.Click += (_, _) => Close();
        menu.Items.Add(_trayShow);
        menu.Items.Add(_trayExit);

        var icon = new Forms.NotifyIcon
        {
            Text = Title,
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    /// <summary>
    /// The site favicon at the notification area's icon size (16px at 100% scaling). Its images are
    /// PNG-compressed, which System.Drawing.Icon decodes incorrectly at small sizes, so the PNG for
    /// the closest size is decoded directly and turned into an icon.
    /// </summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("StalkerGPS.app.ico")!;
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);

        // ICONDIR: 6-byte header, then 16-byte entries (width, height, ..., size @8, offset @12).
        int wanted = Forms.SystemInformation.SmallIconSize.Width;
        var entries = Enumerable.Range(0, BitConverter.ToUInt16(ico, 4))
            .Select(i => (Width: ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16], Entry: 6 + i * 16))
            .OrderBy(e => e.Width)
            .ToList();
        // The smallest image at least as large as wanted (scaled down if needed), else the largest.
        var best = entries.FirstOrDefault(e => e.Width >= wanted, entries[^1]);
        int size = BitConverter.ToInt32(ico, best.Entry + 8);
        int offset = BitConverter.ToInt32(ico, best.Entry + 12);

        using var image = new System.Drawing.Bitmap(new MemoryStream(ico, offset, size));
        using var scaled = new System.Drawing.Bitmap(image, wanted, wanted);
        // The handle is kept for the app's lifetime, so it is never destroyed.
        return System.Drawing.Icon.FromHandle(scaled.GetHicon());
    }

    /// <summary>The text outside the web view. The title is the app's name, so it isn't translated.</summary>
    private void ApplyLanguage()
    {
        var text = _localizer.T;
        MinimizeButton.ToolTip = text.Minimize;
        CloseWindowButton.ToolTip = text.Close;
        UpdateMaximizeButton();
        _trayShow.Text = text.TrayShow;
        _trayExit.Text = text.TrayExit;
        _trayIcon.ContextMenuStrip!.RightToLeft = _localizer.IsRightToLeft ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
        _overlay?.ApplySettings();
    }

    private void UpdateMaximizeButton()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? _localizer.T.Restore : _localizer.T.Maximize;
    }

    private void ApplyTraySetting()
    {
        _trayIcon.Visible = _settings.Current.MinimizeToTray;
        // Turning the setting off while hidden would leave no way back to the window.
        if (!_settings.Current.MinimizeToTray && IsLoaded && !IsVisible)
            RestoreFromTray();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        HideToTrayIfEnabled();

        UpdateMaximizeButton();
        // Without a native frame, a maximized window extends past the screen edge by the
        // resize border; pad the content so nothing is cut off.
        Root.Margin = WindowState == WindowState.Maximized ? TitleBar.MaximizedFrameThickness() : new Thickness(0);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void HideToTrayIfEnabled()
    {
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
            Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // Not in OnClosed: RestoreBounds is empty by then.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var position = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        // A window that started minimized and closed before it rendered has no real position yet.
        if (!position.IsEmpty && position.Left > -32000)
            _settings.RememberWindowPosition(position.Left, position.Top);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _overlay?.Close();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _services.Dispose();
        base.OnClosed(e);
    }
}
