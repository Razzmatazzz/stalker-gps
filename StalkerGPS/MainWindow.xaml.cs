using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using StalkerGPS.Services;
using Forms = System.Windows.Forms;

namespace StalkerGPS;

public partial class MainWindow : Window
{
    private readonly ServiceProvider _services;
    private readonly SettingsStore _settings;
    private readonly Forms.NotifyIcon _trayIcon;

    public MainWindow()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddMudServices();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<PositionTracker>();
        services.AddSingleton<PositionServer>();

        _services = services.BuildServiceProvider();
        Resources.Add("services", _services);

        InitializeComponent();
        SourceInitialized += (_, _) => TitleBar.Apply(this, border: "#303329");

        _settings = _services.GetRequiredService<SettingsStore>();
        _settings.Saved += () => Dispatcher.Invoke(OnSettingsSaved);
        _trayIcon = CreateTrayIcon();
        ApplyTraySetting();

        if (_settings.Current.StartMinimized)
            StartMinimized();

        // Tracking and the map server run for the app's whole lifetime; tracking waits for the game on its own.
        _services.GetRequiredService<PositionTracker>().Start();
        _services.GetRequiredService<PositionServer>().Start();
    }

    private void OnSettingsSaved()
    {
        ApplyTraySetting();
        var server = _services.GetRequiredService<PositionServer>();
        if (server.Port != _settings.Current.WebSocketPort)
            server.Start();
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
            ShowInTaskbar = true;
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());

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

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        // Without a native frame, a maximized window extends past the screen edge by the
        // resize border; pad the content so nothing is cut off.
        Root.Margin = maximized ? TitleBar.MaximizedFrameThickness() : new Thickness(0);
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

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _services.Dispose();
        base.OnClosed(e);
    }
}
