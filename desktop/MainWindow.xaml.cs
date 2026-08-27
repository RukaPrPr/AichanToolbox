using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AichanToolbox.Core;
using Microsoft.Web.WebView2.Core;

namespace AichanToolbox;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCornerDoNotRound = 1;
    private const int DwmCornerRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    private readonly string[] _arguments;
    private readonly AppearanceSettings _appearance;
    private DesktopBridge? _bridge;
    private HwndSource? _windowSource;
    private StartupWindow? _startupWindow;
    private bool _frontendRevealStarted;
    private bool _closed;
    private readonly TaskCompletionSource<bool> _frontendReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow(string[] arguments) : this(arguments, new AppearanceSettings())
    {
    }

    internal MainWindow(string[] arguments, AppearanceSettings appearance)
    {
        _arguments = arguments;
        _appearance = appearance;
        InitializeComponent();
        ApplyTheme(_appearance.Current);
        ApplyRequestedWindowSize();
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitializeBrowserAsync();
        StateChanged += (_, _) =>
        {
            ApplyNativeWindowFrame();
            _bridge?.NotifyWindowStateChanged();
        };
        Closed += OnClosed;
    }

    public void AttachStartupWindow(StartupWindow startupWindow)
    {
        _startupWindow = startupWindow;
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        StartupTelemetry.Mark("wpf.sourceInitialized");
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        if (_windowSource?.CompositionTarget is { } target)
            target.BackgroundColor = _appearance.Current.SurfaceColor;
        _windowSource?.AddHook(WindowMessageHook);
        ApplyNativeWindowFrame();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _closed = true;
        _frontendReadySignal.TrySetCanceled();
        CloseStartupWindow();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _bridge?.Dispose();
    }

    private void ApplyTheme(ThemeSelection selection)
    {
        if (!selection.IsValid) throw new ArgumentException("无效的主题设置。", nameof(selection));
        var color = selection.SurfaceColor;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Background = brush;
        BrowserHost.Background = brush;
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        if (_windowSource?.CompositionTarget is { } target) target.BackgroundColor = color;
    }

    internal void SetTheme(ThemeSelection selection)
    {
        if (_closed) return;
        ApplyTheme(selection);
        _appearance.Save(selection);
    }

    private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest && WindowState == WindowState.Normal && lParam != IntPtr.Zero)
            return HitTestResizeBorder(window, lParam, ref handled);
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var dpi = Math.Max(96u, GetDpiForWindow(window));
            var scale = dpi / 96d;
            minMaxInfo.MinTrackSize.X = (int)Math.Ceiling(MinWidth * scale);
            minMaxInfo.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * scale);
            minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
            minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
            minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
            minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static IntPtr HitTestResizeBorder(IntPtr window, IntPtr lParam, ref bool handled)
    {
        if (!GetWindowRect(window, out var bounds)) return IntPtr.Zero;
        var packed = lParam.ToInt64();
        var x = unchecked((short)(packed & 0xffff));
        var y = unchecked((short)((packed >> 16) & 0xffff));
        var dpi = GetDpiForWindow(window);
        var border = Math.Max(10, (int)Math.Round(10 * Math.Max(96u, dpi) / 96d));
        var left = x >= bounds.Left && x < bounds.Left + border;
        var right = x < bounds.Right && x >= bounds.Right - border;
        var top = y >= bounds.Top && y < bounds.Top + border;
        var bottom = y < bounds.Bottom && y >= bounds.Bottom - border;

        var hit = top && left ? HtTopLeft
            : top && right ? HtTopRight
            : bottom && left ? HtBottomLeft
            : bottom && right ? HtBottomRight
            : left ? HtLeft
            : right ? HtRight
            : top ? HtTop
            : bottom ? HtBottom
            : 0;
        if (hit == 0) return IntPtr.Zero;
        handled = true;
        return new IntPtr(hit);
    }

    private void ApplyNativeWindowFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            // Let DWM clip the real HWND instead of revealing the rectangular WPF/WebView2 host
            // behind a second CSS border radius. Maximized windows stay flush with the work area.
            var cornerPreference = WindowState == WindowState.Maximized
                ? DwmCornerDoNotRound
                : DwmCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));

            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref borderColor, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows versions do not expose these DWM attributes.
        }
        catch (EntryPointNotFoundException)
        {
            // Keep the square native frame rather than failing application startup.
        }
    }

    private void ApplyRequestedWindowSize()
    {
        var sizeIndex = Array.FindIndex(_arguments, value => value.Equals("--window-size", StringComparison.OrdinalIgnoreCase));
        if (sizeIndex < 0 || sizeIndex + 2 >= _arguments.Length) return;
        if (!double.TryParse(_arguments[sizeIndex + 1], out var width) || !double.TryParse(_arguments[sizeIndex + 2], out var height)) return;
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            StartupTelemetry.Mark("webview.initialize.start");
            var screenshotMode = Array.Exists(_arguments, value => value.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
            var userData = screenshotMode
                ? Path.Combine(Path.GetTempPath(), "AichanToolbox7_WebView2_Test", Environment.ProcessId.ToString())
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AichanToolbox", "WebView2");
            StartupTelemetry.SetWebViewProfile(Directory.Exists(userData));
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            if (_closed) return;
            StartupTelemetry.Mark("webview.environmentCreated");
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            StartupTelemetry.Mark("webview.controlReady");

            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!File.Exists(Path.Combine(webRoot, "index.html")))
                throw new FileNotFoundException("前端资源缺失，请先构建 Vue 前端。", Path.Combine(webRoot, "index.html"));
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "aichan.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _bridge = new DesktopBridge(this, Browser,
                () => QueueWindowInteraction(BeginWindowDrag),
                edge => QueueWindowInteraction(() => BeginWindowResize(edge)),
                RevealFrontend, _appearance, SetTheme);
            StartupTelemetry.Mark("bridge.constructed");
            Browser.CoreWebView2.WebMessageReceived += _bridge.Receive;
            Browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith("https://aichan.local/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                StartupTelemetry.Mark(args.IsSuccess ? "webview.navigationCompleted" : "webview.navigationFailed");
                navigation.TrySetResult(args.IsSuccess);
            };
            StartupTelemetry.Mark("webview.navigationStarted");
            Browser.CoreWebView2.Navigate("https://aichan.local/index.html");

            var screenshotIndex = Array.FindIndex(_arguments, value => value.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
            if (screenshotIndex >= 0 && screenshotIndex + 1 < _arguments.Length)
            {
                var succeeded = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (!succeeded) throw new InvalidOperationException("WebView2 前端页面加载失败。");
                await _frontendReadySignal.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                if (Array.Exists(_arguments, value => value.Equals("--open-sidebar", StringComparison.OrdinalIgnoreCase)))
                    await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar-host')?.classList.add('drawer-open');document.querySelector('.sidebar')?.classList.add('drawer-open');document.querySelector('.sidebar-toggle')?.classList.add('active');document.querySelector('.sidebar-drawer-scrim')?.classList.add('visible')");
                await Task.Delay(1200);
                var output = Path.GetFullPath(_arguments[screenshotIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                Close();
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (_closed) return;
            StartupTelemetry.Mark("startup.failed.webviewMissing");
            StartupTelemetry.FlushInBackground("webview-runtime-missing");
            CloseStartupWindow();
            System.Windows.MessageBox.Show(this, "系统缺少 Microsoft Edge WebView2 Runtime，请安装后重新启动。", "缺少运行组件", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
        catch (Exception exception)
        {
            if (_closed) return;
            StartupTelemetry.Mark("startup.failed");
            StartupTelemetry.FlushInBackground("startup-failed");
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), exception.ToString()); } catch { }
            CloseStartupWindow();
            System.Windows.MessageBox.Show(this, exception.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void RevealFrontend()
    {
        if (_closed || _frontendRevealStarted) return;
        _frontendRevealStarted = true;
        StartupTelemetry.Mark("frontend.readyReceived");

        // Leave the WebView2 callback before closing/fading an owned window. Frontend
        // readiness comes from rendered frames, not from the WPF dispatcher being idle.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_closed || !IsLoaded) return;
            var startup = _startupWindow;
            if (startup is not null && startup.IsLoaded)
                startup.RevealAndClose(MarkFrontendRevealed);
            else
                MarkFrontendRevealed();
        }));
    }

    private void MarkFrontendRevealed()
    {
        if (_closed) return;
        _startupWindow = null;
        StartupTelemetry.Mark("frontend.revealed");
        _frontendReadySignal.TrySetResult(true);
        StartupTelemetry.FlushInBackground("frontend-revealed");
    }

    private void CloseStartupWindow()
    {
        var startup = _startupWindow;
        _startupWindow = null;
        if (startup is null) return;
        try { startup.Close(); } catch (InvalidOperationException) { }
    }

    private void QueueWindowInteraction(Action interaction)
    {
        if (_closed) return;
        // Native move/resize starts a modal message loop. Let WebMessageReceived
        // return before entering it so WebView2 callbacks cannot be reentered.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_closed) interaction();
        }));
    }

    private void BeginWindowDrag()
    {
        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private void BeginWindowResize(string edge)
    {
        if (WindowState != WindowState.Normal) return;
        var hit = edge.Trim().ToLowerInvariant() switch
        {
            "left" => HtLeft,
            "right" => HtRight,
            "top" => HtTop,
            "topleft" => HtTopLeft,
            "topright" => HtTopRight,
            "bottom" => HtBottom,
            "bottomleft" => HtBottomLeft,
            "bottomright" => HtBottomRight,
            _ => 0
        };
        if (hit == 0) return;
        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, WmNcLeftButtonDown, new IntPtr(hit), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
