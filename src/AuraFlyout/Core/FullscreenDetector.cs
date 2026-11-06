namespace AuraFlyout.Core;

/// <summary>Detects games / videos / presentations running full screen.</summary>
public static class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow"];

    public static bool IsFullscreenAppRunning()
    {
        try
        {
            if (Native.SHQueryUserNotificationState(out int state) == 0)
            {
                // 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE
                if (state is 3 or 4) return true;
            }

            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || !Native.IsWindowVisible(fg)) return false;
            if (ShellClasses.Contains(Native.GetClassName(fg))) return false;

            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Environment.ProcessId) return false;

            if (!Native.GetWindowRect(fg, out var rect)) return false;
            var m = MonitorHelper.FromWindow(fg).Bounds;
            return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
        }
        catch
        {
            return false;
        }
    }
}
