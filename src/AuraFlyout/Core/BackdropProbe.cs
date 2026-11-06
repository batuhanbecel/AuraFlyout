using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace AuraFlyout.Core;

/// <summary>Development: small windows that each try one way of getting a blurred backdrop.</summary>
public sealed class BackdropProbe : Window
{
    private readonly int _variant;

    public BackdropProbe(int variant, string label, double left, double top)
    {
        _variant = variant;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 300;
        Height = 180;
        Left = left;
        Top = top;
        Background = Brushes.Transparent;
        if (variant == 1) AllowsTransparency = true;
        if (variant is 0 or 2 or 3 or 4 or 5 or 6)
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        if (variant == 4)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN;
            Height = double.NaN;
        }

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap },
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.MakeToolWindow(hwnd);
        WindowEffects.SetDarkMode(hwnd, true);
        WindowEffects.SetRoundedCorners(hwnd);
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } t }) t.BackgroundColor = Colors.Transparent;

        switch (_variant)
        {
            case 0: // WindowChrome glass + SWCA acrylic (current code)
            case 1: // layered + SWCA acrylic
                WindowEffects.SetAccentForProbe(hwnd, 4, 0x99202020);
                break;
            case 2: // WindowChrome glass + DWM transient acrylic
            {
                int v = 3;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
                Native.SendMessage(hwnd, Native.WM_NCACTIVATE, new IntPtr(1), new IntPtr(-1));
                break;
            }
            case 3: // WindowChrome glass + SWCA blur-behind (no tint)
                WindowEffects.SetAccentForProbe(hwnd, 3, 0);
                break;
            case 4: // like 2 but sized to content
            case 6: // like 2, then WindowEffects.ApplyBackdrop (the real code path)
            {
                int v = 3;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
                Native.SendMessage(hwnd, Native.WM_NCACTIVATE, new IntPtr(1), new IntPtr(-1));
                if (_variant == 6) WindowEffects.ApplyBackdrop(this, 0);
                break;
            }
            case 5: // like 2 + explicit DwmExtendFrameIntoClientArea(-1)
            {
                var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                int v = 3;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
                Native.SendMessage(hwnd, Native.WM_NCACTIVATE, new IntPtr(1), new IntPtr(-1));
                break;
            }
        }
    }
}
