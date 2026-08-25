using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace AichanToolbox;

public partial class StartupWindow : Window
{
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
        Owner = owner;
        Left = owner.Left;
        Top = owner.Top;
        Width = Math.Max(1, owner.ActualWidth);
        Height = Math.Max(1, owner.ActualHeight);
    }

    public void RevealAndClose(Action completed)
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation
        {
            From = Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(170),
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
