using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace AuraFlyout.Core;

/// <summary>
/// A per-user installation like any Windows app: the exe in %LocalAppData%\Programs\AuraFlyout, a Start
/// menu shortcut (carrying the notification app id) and an entry under Settings › Apps › Installed apps
/// that uninstalls it. No administrator rights needed; settings stay in %AppData%\AuraFlyout.
/// </summary>
public static class Installer
{
    public const string FinishArg = "--finish-install";
    public const string UninstallArg = "--uninstall";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AuraFlyout";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AuraFlyout");

    public static string Exe => Path.Combine(Folder, "AuraFlyout.exe");

    private static string Shortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AuraFlyout.lnk");

    /// <summary>This process runs the installed copy.</summary>
    public static bool RunningInstalled => string.Equals(Environment.ProcessPath, Exe, StringComparison.OrdinalIgnoreCase);

    public static bool Installed
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return k != null && File.Exists(Exe);
        }
    }

    public static string VersionText => "v" + (typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>Copies this exe into place and restarts from there (which finishes the installation).</summary>
    public static void Install()
    {
        bool startup = RunKeyPresent() || (SettingsStore.Current.RunElevated && Elevation.TaskHasLogonTrigger());
        Directory.CreateDirectory(Folder);
        File.Copy(Environment.ProcessPath!, Exe, overwrite: true);
        App.Instance.ExitApp(restart: () =>
            Process.Start(new ProcessStartInfo(Exe, FinishArg + (startup ? " startup" : "")) { UseShellExecute = true }));
    }

    /// <summary>In the installed copy: shortcut, uninstall entry, and start-up pointing here.</summary>
    public static void Finish(bool startup)
    {
        try
        {
            CreateShortcut();
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "AuraFlyout");
                k.SetValue("DisplayVersion", VersionText.TrimStart('v'));
                k.SetValue("Publisher", "AuraFlyout");
                k.SetValue("DisplayIcon", Exe);
                k.SetValue("InstallLocation", Folder);
                k.SetValue("UninstallString", $"\"{Exe}\" {UninstallArg}");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
            }
            if (SettingsStore.Current.RunElevated && Elevation.IsAdmin) Elevation.Register(startup); // the task now starts this copy
            else if (startup) StartupManager.SetRunKey(true);
            Log.Info("Installed to " + Folder);
        }
        catch (Exception ex)
        {
            Log.Error("Installation could not be finished", ex);
        }
    }

    /// <summary>Removes everything the installation added; the folder is deleted once this process ends.</summary>
    public static void Uninstall()
    {
        try
        {
            if (Elevation.TaskExists())
            {
                if (Elevation.IsAdmin) Elevation.Unregister();
                else Elevation.RunElevatedAndWait($"{Elevation.RegisterArg} off");
            }
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue("AuraFlyout", throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + WindowsNotifications.AppId, throwOnMissingSubKey: false);
            if (File.Exists(Shortcut)) File.Delete(Shortcut);
            // the running exe cannot delete itself: a short-lived cmd does it right after we exit
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{Folder}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            Log.Info("Uninstalled");
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall failed", ex);
        }
    }

    private static bool RunKeyPresent()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("AuraFlyout") != null;
    }

    // ---------- Start menu shortcut with the AppUserModelID (ties notifications to it) ----------

    private static void CreateShortcut()
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(Exe);
        link.SetArguments("");
        link.SetWorkingDirectory(Folder);
        link.SetIconLocation(Exe, 0);
        link.SetDescription("AuraFlyout");

        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // System.AppUserModel.ID
        var value = new PROPVARIANT { vt = 31 /*VT_LPWSTR*/, pointer = Marshal.StringToCoTaskMemUni(WindowsNotifications.AppId) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.pointer);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Shortcut)!);
        ((IPersistFile)link).Save(Shortcut, true);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }
}
