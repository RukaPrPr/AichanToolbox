using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace AichanToolbox;

public partial class StartupWindow : Window
{
    private const double DefaultWidth = 1520;
    private const double DefaultHeight = 940;
    private const double MinimumWidth = 760;
    private const double MinimumHeight = 560;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCornerRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private bool _closing;

    public StartupWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeFrame();
    }

    public void MatchOwnerBounds(Window owner)
    {
        try { Owner = owner; }
        catch (InvalidOperationException)
        {
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            if (ownerHandle != IntPtr.Zero) new WindowInteropHelper(this).Owner = ownerHandle;
        }
        Left = owner.Left;
        Top = owner.Top;
        Width = Math.Max(1, owner.ActualWidth);
        Height = Math.Max(1, owner.ActualHeight);
        Topmost = false;
    }

    public void MatchInitialBounds(string[] arguments)
    {
        var width = DefaultWidth;
        var height = DefaultHeight;
        var sizeIndex = Array.FindIndex(arguments, value => value.Equals("--window-size", StringComparison.OrdinalIgnoreCase));
        if (sizeIndex >= 0 && sizeIndex + 2 < arguments.Length &&
            double.TryParse(arguments[sizeIndex + 1], out var requestedWidth) &&
            double.TryParse(arguments[sizeIndex + 2], out var requestedHeight))
        {
            width = Math.Max(MinimumWidth, requestedWidth);
            height = Math.Max(MinimumHeight, requestedHeight);
        }

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(width, workArea.Width);
        Height = Math.Min(height, workArea.Height);
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    public void RevealAndClose(Action completed)
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation
        {
            From = Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch (InvalidOperationException) { }
            completed();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void ApplyNativeFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var cornerPreference = DwmCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));
            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref borderColor, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
