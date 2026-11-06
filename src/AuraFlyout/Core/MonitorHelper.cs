using System.Runtime.InteropServices;

namespace AuraFlyout.Core;

public sealed record MonitorInfo(IntPtr Handle, Native.RECT Bounds, Native.RECT WorkArea, bool IsPrimary, string DeviceName, double Scale);

/// <summary>Physical-pixel monitor helpers (per-monitor DPI aware).</summary>
public static class MonitorHelper
{
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var info = Describe(h);
            if (info != null) list.Add(info);
            return true;
        }, IntPtr.Zero);

        // primary first, then left-to-right
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    public static MonitorInfo Primary() =>
        Describe(Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY)) ?? GetAll()[0];

    public static MonitorInfo UnderCursor()
    {
        Native.GetCursorPos(out var p);
        return Describe(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST)) ?? Primary();
    }

    public static MonitorInfo FromWindow(IntPtr hwnd) =>
        Describe(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)) ?? Primary();

    /// <summary>Monitor chosen in settings for flyouts.</summary>
    public static MonitorInfo ForFlyouts() =>
        SettingsStore.Current.FlyoutMonitor == 1 ? UnderCursor() : Primary();

    private static MonitorInfo? Describe(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(handle, ref mi)) return null;

        double scale = 1.0;
        if (Native.GetDpiForMonitor(handle, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        return new MonitorInfo(handle, mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, mi.szDevice, scale);
    }
}
