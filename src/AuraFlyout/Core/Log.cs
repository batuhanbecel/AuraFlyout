using System.IO;

namespace AuraFlyout.Core;

/// <summary>Tiny file logger (%AppData%\AuraFlyout\aura.log, rotated at 1 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "aura.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += Environment.NewLine + "    " + ex;
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
