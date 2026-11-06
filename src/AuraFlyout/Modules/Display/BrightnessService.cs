using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using AuraFlyout.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AuraFlyout.Modules.Display;

/// <summary>One screen whose brightness can be set: a monitor over DDC/CI or the built-in laptop panel.</summary>
public sealed partial class DisplayBrightness : ObservableObject
{
    private readonly BrightnessService _owner;
    internal bool Syncing;

    internal DisplayBrightness(BrightnessService owner, string key, string name, IntPtr physical, bool builtIn, int min, int max)
    {
        _owner = owner;
        Key = key;
        Name = name;
        Physical = physical;
        BuiltIn = builtIn;
        Min = min;
        Max = Math.Max(min + 1, max);
    }

    internal string Key { get; }
    internal IntPtr Physical { get; set; }
    internal bool BuiltIn { get; }
    internal int Min { get; }
    internal int Max { get; }

    public string Name { get; }

    /// <summary>0..100 (the monitor's own range is mapped onto it).</summary>
    [ObservableProperty] public partial double Value { get; set; }

    partial void OnValueChanged(double value)
    {
        if (!Syncing) _owner.Queue(this, value);
    }
}

/// <summary>
/// Brightness of every screen, the way the Windows quick settings set it for the built-in panel
/// (WMI) and the way monitor tools do for external monitors (DDC/CI through dxva2). Monitor names
/// come from their EDID ("VG27A"). Writes go through one background thread that always applies the
/// latest value only, so dragging a slider never queues up slow DDC commands.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly ConcurrentDictionary<DisplayBrightness, double> _pending = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _writer;
    private readonly List<IntPtr> _handles = [];
    private ManagementEventWatcher? _watcher;
    private bool _disposed, _refreshing;

    public ObservableCollection<DisplayBrightness> Displays { get; } = [];

    /// <summary>The built-in panel's brightness changed outside AuraFlyout (brightness keys): 0..100.</summary>
    public event Action<int>? ChangedExternally;

    public BrightnessService()
    {
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "AuraFlyout.Brightness" };
        _writer.Start();
    }

    public void Start()
    {
        Refresh();
        try
        {
            // laptop brightness keys: Windows changes the panel itself, we only show the flyout
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"),
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                int v = Convert.ToInt32(e.NewEvent["Brightness"]);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var d in Displays.Where(d => d.BuiltIn))
                    {
                        d.Syncing = true;
                        d.Value = v;
                        d.Syncing = false;
                    }
                    ChangedExternally?.Invoke(v);
                });
            };
            _watcher.Start();
        }
        catch
        {
            _watcher = null; // no built-in panel (desktop)
        }
    }

    /// <summary>Re-reads the screens and their current levels (monitor buttons may have changed them).</summary>
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        Task.Run(Read).ContinueWith(t =>
        {
            var found = t.IsCompletedSuccessfully ? t.Result : [];
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Apply(found);
                _refreshing = false;
            });
        });
    }

    private void Apply(List<(string Key, string Name, IntPtr Physical, bool BuiltIn, int Min, int Cur, int Max)> found)
    {
        for (int i = Displays.Count - 1; i >= 0; i--)
            if (!found.Any(f => f.Key == Displays[i].Key)) Displays.RemoveAt(i);
        foreach (var f in found)
        {
            var d = Displays.FirstOrDefault(x => x.Key == f.Key);
            if (d == null)
            {
                d = new DisplayBrightness(this, f.Key, f.Name, f.Physical, f.BuiltIn, f.Min, f.Max);
                Displays.Add(d);
            }
            d.Physical = f.Physical;
            if (_pending.ContainsKey(d)) continue; // a write is on its way: do not jump back
            d.Syncing = true;
            d.Value = Math.Round((f.Cur - d.Min) * 100.0 / (d.Max - d.Min));
            d.Syncing = false;
        }
    }

    internal void Queue(DisplayBrightness d, double value)
    {
        _pending[d] = value;
        _wake.Set();
    }

    private void WriteLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            foreach (var d in _pending.Keys.ToList())
            {
                if (!_pending.TryRemove(d, out double v)) continue;
                try
                {
                    int level = (int)Math.Round(d.Min + v / 100.0 * (d.Max - d.Min));
                    if (d.BuiltIn) SetBuiltIn((byte)Math.Clamp(v, 0, 100));
                    else if (d.Physical != IntPtr.Zero) SetMonitorBrightness(d.Physical, (uint)level);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Brightness of {d.Name} could not be set", ex);
                }
            }
        }
    }

    // ---------- reading ----------

    private List<(string, string, IntPtr, bool, int, int, int)> Read()
    {
        lock (_handles)
        {
            if (_handles.Count > 0)
            {
                var old = _handles.ToArray();
                _handles.Clear();
                // keep the old handles alive until no write can use them any more
                Task.Delay(5000).ContinueWith(_ => DestroyHandles(old));
            }
        }

        var result = new List<(string, string, IntPtr, bool, int, int, int)>();
        var names = EdidNames();
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            monitors.Add(h);
            return true;
        }, IntPtr.Zero);

        bool builtInUsed = false;
        foreach (IntPtr hmon in monitors)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(hmon, ref info);
            string name = FriendlyName(info.szDevice, names);
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0) continue;
            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, physical)) continue;
            lock (_handles) _handles.AddRange(physical.Select(p => p.hPhysicalMonitor));

            for (int i = 0; i < physical.Length; i++)
            {
                var p = physical[i];
                string key = info.szDevice + "#" + i;
                if (GetMonitorBrightness(p.hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                    result.Add((key, name, p.hPhysicalMonitor, false, (int)min, (int)cur, (int)max));
                else if (!builtInUsed && ReadBuiltIn() is int b)
                {
                    builtInUsed = true; // DDC does not reach a laptop panel: that one is set through WMI
                    result.Add((key, name, IntPtr.Zero, true, 0, b, 100));
                }
            }
        }
        return result;
    }

    private static int? ReadBuiltIn()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch
        {
            // no WMI brightness: desktop monitor without DDC/CI
        }
        return null;
    }

    private static void SetBuiltIn(byte value)
    {
        using var cls = new ManagementClass(@"root\wmi", "WmiMonitorBrightnessMethods", null);
        foreach (ManagementObject o in cls.GetInstances())
            o.InvokeMethod("WmiSetBrightness", [(uint)1, value]);
    }

    /// <summary>Monitor names from the EDID, by hardware id ("AUS27A1" → "VG27A").</summary>
    private static Dictionary<string, string> EdidNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
            foreach (ManagementObject o in searcher.Get())
            {
                string instance = o["InstanceName"] as string ?? "";
                string[] parts = instance.Split('\\');
                if (parts.Length < 2 || o["UserFriendlyName"] is not ushort[] chars) continue;
                string name = new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                if (name.Length > 0) map[parts[1]] = name;
            }
        }
        catch
        {
            // WMI unavailable: generic names
        }
        return map;
    }

    private static string FriendlyName(string gdiName, Dictionary<string, string> edid)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(gdiName, 0, ref dd, 1 /*EDD_GET_DEVICE_INTERFACE_NAME*/))
        {
            // \\?\DISPLAY#AUS27A1#5&...#{guid}
            string[] parts = dd.DeviceID.Split('#');
            if (parts.Length > 1 && edid.TryGetValue(parts[1], out var name)) return name;
            if (!string.IsNullOrWhiteSpace(dd.DeviceString)) return dd.DeviceString;
        }
        return gdiName.Replace(@"\\.\", "");
    }

    private static void DestroyHandles(IntPtr[] handles)
    {
        foreach (var h in handles) DestroyPhysicalMonitor(h);
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        try { _watcher?.Stop(); } catch { /* ignore */ }
        _watcher?.Dispose();
        lock (_handles) DestroyHandles(_handles.ToArray());
    }

    // ---------- native ----------

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
}
