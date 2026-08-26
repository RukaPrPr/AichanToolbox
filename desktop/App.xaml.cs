using System.Windows;
using System.Runtime.Versioning;
using System.Windows.Threading;
using AichanToolbox.Core;

[assembly: SupportedOSPlatform("windows10.0.17763.0")]

namespace AichanToolbox;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTelemetry.Mark("wpf.onStartup");
        base.OnStartup(e);
        var startup = new StartupWindow();
        startup.MatchInitialBounds(e.Args);
        startup.Show();
        StartupTelemetry.Mark("wpf.startupWindowShown");

        // Yield through the first WPF render pass so the lightweight startup surface is
        // visible before MainWindow construction and WebView2 initialization begin.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var window = new MainWindow(e.Args);
            StartupTelemetry.Mark("wpf.windowConstructed");
            MainWindow = window;
            window.AttachStartupWindow(startup);
            window.Show();
            StartupTelemetry.Mark("wpf.windowShown");
            startup.MatchOwnerBounds(window);
        }));
    }
}
