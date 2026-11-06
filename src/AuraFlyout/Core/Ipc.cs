using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace AuraFlyout.Core;

/// <summary>
/// Named mutex / events shared between instances. They get an explicit DACL for the current user:
/// an elevated AuraFlyout (admin mode) would otherwise create them with an admin-only default DACL,
/// and a normal launch could neither detect it nor ask it to open its settings or exit.
/// </summary>
public static class Ipc
{
    private static SecurityIdentifier User
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User!;
        }
    }

    public static Mutex CreateMutex(string name, out bool createdNew)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(User, MutexRights.FullControl, AccessControlType.Allow));
        try
        {
            return MutexAcl.Create(true, name, out createdNew, security);
        }
        catch (UnauthorizedAccessException)
        {
            // an older elevated instance holds it with an admin-only DACL: treat as "already running"
            createdNew = false;
            return new Mutex(false);
        }
    }

    public static EventWaitHandle CreateEvent(string name)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }

    /// <summary>Signals a named event of the running instance; false when there is none (or no access).</summary>
    public static bool Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            return e.Set();
        }
        catch
        {
            return false;
        }
    }
}
