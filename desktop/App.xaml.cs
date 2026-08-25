using System.Windows;
using System.Runtime.Versioning;
using AichanToolbox.Core;

[assembly: SupportedOSPlatform("windows10.0.17763.0")]

namespace AichanToolbox;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTelemetry.Mark("wpf.onStartup");
        base.OnStartup(e);
        var window = new MainWindow(e.Args);
        StartupTelemetry.Mark("wpf.windowConstructed");
        MainWindow = window;
        window.Show();
        StartupTelemetry.Mark("wpf.windowShown");
        var startup = new StartupWindow();
        startup.MatchOwnerBounds(window);
        window.AttachStartupWindow(startup);
        startup.Show();
        StartupTelemetry.Mark("wpf.startupWindowShown");
    }
}
