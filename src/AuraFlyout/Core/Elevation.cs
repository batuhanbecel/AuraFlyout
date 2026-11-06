using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace AuraFlyout.Core;

/// <summary>
/// CPU temperature (PawnIO) and FPS (ETW) need administrator rights. Same approach as TaskbarStatus:
/// a scheduled task with "highest privileges" starts AuraFlyout elevated without a UAC prompt; normal
/// launches hop through the task and its logon trigger doubles as "start with Windows".
/// </summary>
public static class Elevation
{
    public const string TaskName = "AuraFlyout";
    public const string FromTaskArg = "--from-task";
    public const string RegisterArg = "--register-task";

    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool TaskExists() => Schtasks($"/Query /TN {TaskName}", out _) == 0;

    public static bool TaskHasLogonTrigger() =>
        Schtasks($"/Query /TN {TaskName} /XML", out string xml) == 0 && xml.Contains("<LogonTrigger>");

    public static bool RunTask() => Schtasks($"/Run /TN {TaskName}", out _) == 0;

    /// <summary>Creates (or replaces) the elevated task. Requires admin.</summary>
    public static bool Register(bool atLogon)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) ?? "";
        string exe = SecurityElement.Escape(Environment.ProcessPath ?? "") ?? "";
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>AuraFlyout (elevated start for CPU temperature and FPS)</Description></RegistrationInfo>\r\n" +
            "  <Triggers>" + (atLogon ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger>" : "") + "</Triggers>\r\n" +
            $"  <Principals><Principal id=\"Author\"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings>\r\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
            "    <Priority>6</Priority>\r\n" +
            "  </Settings>\r\n" +
            $"  <Actions Context=\"Author\"><Exec><Command>{exe}</Command><Arguments>{FromTaskArg} --tray</Arguments></Exec></Actions>\r\n" +
            "</Task>\r\n";

        string file = Path.Combine(Path.GetTempPath(), "AuraFlyout-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            return Schtasks($"/Create /TN {TaskName} /XML \"{file}\" /F", out _) == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static bool Unregister() => Schtasks($"/Delete /TN {TaskName} /F", out _) == 0;

    /// <summary>Runs this exe elevated with the given arguments and waits. False when UAC was declined.</summary>
    public static bool RunElevatedAndWait(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                Verb = "runas",
                UseShellExecute = true,
            });
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }

    /// <summary>Handles "--register-task on|off|nologon" in an elevated helper process. Returns the exit code.</summary>
    public static int HandleRegisterArgs(string[] args)
    {
        int i = Array.IndexOf(args, RegisterArg);
        string mode = i >= 0 && i + 1 < args.Length ? args[i + 1] : "on";
        bool ok = mode switch
        {
            "off" => Unregister() || !TaskExists(),
            "nologon" => Register(atLogon: false),
            _ => Register(atLogon: true),
        };
        return ok ? 0 : 1;
    }

    private static int Schtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warn("schtasks failed", ex);
            output = "";
            return -1;
        }
    }
}
