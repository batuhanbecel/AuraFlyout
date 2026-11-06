using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace AuraFlyout.Modules.SystemStatus;

/// <summary>Disk activity the way Task Manager shows it: 100 % minus the idle time of all physical disks.</summary>
internal sealed class DiskMonitor
{
    private PerformanceCounter? _idle;
    private int _failures;

    public float? Read()
    {
        if (_failures > 5) return null;
        try
        {
            if (_idle == null)
            {
                _idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true);
                _idle.NextValue(); // first value of a rate counter is always 0
                return null;
            }
            _failures = 0;
            return Math.Clamp(100f - _idle.NextValue(), 0f, 100f);
        }
        catch
        {
            _failures++;
            _idle = null;
            return null;
        }
    }
}

/// <summary>
/// Dedicated video memory in use (the "GPU Adapter Memory" counters Task Manager reads) and the card's
/// total, from the display adapter's registry entry.
/// </summary>
internal sealed class VramMonitor
{
    private PerformanceCounterCategory? _category;
    private double? _totalGb;
    private int _failures;

    public double? TotalGb => _totalGb ??= ReadTotal();

    public double? ReadUsedGb()
    {
        if (_failures > 5) return null;
        try
        {
            _category ??= new PerformanceCounterCategory("GPU Adapter Memory");
            var data = _category.ReadCategory()["Dedicated Usage"];
            if (data == null) return null;
            long max = 0;
            foreach (InstanceData d in data.Values) max = Math.Max(max, d.RawValue); // the busiest adapter: the one games use
            _failures = 0;
            return max / 1024.0 / 1024 / 1024;
        }
        catch
        {
            _failures++;
            return null;
        }
    }

    private static double? ReadTotal()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            long best = 0;
            foreach (string name in cls.GetSubKeyNames())
            {
                if (!int.TryParse(name, out _)) continue;
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is long q) best = Math.Max(best, q);
                else if (k?.GetValue("HardwareInformation.MemorySize") is int m) best = Math.Max(best, (uint)m);
            }
            return best > 0 ? best / 1024.0 / 1024 / 1024 : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Round trip to a public resolver (1.1.1.1) every two seconds, off the UI thread.</summary>
internal sealed class PingMonitor : IDisposable
{
    private readonly Timer _timer;
    private int _busy;

    public PingMonitor(string host)
    {
        Host = host;
        _timer = new Timer(_ => Send(), null, 0, 2000);
    }

    public string Host { get; set; }

    /// <summary>Last round trip in ms; null when the host did not answer.</summary>
    public double? LastMs { get; private set; }

    private void Send()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(Host, 1500);
            LastMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            LastMs = null;
        }
        finally
        {
            _busy = 0;
        }
    }

    public void Dispose() => _timer.Dispose();
}
