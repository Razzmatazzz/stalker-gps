using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using StalkerTracker.Services;

namespace StalkerTracker;

public partial class MainWindow : Window
{
    private readonly ServiceProvider _services;

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

        _services = services.BuildServiceProvider();
        Resources.Add("services", _services);

        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.Dispose();
        base.OnClosed(e);
    }
}
