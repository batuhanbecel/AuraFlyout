using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using AuraFlyout.Core;
using AuraFlyout.Modules;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace AuraFlyout.Modules.LogiBattery;

/// <summary>A Logitech device as the UI shows it.</summary>
public sealed partial class LogiDeviceItem : ObservableObject, IBatteryDevice
{
    public bool CanForget => true;
    public LogiDeviceItem(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = "";
    [ObservableProperty] public partial bool Connected { get; set; }
    [ObservableProperty] public partial string Connection { get; set; } = "";
    [ObservableProperty] public partial int Percent { get; set; } = -1;
    [ObservableProperty] public partial bool Approximate { get; set; }
    [ObservableProperty] public partial bool Charging { get; set; }
    [ObservableProperty] public partial bool Full { get; set; }
    [ObservableProperty] public partial bool Low { get; set; }
    [ObservableProperty] public partial bool Hidden { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string PercentText { get; set; } = "";

    public bool HasBattery => Percent >= 0;
    partial void OnPercentChanged(int value) => OnPropertyChanged(nameof(HasBattery));
}

/// <summary>
/// Hosts TaskbarLogiBattery's device engine (HID++ 2.0 / Centurion) and turns its snapshots into
/// bindable items plus connect / disconnect / low / full notifications.
/// </summary>
public sealed class LogiBatteryService : IDisposable
{
    public const string RegistryRoot = @"Software\AuraFlyout\LogiBattery";
    private const string LegacyKey = @"Software\TaskbarLogiBattery";

    private readonly DateTime _started = DateTime.UtcNow;
    private readonly Dictionary<string, Watch> _watches = [];
    private DeviceManager? _manager;
    private HwndSource? _deviceWindow;
    private List<LogiDevice> _raw = [];

    private sealed class Watch
    {
        public bool Connected, LowSent, CriticalSent, SawCharging, FullSent;
    }

    public enum Event { Connected, Disconnected, Low, Critical, Full }

    public ObservableCollection<LogiDeviceItem> Devices { get; } = [];

    /// <summary>Visible + connected devices, in taskbar order (headset, keyboard, mouse).</summary>
    public IEnumerable<LogiDeviceItem> TaskbarDevices => Devices.Where(d => d.Connected && !d.Hidden);

    public event Action? Changed;

    /// <summary>name, title, text, glyph, warning</summary>
    public event Action<string, string, string, bool>? Notify;

    private static AppSettings S => SettingsStore.Current;

    public void Start()
    {
        SettingsStore.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.LogiEnabled))
            {
                if (S.LogiEnabled) StartEngine();
                else StopEngine();
            }
            else if (e.PropertyName == nameof(AppSettings.LogiPollSeconds) && _manager != null)
            {
                _manager.PollInterval = TimeSpan.FromSeconds(Math.Clamp(S.LogiPollSeconds, 15, 600));
            }
            else if (e.PropertyName == nameof(AppSettings.LogiDebugLog))
            {
                Log.Enabled = S.LogiDebugLog;
            }
            else if (e.PropertyName is nameof(AppSettings.LogiLowThreshold) or nameof(AppSettings.LogiAliases) or nameof(AppSettings.LogiShowPercent))
            {
                Rebuild();
            }
        };
        S.LogiHidden.CollectionChanged += (_, _) => Rebuild();
        L10n.LanguageChanged += Rebuild;
        Log.Enabled = S.LogiDebugLog;
        if (S.LogiEnabled) StartEngine();
    }

    private void StartEngine()
    {
        if (_manager != null) return;
        MigrateLegacyDevices();
        _manager = new DeviceManager
        {
            RegistryKey = RegistryRoot,
            PollInterval = TimeSpan.FromSeconds(Math.Clamp(S.LogiPollSeconds, 15, 600)),
        };
        _manager.Changed += list => Application.Current?.Dispatcher.BeginInvoke(() => OnDevices(list));
        _manager.Start();
        HookDeviceChanges();
    }

    private void StopEngine()
    {
        _manager?.Dispose();
        _manager = null;
        _deviceWindow?.Dispose();
        _deviceWindow = null;
        _raw = [];
        Devices.Clear();
        Changed?.Invoke();
    }

    public void RefreshNow() => _manager?.RefreshNow();

    public void Forget(string id) => _manager?.Forget(id);

    public void SetHidden(string id, bool hide)
    {
        var list = S.LogiHidden;
        if (hide && !list.Contains(id)) list.Add(id);
        if (!hide) list.Remove(id);
    }

    public void Rename(string id, string? alias)
    {
        var map = new Dictionary<string, string>(S.LogiAliases);
        if (string.IsNullOrWhiteSpace(alias)) map.Remove(id);
        else map[id] = alias.Trim();
        S.LogiAliases = map;
    }

    internal string NameOf(LogiDevice d) =>
        S.LogiAliases.TryGetValue(d.Id, out var alias) ? alias : d.Name ?? d.Id;

    // WM_DEVICECHANGE: plugging a receiver / cable in or out triggers a quick rescan
    private void HookDeviceChanges()
    {
        if (_deviceWindow != null) return;
        _deviceWindow = new HwndSource(new HwndSourceParameters("AuraFlyoutLogiDevices") { Width = 0, Height = 0, WindowStyle = 0 });
        _deviceWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WM_DEVICECHANGE = 0x219;
            if (msg == WM_DEVICECHANGE) _manager?.RequestRescan();
            return IntPtr.Zero;
        });
    }

    private void OnDevices(List<LogiDevice> list)
    {
        _raw = list;
        bool startup = DateTime.UtcNow - _started < TimeSpan.FromSeconds(20);
        int low = S.LogiLowThreshold;

        foreach (var d in list)
        {
            if (S.LogiHidden.Contains(d.Id)) continue;
            if (!_watches.TryGetValue(d.Id, out var w))
            {
                w = new Watch { Connected = d.Connected };
                _watches[d.Id] = w;
                if (d.Connected && !startup && S.LogiNotifyConnect) Announce(d, Event.Connected);
            }
            else if (w.Connected != d.Connected)
            {
                w.Connected = d.Connected;
                if (d.Connected && S.LogiNotifyConnect && !startup) Announce(d, Event.Connected);
                if (!d.Connected && S.LogiNotifyDisconnect) Announce(d, Event.Disconnected);
            }

            var b = d.Battery;
            if (!d.Connected || b == null) continue;
            if (b.State == ChargeState.Discharging)
            {
                if (b.Percent <= low && !w.LowSent)
                {
                    w.LowSent = true;
                    if (S.LogiNotifyLow && b.Percent > 5) Announce(d, Event.Low);
                }
                if (b.Percent <= 5 && !w.CriticalSent)
                {
                    w.CriticalSent = w.LowSent = true;
                    if (S.LogiNotifyLow) Announce(d, Event.Critical);
                }
            }
            if (b.OnCharger || b.Percent > low + 3) w.LowSent = w.CriticalSent = false;
            if (b.State == ChargeState.Charging) w.SawCharging = true;
            if (b.State == ChargeState.Full && w.SawCharging && !w.FullSent)
            {
                w.FullSent = true;
                if (S.LogiNotifyFull) Announce(d, Event.Full);
            }
            if (b.State != ChargeState.Full) w.FullSent = false;
            if (!b.OnCharger) w.SawCharging = false;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        var keep = new HashSet<string>();
        int index = 0;
        foreach (var d in _raw)
        {
            keep.Add(d.Id);
            var item = Devices.FirstOrDefault(x => x.Id == d.Id);
            if (item == null)
            {
                item = new LogiDeviceItem(d.Id);
                Devices.Insert(Math.Min(index, Devices.Count), item);
            }
            else if (Devices.IndexOf(item) != index && index < Devices.Count)
            {
                Devices.Move(Devices.IndexOf(item), index);
            }
            Fill(item, d);
            index++;
        }
        for (int i = Devices.Count - 1; i >= 0; i--)
            if (!keep.Contains(Devices[i].Id)) Devices.RemoveAt(i);
        Changed?.Invoke();
    }

    private void Fill(LogiDeviceItem item, LogiDevice d)
    {
        var b = d.Battery;
        item.Name = NameOf(d);
        item.Glyph = KindGlyph(d.Kind);
        item.Connected = d.Connected;
        item.Connection = d.Connection ?? "";
        item.Hidden = S.LogiHidden.Contains(d.Id);
        item.Percent = b?.Percent ?? -1;
        item.Approximate = b?.Approximate == true;
        item.Charging = b?.State == ChargeState.Charging;
        item.Full = b?.State == ChargeState.Full;
        item.Low = b != null && b.Percent <= S.LogiLowThreshold && !b.OnCharger;
        item.PercentText = b == null ? "--" : (item.Approximate ? "~" : "") + Pct(b.Percent);

        string state;
        if (!d.Connected)
            state = L10n.Get("Logi_NotConnected") + (d.LastSeen > DateTime.MinValue ? " · " + Ago(d.LastSeen) : "");
        else if (b == null) state = L10n.Get("Logi_Reading");
        else if (item.Charging) state = L10n.Get("Logi_Charging");
        else if (item.Full) state = L10n.Get("Logi_Full");
        else if (b.State == ChargeState.Error) state = L10n.Get("Logi_Error");
        else state = L10n.Get("Logi_OnBattery");
        if (d.Connected && !string.IsNullOrEmpty(d.Connection)) state += " · " + d.Connection;
        item.StatusText = state;
    }

    private void Announce(LogiDevice d, Event e)
    {
        string name = NameOf(d);
        var b = d.Battery;
        string pct = b != null ? Pct(b.Percent) : "";
        string title, text;
        switch (e)
        {
            case Event.Connected:
                title = L10n.Format("Logi_EvConnected", name);
                text = b == null ? "" : b.State == ChargeState.Charging ? L10n.Get("Logi_Charging") + " · " + pct : L10n.Get("Logi_Battery") + " " + pct;
                break;
            case Event.Disconnected:
                title = L10n.Format("Logi_EvDisconnected", name);
                text = b == null ? "" : L10n.Get("Logi_LastLevel") + " " + pct;
                break;
            case Event.Low:
                title = L10n.Format("Logi_EvLow", name);
                text = L10n.Format("Logi_EvLowText", pct);
                break;
            case Event.Critical:
                title = L10n.Format("Logi_EvCritical", name);
                text = L10n.Format("Logi_EvCriticalText", pct);
                break;
            default:
                title = L10n.Format("Logi_EvFull", name);
                text = L10n.Get("Logi_EvFullText");
                break;
        }
        Notify?.Invoke(title, text, KindGlyph(d.Kind), e is Event.Low or Event.Critical);
    }

    /// <summary>Shows a sample notification (settings page).</summary>
    public void TestNotification()
    {
        var d = _raw.FirstOrDefault(x => x.Connected) ?? new LogiDevice { Id = "test", Name = "Logitech", Kind = DeviceKind.Mouse, Battery = new BatteryReading { Percent = 15 } };
        Announce(d, d.Battery != null && d.Battery.Percent <= S.LogiLowThreshold ? Event.Low : Event.Connected);
    }

    internal static string KindGlyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse or DeviceKind.Trackball => "",
        DeviceKind.Keyboard or DeviceKind.Numpad => "",
        DeviceKind.Headset => "",
        DeviceKind.Gamepad or DeviceKind.Wheel => "",
        DeviceKind.Speaker => "",
        DeviceKind.Webcam => "",
        DeviceKind.Presenter => "",
        _ => "",
    };

    /// <summary>"%85" in Turkish, "85%" in English (as in the original).</summary>
    public static string Pct(int value) => L10n.ActiveLanguage == "tr" ? "%" + value : value + "%";

    private static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        if (t.TotalMinutes < 1) return L10n.Get("Ago_Now");
        if (t.TotalMinutes < 60) return L10n.Format("Ago_Min", (int)t.TotalMinutes);
        if (t.TotalHours < 24) return L10n.Format("Ago_Hour", (int)t.TotalHours);
        if (t.TotalDays < 2) return L10n.Get("Ago_Yesterday");
        return L10n.Format("Ago_Days", (int)t.TotalDays);
    }

    /// <summary>First start: take over the devices (and custom names) TaskbarLogiBattery remembered.</summary>
    private static void MigrateLegacyDevices()
    {
        try
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RegistryRoot + @"\Devices");
            if (existing != null && existing.SubKeyCount > 0) return;
            using var legacy = Registry.CurrentUser.OpenSubKey(LegacyKey + @"\Devices");
            if (legacy == null) return;

            var aliases = new Dictionary<string, string>(S.LogiAliases);
            foreach (string id in legacy.GetSubKeyNames())
            {
                using var src = legacy.OpenSubKey(id);
                using var dst = Registry.CurrentUser.CreateSubKey(RegistryRoot + @"\Devices\" + id);
                if (src == null) continue;
                foreach (string name in src.GetValueNames())
                {
                    object? v = src.GetValue(name);
                    if (v == null) continue;
                    if (name == "Alias" && v is string a) aliases[id] = a;
                    else dst.SetValue(name, v, src.GetValueKind(name));
                }
            }
            S.LogiAliases = aliases;
            Core.Log.Info("Imported Logitech devices from TaskbarLogiBattery");
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Logitech device import failed", ex);
        }
    }

    public void Dispose() => StopEngine();
}
