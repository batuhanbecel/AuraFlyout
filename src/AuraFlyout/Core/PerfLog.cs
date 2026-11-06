using System.Diagnostics;

namespace AuraFlyout.Core;

/// <summary>Development timing log (AURA_PERFLOG=path): milliseconds since start, one line per event.</summary>
public static class PerfLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("AURA_PERFLOG");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();

    public static bool Enabled => Path != null;

    public static double Now => Clock.Elapsed.TotalMilliseconds;

    public static void Write(string text)
    {
        if (Path == null) return;
        lock (Gate) System.IO.File.AppendAllText(Path, $"{Clock.Elapsed.TotalMilliseconds,9:F1} [{Environment.CurrentManagedThreadId,2}] {text}\n");
    }
}
