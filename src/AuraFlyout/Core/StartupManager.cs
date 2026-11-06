using Microsoft.Win32;

namespace AuraFlyout.Core;

/// <summary>
/// "Start with Windows": a HKCU\...\Run entry normally, or the logon trigger of the elevated
/// scheduled task when AuraFlyout runs as administrator (see <see cref="Elevation"/>).
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AuraFlyout";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is string value && Environment.ProcessPath is string path &&
                    value.Contains(path, StringComparison.OrdinalIgnoreCase))
                    return true;
                return SettingsStore.Current.RunElevated && Elevation.TaskHasLogonTrigger();
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (SettingsStore.Current.RunElevated)
            {
                // the elevated task's logon trigger starts us; the Run entry would start a second, unelevated copy
                SetRunKey(false);
                string mode = enabled ? "on" : "nologon";
                if (Elevation.IsAdmin) Elevation.Register(enabled);
                else if (!Elevation.RunElevatedAndWait($"{Elevation.RegisterArg} {mode}")) SetRunKey(enabled); // UAC declined
                return;
            }
            SetRunKey(enabled);
        }
        catch (Exception ex)
        {
            Log.Error("Startup registration failed", ex);
        }
    }

    public static void SetRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
