using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AuraFlyout.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AuraFlyout.Modules.SystemStatus;

public enum MetricLevel { Normal, Warn, Hot }

public enum MetricGlyphKind { None, Cpu, Ram, Gpu, Fps, Temperature, Download, Upload, Disk, Ping }

/// <summary>One "glyph + label + value" group shown on the taskbar (same model as TaskbarStatus' cells).</summary>
public sealed record StatusValue(string Text, MetricLevel Level, MetricGlyphKind Glyph);

public sealed record StatusCell(string Label, IReadOnlyList<StatusValue> Values);

/// <summary>
/// Samples CPU, GPU, RAM, FPS and network with the TaskbarStatus engines and publishes
/// formatted cells for the taskbar plus raw values / short history for the flyout.
/// </summary>
public sealed partial class SystemStatusService : ObservableObject, IDisposable
{
    public const int HistoryLength = 60;

    private readonly CpuMonitor _cpu = new();
    private readonly GpuMonitor _gpu = new();
    private readonly GpuSensor _gpuSensor = new();
    private readonly CpuTempMonitor _cpuTemp = new();
    private readonly NetMonitor _net = new();
    private readonly DiskMonitor _disk = new();
    private readonly VramMonitor _vram = new();
    private PingMonitor? _ping;
    private FpsMonitor? _fps;
    private readonly Dictionary<string, DateTime> _alerted = [];
    private readonly Dictionary<string, int> _overFor = [];
    private readonly DispatcherTimer _timer;
    private DateTime _nextCpuTempRead;
    private int _cpuTempBusy;
    private volatile object? _cpuTempBox;

    [ObservableProperty] public partial float CpuUsage { get; private set; }
    [ObservableProperty] public partial float? CpuTemp { get; private set; }
    [ObservableProperty] public partial string? CpuTempSource { get; private set; }
    [ObservableProperty] public partial float? GpuUsage { get; private set; }
    [ObservableProperty] public partial float? GpuTemp { get; private set; }
    [ObservableProperty] public partial int GpuFanRpm { get; private set; }
    [ObservableProperty] public partial string? GpuName { get; private set; }
    [ObservableProperty] public partial float RamUsage { get; private set; }
    [ObservableProperty] public partial double RamUsedGb { get; private set; }
    [ObservableProperty] public partial double RamTotalGb { get; private set; }
    [ObservableProperty] public partial int Fps { get; private set; }
    [ObservableProperty] public partial string FpsProcess { get; private set; } = "";
    [ObservableProperty] public partial double NetDown { get; private set; }
    [ObservableProperty] public partial double NetUp { get; private set; }
    [ObservableProperty] public partial float? DiskUsage { get; private set; }
    [ObservableProperty] public partial double? VramUsedGb { get; private set; }
    [ObservableProperty] public partial double? PingMs { get; private set; }

    public double? VramTotalGb => _vram.TotalGb;
    public bool FpsAvailable => _fps?.Available == true;
    public string CpuName { get; } = ReadCpuName();

    public Queue<float> CpuHistory { get; } = new();
    public Queue<float> GpuHistory { get; } = new();
    public Queue<float> RamHistory { get; } = new();
    public Queue<float> NetHistory { get; } = new();
    public Queue<float> DiskHistory { get; } = new();
    public Queue<float> PingHistory { get; } = new();

    public IReadOnlyList<StatusCell> Cells { get; private set; } = [];

    /// <summary>Raised after every sample (UI thread).</summary>
    public event Action? Sampled;

    public SystemStatusService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();
        SettingsStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.StatusInterval))
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(SettingsStore.Current.StatusInterval, 250, 10000));
            if (e.PropertyName is nameof(AppSettings.StatusEnabled) or nameof(AppSettings.OverlayEnabled)
                or nameof(AppSettings.StatusShowPing) or nameof(AppSettings.OverlayShowPing)) UpdateRunning();
            if (e.PropertyName != null && e.PropertyName.StartsWith("Status")) BuildCells();
        };
    }

    private static AppSettings S => SettingsStore.Current;

    public void Start()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(S.StatusInterval, 250, 10000));
        UpdateRunning();
    }

    /// <summary>Sampling runs while the taskbar readout or the in-game readout needs it.</summary>
    private bool Needed => S.StatusEnabled || S.OverlayEnabled;

    private bool WantsGpu => (S.StatusEnabled && S.StatusShowGpu) || (S.OverlayEnabled && S.OverlayShowGpu && OverlayVisible) || PanelOpen;

    private bool WantsFps => (S.StatusEnabled && S.StatusShowFps) || (S.OverlayEnabled && S.OverlayShowFps);

    private bool WantsPing => (S.StatusEnabled && S.StatusShowPing) || (S.OverlayEnabled && S.OverlayShowPing) || PanelOpen;

    /// <summary>The status panel is open: its ping chart needs samples even when the taskbar hides ping.</summary>
    public bool PanelOpen
    {
        get => _panelOpen;
        set
        {
            _panelOpen = value;
            UpdateRunning();
        }
    }

    private bool _panelOpen;

    /// <summary>The in-game readout is on screen (GPU / VRAM are then read for it).</summary>
    public bool OverlayVisible { get; set; }

    private void UpdateRunning()
    {
        if (Needed || PanelOpen)
        {
            // ETW needs admin; without it Available stays false and FPS shows "--"
            if (_fps == null && WantsFps) _fps = new FpsMonitor();
            if (!_timer.IsEnabled)
            {
                Tick();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }

        if (WantsPing && _ping == null) _ping = new PingMonitor("1.1.1.1");
        else if (!WantsPing && _ping != null)
        {
            _ping.Dispose();
            _ping = null;
            PingMs = null;
        }
    }

    /// <summary>Samples right away (e.g. when the flyout opens).</summary>
    public void SampleNow() => Tick();

    private void Tick()
    {
        double t0 = PerfLog.Now;
        try
        {
            TickCore();
        }
        finally
        {
            if (PerfLog.Enabled) PerfLog.Write($"status tick {PerfLog.Now - t0:F1} ms (gpu={WantsGpu})");
        }
    }

    private void TickCore()
    {
        try
        {
            CpuUsage = _cpu.Read();
            RamUsage = RamMonitor.Read(out double used, out double total);
            RamUsedGb = used;
            RamTotalGb = total;
            // the "GPU Engine" counters list every process' engines: only read them when GPU load is shown
            if (WantsGpu) GpuUsage = _gpu.Read();
            _gpuSensor.Update();
            GpuTemp = _gpuSensor.Temperature;
            GpuFanRpm = _gpuSensor.FanRpm;
            GpuName = _gpuSensor.Name;
            _net.Update();
            NetDown = _net.Down;
            NetUp = _net.Up;

            // WMI can take seconds when the sensor tool is not running: stay off the UI thread
            if (S.StatusShowCpuTemp && DateTime.UtcNow >= _nextCpuTempRead &&
                Interlocked.CompareExchange(ref _cpuTempBusy, 1, 0) == 0)
            {
                _nextCpuTempRead = DateTime.UtcNow.AddSeconds(2);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _cpuTempBox = _cpuTemp.Read() is float t ? t : null; }
                    catch { _cpuTempBox = null; }
                    finally { _cpuTempBusy = 0; }
                });
            }
            CpuTemp = _cpuTempBox is float ct ? ct : null;
            CpuTempSource = _cpuTemp.Source;

            // readings nobody looks at are not taken
            if ((S.StatusEnabled && S.StatusShowDisk) || PanelOpen) DiskUsage = _disk.Read();
            if ((S.StatusEnabled && S.StatusShowVram) || (S.OverlayEnabled && S.OverlayShowVram && OverlayVisible) || PanelOpen) VramUsedGb = _vram.ReadUsedGb();
            PingMs = _ping?.LastMs;

            if (_fps == null && WantsFps) _fps = new FpsMonitor();
            if (_fps is { Available: true })
            {
                Fps = _fps.Read();
                IntPtr fg = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(fg, out uint pid);
                _fps.TargetPid = (int)pid;
                if (Fps > 0) FpsProcess = ProcessName((int)pid);
            }

            Push(CpuHistory, CpuUsage);
            Push(GpuHistory, GpuUsage ?? 0);
            Push(RamHistory, RamUsage);
            Push(NetHistory, (float)(NetDown / 1024 / 1024));
            Push(DiskHistory, DiskUsage ?? 0);
            if (_ping != null) Push(PingHistory, (float)(PingMs ?? 0));

            BuildCells();
            CheckAlerts();
            Sampled?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("system status sample failed", ex);
        }
    }

    private static void Push(Queue<float> q, float v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    // ---------- what to show (same rules as TaskbarStatus) ----------

    private void BuildCells()
    {
        var cells = new List<StatusCell>();
        var s = S;
        if (s.StatusShowCpu || s.StatusShowCpuTemp)
        {
            var v = new List<StatusValue>();
            if (s.StatusShowCpu) v.Add(new StatusValue(Percent(CpuUsage), UsageLevel(CpuUsage), MetricGlyphKind.Cpu));
            if (s.StatusShowCpuTemp) v.Add(new StatusValue(Temp(CpuTemp), TempLevel(CpuTemp), MetricGlyphKind.Temperature));
            cells.Add(new StatusCell("CPU", v));
        }
        if (s.StatusShowGpu || s.StatusShowGpuTemp)
        {
            var v = new List<StatusValue>();
            if (s.StatusShowGpu) v.Add(new StatusValue(GpuUsage.HasValue ? Percent(GpuUsage.Value) : "--", UsageLevel(GpuUsage ?? 0), MetricGlyphKind.Gpu));
            if (s.StatusShowGpuTemp) v.Add(new StatusValue(Temp(GpuTemp), TempLevel(GpuTemp), MetricGlyphKind.Temperature));
            cells.Add(new StatusCell("GPU", v));
        }
        if (s.StatusShowRam)
        {
            string text = s.StatusRamAsGb ? RamUsedGb.ToString("0.0") + " GB" : Percent(RamUsage);
            cells.Add(new StatusCell("RAM", [new StatusValue(text, UsageLevel(RamUsage), MetricGlyphKind.Ram)]));
        }
        if (s.StatusShowFps)
            cells.Add(new StatusCell("FPS", [new StatusValue(FpsAvailable && Fps > 0 ? Fps.ToString() : "--", MetricLevel.Normal, MetricGlyphKind.Fps)]));
        if (s.StatusShowDisk)
            cells.Add(new StatusCell("DISK", [new StatusValue(DiskUsage.HasValue ? Percent(DiskUsage.Value) : "--", UsageLevel(DiskUsage ?? 0), MetricGlyphKind.Disk)]));
        if (s.StatusShowVram)
            cells.Add(new StatusCell("VRAM", [new StatusValue(VramUsedGb is double vr ? vr.ToString("0.0") + " GB" : "--", MetricLevel.Normal, MetricGlyphKind.Gpu)]));
        if (s.StatusShowPing)
            cells.Add(new StatusCell("PING", [new StatusValue(PingMs is double pm ? Math.Round(pm) + " ms" : "--", pm_Level(PingMs), MetricGlyphKind.Ping)]));
        if (s.StatusShowNet)
        {
            cells.Add(new StatusCell("↓", [new StatusValue(NetMonitor.Format(NetDown), MetricLevel.Normal, MetricGlyphKind.Download)]));
            cells.Add(new StatusCell("↑", [new StatusValue(NetMonitor.Format(NetUp), MetricLevel.Normal, MetricGlyphKind.Upload)]));
        }
        Cells = cells;
    }

    public static string Percent(float v) => ((int)Math.Round(v)) + "%";
    public static string Temp(float? v) => v.HasValue ? ((int)Math.Round(v.Value)) + "°C" : "--";
    private static MetricLevel UsageLevel(float v) => v >= 90 ? MetricLevel.Warn : MetricLevel.Normal;

    private static MetricLevel TempLevel(float? v) => TempLevelOf(v);

    public static MetricLevel TempLevelOf(float? v) =>
        !v.HasValue ? MetricLevel.Normal : v.Value >= 85 ? MetricLevel.Hot : v.Value >= 75 ? MetricLevel.Warn : MetricLevel.Normal;

    private static MetricLevel pm_Level(double? ms) =>
        ms is not double v ? MetricLevel.Normal : v >= 150 ? MetricLevel.Hot : v >= 80 ? MetricLevel.Warn : MetricLevel.Normal;

    // ---------- warnings as Windows notifications ----------

    /// <summary>
    /// CPU / GPU temperature or RAM above the chosen limit for 10 s in a row raises one notification;
    /// the same one again only after 15 minutes.
    /// </summary>
    private void CheckAlerts()
    {
        if (!S.AlertsEnabled) return;
        Check("cpu", CpuTemp is float c && c >= S.AlertCpuTemp, "Alert_CpuTemp", Temp(CpuTemp));
        Check("gpu", GpuTemp is float g && g >= S.AlertGpuTemp, "Alert_GpuTemp", Temp(GpuTemp));
        Check("ram", RamUsage >= S.AlertRam, "Alert_Ram", Percent(RamUsage));
    }

    private void Check(string id, bool over, string key, string value)
    {
        if (!over)
        {
            _overFor[id] = 0;
            return;
        }
        double interval = Math.Max(0.25, _timer.Interval.TotalSeconds);
        int needed = (int)Math.Ceiling(10 / interval);
        _overFor[id] = _overFor.GetValueOrDefault(id) + 1;
        if (_overFor[id] < needed) return;
        if (_alerted.TryGetValue(id, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(15)) return;
        _alerted[id] = DateTime.UtcNow;
        App.Panels.ShowToast(L10n.Get(key), L10n.Format("Alert_Value", value), "", warning: true);
    }

    /// <summary>
    /// Why the CPU temperature is missing. <paramref name="withLabel"/> prefixes "temp:" for places
    /// where it would otherwise be unclear what is missing (CPU tile, tooltip).
    /// </summary>
    public string CpuTempHint(bool withLabel = true)
    {
        string why = !PawnIo.Installed ? L10n.Get("Status_TempNoPawnIo")
            : !Elevation.IsAdmin ? L10n.Get("Status_NeedsAdmin")
            : L10n.Get("Status_TempUnsupported");
        return withLabel ? L10n.Get("Status_TempLabel") + ": " + why : why;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return "?";
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch
        {
            return "CPU";
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _ping?.Dispose();
        _fps?.Dispose();
        _gpuSensor.Dispose();
    }
}
