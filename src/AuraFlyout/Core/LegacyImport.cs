using System.Diagnostics;
using Microsoft.Win32;

namespace AuraFlyout.Core;

/// <summary>
/// One-time import of the preferences of the standalone TaskbarStatus, TaskbarLogiBattery and
/// TaskbarSpeedTest apps, plus helpers to retire those apps now that AuraFlyout contains them.
/// </summary>
public static class LegacyImport
{
    public static readonly string[] ProcessNames = ["TaskbarStatus", "TaskbarLogiBattery", "TaskbarSpeedTest"];

    public static void RunOnce()
    {
        var s = SettingsStore.Current;
        if (s.ModulesImported) return;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TaskbarStatus"))
            {
                if (k != null)
                {
                    s.StatusShowCpu = Bool(k, "ShowCpu", s.StatusShowCpu);
                    s.StatusShowCpuTemp = Bool(k, "ShowCpuTemp", s.StatusShowCpuTemp);
                    s.StatusShowRam = Bool(k, "ShowRam", s.StatusShowRam);
                    s.StatusShowGpu = Bool(k, "ShowGpu", s.StatusShowGpu);
                    s.StatusShowGpuTemp = Bool(k, "ShowGpuTemp", s.StatusShowGpuTemp);
                    s.StatusShowFps = Bool(k, "ShowFps", s.StatusShowFps);
                    s.StatusShowNet = Bool(k, "ShowNet", s.StatusShowNet);
                    s.StatusTwoLines = Bool(k, "TwoLines", s.StatusTwoLines);
                    s.StatusBold = Bool(k, "Bold", s.StatusBold);
                    s.StatusShowLabels = Bool(k, "ShowLabels", s.StatusShowLabels);
                    s.StatusShowGlyphs = Bool(k, "ShowGlyphs", s.StatusShowGlyphs);
                    s.StatusColorWarnings = Bool(k, "ColorWarnings", s.StatusColorWarnings);
                    s.StatusRamAsGb = Bool(k, "RamAsGb", s.StatusRamAsGb);
                    s.StatusFontSize = Math.Clamp(Int(k, "FontSize", s.StatusFontSize), 9, 18);
                    s.StatusInterval = Math.Clamp(Int(k, "Interval", s.StatusInterval), 250, 10000);
                    s.StatusAnchor = Int(k, "Anchor", 0) == 1 ? 0 : 2; // 0 = next to the tray, 1 = left edge
                }
            }

            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TaskbarLogiBattery"))
            {
                if (k != null)
                {
                    s.LogiNotifyConnect = Bool(k, "NotifyConnect", s.LogiNotifyConnect);
                    s.LogiNotifyDisconnect = Bool(k, "NotifyDisconnect", s.LogiNotifyDisconnect);
                    s.LogiNotifyLow = Bool(k, "NotifyLow", s.LogiNotifyLow);
                    s.LogiNotifyFull = Bool(k, "NotifyFull", s.LogiNotifyFull);
                    s.LogiLowThreshold = Math.Clamp(Int(k, "LowThreshold", s.LogiLowThreshold), 5, 30);
                    s.LogiPollSeconds = Math.Clamp(Int(k, "PollSeconds", s.LogiPollSeconds), 15, 600);
                    s.LogiAnchor = Int(k, "WidgetAnchor", 0) == 1 ? 0 : 2;
                    if (k.GetValue("Hidden") is string hidden)
                        foreach (string id in hidden.Split(';', StringSplitOptions.RemoveEmptyEntries))
                            if (!s.LogiHidden.Contains(id)) s.LogiHidden.Add(id);
                }
            }

            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TaskbarSpeedTest"))
            {
                if (k != null)
                {
                    s.SpeedWidgetDownload = Bool(k, "WidgetDownload", s.SpeedWidgetDownload);
                    s.SpeedWidgetUpload = Bool(k, "WidgetUpload", s.SpeedWidgetUpload);
                    s.SpeedWidgetPing = Bool(k, "WidgetPing", s.SpeedWidgetPing);
                    s.SpeedWidgetUnits = Bool(k, "WidgetUnits", s.SpeedWidgetUnits);
                    s.SpeedTwoLines = Bool(k, "TwoLines", s.SpeedTwoLines);
                    s.SpeedUnit = Math.Clamp(Int(k, "Unit", s.SpeedUnit), 0, 1);
                    s.SpeedSeconds = Math.Clamp(Int(k, "Seconds", s.SpeedSeconds), 5, 15);
                    s.SpeedAutoTestHours = Math.Clamp(Int(k, "AutoTestHours", s.SpeedAutoTestHours), 0, 24);
                    s.SpeedAnchor = Int(k, "WidgetAnchor", 0) == 1 ? 0 : 2;
                }
            }
            Log.Info("Imported settings of the standalone taskbar apps");
        }
        catch (Exception ex)
        {
            Log.Warn("Legacy settings import failed", ex);
        }
        s.ModulesImported = true;
    }

    /// <summary>Standalone apps that are running right now (they would draw a second copy on the taskbar).</summary>
    public static List<string> RunningApps()
    {
        var list = new List<string>();
        foreach (string name in ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) list.Add(name);
            foreach (var p in procs) p.Dispose();
        }
        return list;
    }

    /// <summary>
    /// Closes the standalone apps and removes their per-user autostart entries.
    /// TaskbarStatus' elevated scheduled task needs admin rights and is only removed when we have them.
    /// </summary>
    public static void RetireStandaloneApps()
    {
        foreach (string name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not close {name}", ex);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run != null)
                foreach (string name in ProcessNames)
                    run.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove Run entries", ex);
        }

        if (Elevation.IsAdmin)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /TN TaskbarStatus /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not remove the TaskbarStatus task", ex);
            }
        }
    }

    private static bool Bool(RegistryKey k, string name, bool fallback) => k.GetValue(name) is int v ? v != 0 : fallback;
    private static int Int(RegistryKey k, string name, int fallback) => k.GetValue(name) is int v ? v : fallback;
}
