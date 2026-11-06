using AuraFlyout.Core;

namespace AuraFlyout.Audio;

/// <summary>
/// Hides / restores Windows 11's built-in volume &amp; media overlay so it does not
/// appear on top of our own flyout. The overlay lives in a shell XAML island window.
/// </summary>
public static class NativeOsd
{
    private static IntPtr _osd;
    private static int _originalExStyle;

    public static bool IsHidden { get; private set; }

    public static void Hide()
    {
        try
        {
            if (_osd == IntPtr.Zero || !Native.IsWindow(_osd))
            {
                _osd = Find();
                if (_osd == IntPtr.Zero) return;
                _originalExStyle = Native.GetWindowLong(_osd, Native.GWL_EXSTYLE);
            }

            Native.SetWindowLong(_osd, Native.GWL_EXSTYLE, _originalExStyle | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT);
            Native.SetWindowPos(_osd, IntPtr.Zero, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Native.ShowWindow(_osd, Native.SW_SHOWMINNOACTIVE);
            IsHidden = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Native OSD could not be hidden", ex);
        }
    }

    public static void Restore()
    {
        if (_osd == IntPtr.Zero || !IsHidden) return;
        try
        {
            if (Native.IsWindow(_osd))
            {
                Native.SetWindowLong(_osd, Native.GWL_EXSTYLE, _originalExStyle);
                Native.SetWindowPos(_osd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Native.ShowWindow(_osd, Native.SW_SHOWNOACTIVATE);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Native OSD could not be restored", ex);
        }
        IsHidden = false;
    }

    private static IntPtr Find()
    {
        IntPtr island = IntPtr.Zero;
        while ((island = Native.FindWindowEx(IntPtr.Zero, island, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
        {
            IntPtr bridge = Native.FindWindowEx(island, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource");
            if (bridge == IntPtr.Zero) continue;

            IntPtr input = Native.FindWindowEx(bridge, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
            if (input == IntPtr.Zero) continue;

            // the overlay island has a real (non-empty) input site once it was shown, and is small: the
            // same kind of island also hosts Alt+Tab (screen-sized, an empty input site at the work area's
            // corner), which must never be touched
            if (!Native.GetWindowRect(input, out var r) || r.Width <= 0 || r.Height <= 0) continue;
            if (!Native.GetWindowRect(island, out var box) || box.Width > 900 || box.Height > 500) continue;
            return island;
        }
        return IntPtr.Zero;
    }
}
