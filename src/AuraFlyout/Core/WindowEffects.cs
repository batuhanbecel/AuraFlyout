using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AuraFlyout.Core;

/// <summary>DWM helpers: rounded corners, acrylic blur and system backdrops for borderless windows.</summary>
public static class WindowEffects
{
    public static void MakeToolWindow(IntPtr hwnd, bool noActivate = true)
    {
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW;
        ex &= ~Native.WS_EX_APPWINDOW;
        if (noActivate) ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
    }

    /// <summary>
    /// DWM only draws the soft Windows 11 window shadow (and the hairline border) for windows with a
    /// sizing frame. WindowChrome keeps the client area covering the whole window and its resize
    /// border is 0, so the frame stays invisible and the flyout cannot be resized.
    /// </summary>
    public static void EnableSystemShadow(IntPtr hwnd)
    {
        const int WS_THICKFRAME = 0x00040000;
        const uint SWP_FRAMECHANGED = 0x0020;
        int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        Native.SetWindowLong(hwnd, Native.GWL_STYLE, style | WS_THICKFRAME);
        // a sizing frame gets a caption strip in the accent colour ("show accent colour on title bars"): none
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Native.DwmSetWindowAttribute(hwnd, 35 /*DWMWA_CAPTION_COLOR*/, ref none, sizeof(int));
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetRoundedCorners(IntPtr hwnd, bool small = false)
    {
        int pref = small ? Native.DWMWCP_ROUNDSMALL : Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private static readonly HashSet<IntPtr> AccentWindows = [];

    /// <summary>
    /// Applies the backdrop chosen in settings. Returns true when the window background is
    /// see-through (the content then only adds an optional tint on top).
    /// 0 = Fluent acrylic: the Windows 11 system material (DWMSBT_TRANSIENTWINDOW), the same one the
    ///     shell's own flyouts use; kept in its "active" look by FlyoutWindow's WM_NCACTIVATE handling.
    /// 1 = classic acrylic through SetWindowCompositionAttribute (Windows 10 style, tint = opacity).
    /// 2 = solid.
    /// </summary>
    public static bool ApplyBackdrop(Window window, int mode)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        SetDarkMode(hwnd, ThemeService.IsDark);
        SetRoundedCorners(hwnd);

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;

        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        if (mode == 0)
        {
            // an accent policy, even a "disabled" one, suppresses the system backdrop: only touch it if we set one
            if (AccentWindows.Remove(hwnd)) SetAccent(hwnd, Native.AccentState.Disabled, 0);
            int transient = 3; // DWMSBT_TRANSIENTWINDOW
            if (Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref transient, sizeof(int)) == 0)
                return true;
            mode = 1; // Windows 10: no system backdrops
        }

        int none = 1; // DWMSBT_NONE
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        if (mode == 1)
        {
            SetAccent(hwnd, Native.AccentState.EnableAcrylicBlurBehind, ThemeService.AcrylicTint());
            AccentWindows.Add(hwnd);
            return true;
        }
        if (AccentWindows.Remove(hwnd)) SetAccent(hwnd, Native.AccentState.Disabled, 0);
        return false;
    }

    /// <summary>
    /// No backdrop at all: with the frame extended over the whole window, what WPF leaves transparent
    /// is see-through. Undone by <see cref="ApplyBackdrop"/>.
    /// </summary>
    public static void ClearBackdrop(IntPtr hwnd)
    {
        int none = 1; // DWMSBT_NONE
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        if (AccentWindows.Remove(hwnd)) SetAccent(hwnd, Native.AccentState.Disabled, 0);
    }

    internal static void SetAccentForProbe(IntPtr hwnd, int state, uint tint) => SetAccent(hwnd, (Native.AccentState)state, tint);

    private static void SetAccent(IntPtr hwnd, Native.AccentState state, uint tint)
    {
        var accent = new Native.AccentPolicy
        {
            AccentState = state,
            AccentFlags = state == Native.AccentState.Disabled ? 0 : 2,
            GradientColor = tint,
        };

        int size = Marshal.SizeOf<Native.AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Native.WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
            Native.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
