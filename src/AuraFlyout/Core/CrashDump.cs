using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AuraFlyout.Core;

/// <summary>
/// A memory dump of AuraFlyout when an unhandled exception ends it: %LocalAppData%\AuraFlyout\crash.dmp,
/// one file, the latest crash only. It stays on the computer; it is there for dotnet-dump when a crash
/// (a finalizer one, say) shows nothing but its last frames in the log.
/// </summary>
internal static partial class CrashDump
{
    // MiniDumpWithDataSegs | WithHandleData | WithUnloadedModules | WithPrivateReadWriteMemory |
    // WithFullMemoryInfo | WithThreadInfo | IgnoreInaccessibleMemory: what dotnet-dump needs for the managed heap
    private const uint DumpType = 0x1 | 0x4 | 0x20 | 0x200 | 0x800 | 0x1000 | 0x20000;

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraFlyout", "crash.dmp");

    public static void Write()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var process = Process.GetCurrentProcess();
            using var file = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            if (MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                Log.Info("Crash dump written to " + Path);
            else
                Log.Warn($"Crash dump failed (error {Marshal.GetLastPInvokeError()})");
        }
        catch (Exception ex)
        {
            Log.Warn("Crash dump failed", ex);
        }
    }

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeFileHandle hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
