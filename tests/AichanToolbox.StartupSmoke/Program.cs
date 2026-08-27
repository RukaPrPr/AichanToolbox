using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using AichanToolbox;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Construct visuals without showing windows, creating WebView2 profiles,
        // moving a pointer, or automating the desktop.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var splash = new StartupWindow();
        var main = new MainWindow(Array.Empty<string>());
        try
        {
            Require(splash.AllowsTransparency && splash.WindowStyle == WindowStyle.None, "Splash must support per-pixel alpha from creation.");
            Require(splash.Background is SolidColorBrush { Color.A: 0 }, "Splash clear background must be transparent.");
            Require(WindowChrome.GetWindowChrome(splash) is null, "Splash must not switch native frame composition during exit.");
            var surface = (Border)splash.FindName("StartupSurface");
            Require(surface.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(0xEE, 0xF1, 0xF4), "Splash background differs from the light page.");

            var completed = 0;
            splash.RevealAndClose(() => completed++);
            splash.RevealAndClose(() => completed++);
            Require(surface.HasAnimatedProperties, "Exit must animate the content surface.");
            Require(!splash.HasAnimatedProperties && splash.Opacity == 1, "Exit must leave native window opacity unchanged.");
            Require(completed == 0, "Handoff must not complete before the fade.");
            surface.BeginAnimation(UIElement.OpacityProperty, null);

            Require(!main.AllowsTransparency && main.Opacity == 1, "Main window must stay opaque.");
            var browser = (WebView2)main.FindName("Browser");
            Require(browser.DefaultBackgroundColor == System.Drawing.Color.FromArgb(0xEE, 0xF1, 0xF4), "Browser initial background must be light.");
            Require(browser.CoreWebView2 is null, "Headless smoke must not initialize a browser profile.");
            Console.WriteLine("STARTUP_SMOKE_OK splash-per-pixel=true visual-only-fade=true main-opaque=true browser-light=true no-ui-shown=true");
        }
        finally
        {
            main.Close();
            splash.Close();
            application.Shutdown();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
