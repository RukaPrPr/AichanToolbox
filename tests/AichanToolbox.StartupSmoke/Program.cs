using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using AichanToolbox;
using AichanToolbox.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Construct visuals without showing windows, creating WebView2 profiles,
        // moving a pointer, or automating the desktop.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var themeTestRoot = Path.Combine(Path.GetTempPath(), "AichanToolbox.ThemeSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(themeTestRoot);
        var themeTestPath = Path.Combine(themeTestRoot, "appearance.json");
        var appearance = new AppearanceSettings(themeTestPath);
        var splash = new StartupWindow();
        var main = new MainWindow(Array.Empty<string>(), appearance);
        var mainClosed = false;
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
            var dark = new ThemeSelection("graphite-purple", "dark", "#16171b");
            main.SetTheme(dark);
            Require(main.Background is SolidColorBrush darkBrush && darkBrush.Color == dark.SurfaceColor, "Main background must follow the selected theme.");
            var browserHost = (Grid)main.FindName("BrowserHost");
            Require(browserHost.Background is SolidColorBrush hostBrush && hostBrush.Color == dark.SurfaceColor, "WebView host background must follow the theme.");
            Require(browser.DefaultBackgroundColor == System.Drawing.Color.FromArgb(0x16, 0x17, 0x1B), "WebView clear color must follow the theme.");
            Require(new AppearanceSettings(themeTestPath).Current == dark, "Theme preference must survive restart.");
            var darkSplash = new StartupWindow(dark);
            Require(((Border)darkSplash.FindName("StartupSurface")).Background is SolidColorBrush splashBrush && splashBrush.Color == dark.SurfaceColor, "Splash must use the saved theme.");
            darkSplash.Close();

            var future = new ThemeSelection("future-sepia", "dark", "#28211c");
            appearance.Save(future);
            Require(new AppearanceSettings(themeTestPath).Current == future, "New theme IDs must not require native code changes.");
            var invalidRejected = false;
            try { appearance.Save(new ThemeSelection("invalid", "dark", "red")); }
            catch (ArgumentException) { invalidRejected = true; }
            Require(invalidRejected && new AppearanceSettings(themeTestPath).Current == future, "Invalid colors must not overwrite saved preferences.");
            File.WriteAllText(themeTestPath, "{broken");
            Require(new AppearanceSettings(themeTestPath).Current == ThemeSelection.Default, "Corrupt preferences must fall back to light.");
            File.WriteAllText(themeTestPath, "{\"id\":null,\"colorScheme\":\"dark\",\"background\":\"#16171b\"}");
            Require(new AppearanceSettings(themeTestPath).Current == ThemeSelection.Default, "Incomplete preferences must fall back to light.");
            main.SetTheme(ThemeSelection.Default);
            Require(browser.DefaultBackgroundColor == System.Drawing.Color.FromArgb(0xEE, 0xF1, 0xF4), "Switching to light must restore the original clear color.");
            Require(new AppearanceSettings(themeTestPath).Current == ThemeSelection.Default, "Light selection must be persisted.");
            Require(browser.CoreWebView2 is null, "Theme tests must not create a browser profile.");
            Console.WriteLine("THEME_SMOKE_OK persistence=true native-backgrounds=true future-theme=true corrupt-fallback=true");
            var queueInteraction = typeof(MainWindow).GetMethod("QueueWindowInteraction", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Window interaction dispatcher is missing.");
            var callerActive = true;
            var interactionRan = false;
            var ranInsideCaller = false;
            queueInteraction.Invoke(main, [new Action(() =>
            {
                interactionRan = true;
                ranInsideCaller = callerActive;
            })]);
            Require(!interactionRan, "Native interaction must not run inside the requesting callback.");
            callerActive = false;
            DrainDispatcher();
            Require(interactionRan && !ranInsideCaller, "Queued interaction must run after its caller returns.");

            var closedCalls = 0;
            queueInteraction.Invoke(main, [new Action(() => closedCalls++)]);
            main.Close();
            mainClosed = true;
            DrainDispatcher();
            Require(closedCalls == 0, "Closing the window must cancel a pending native interaction.");
            queueInteraction.Invoke(main, [new Action(() => closedCalls++)]);
            DrainDispatcher();
            Require(closedCalls == 0, "A closed window must reject new native interactions.");
            Console.WriteLine("STARTUP_SMOKE_OK splash-per-pixel=true visual-only-fade=true main-opaque=true browser-light=true no-ui-shown=true");
            Console.WriteLine("WINDOW_INTERACTION_SMOKE_OK deferred=true cancelled-on-close=true rejected-after-close=true");
        }
        finally
        {
            if (!mainClosed) main.Close();
            splash.Close();
            application.Shutdown();
            File.Delete(themeTestPath);
            Directory.Delete(themeTestRoot);
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
