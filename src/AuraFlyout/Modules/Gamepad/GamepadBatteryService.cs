using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AuraFlyout.Core;
using AuraFlyout.Modules.Bluetooth;
using AuraFlyout.Modules.LogiBattery;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Gaming.Input;
using Log = AuraFlyout.Core.Log;

namespace AuraFlyout.Modules.Gamepad;

/// <summary>A game controller and its charge (same shape as the other battery cards).</summary>
public sealed partial class GamepadItem : ObservableObject, IBatteryDevice
{
    public GamepadItem(string id) => Id = id;

    /// <summary>The device container when known (stable across ports and reconnects), else a path.</summary>
    public string Id { get; }
    public string Glyph => ""; // Game

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string PercentText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBattery))] public partial int Percent { get; set; } = -1;
    [ObservableProperty] public partial bool Low { get; set; }
    [ObservableProperty] public partial bool Charging { get; set; }
    [ObservableProperty] public partial bool Full { get; set; }
    [ObservableProperty] public partial bool Hidden { get; set; }

    public bool HasBattery => Percent >= 0;
    public bool Connected => true;
    public bool CanForget => false;
}

/// <summary>
/// Battery levels of game controllers, over USB, the Xbox wireless adapter or Bluetooth, one entry
/// per controller:
/// - Xbox over Bluetooth: the charge Windows itself reports for the Bluetooth device (Settings ›
///   Bluetooth &amp; devices). Windows.Gaming.Input sees the same controller as a generic HID game
///   controller there, with a charge on a wrong scale (100 of 1000), so that copy is skipped.
/// - Xbox over USB or the wireless adapter: Windows.Gaming.Input's battery report.
/// - PlayStation (DualShock 4, DualSense, DualSense Edge): their own HID input report.
/// </summary>
public sealed class GamepadBatteryService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _busy, _again, _listening;
    private List<Reading> _last = [];

    public ObservableCollection<GamepadItem> Devices { get; } = [];

    public event Action? Changed;

    public GamepadBatteryService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => RefreshNow();
    }

    public void Start()
    {
        try
        {
            // Windows.Gaming.Input only lists controllers for a process that listens for them
            RawGameController.RawGameControllerAdded += OnControllersChanged;
            RawGameController.RawGameControllerRemoved += OnControllersChanged;
            _listening = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Game controllers cannot be listed", ex);
        }
        Batteries.OnNamesChanged(() => Apply(_last));
        _timer.Start();
        RefreshNow();
    }

    private void OnControllersChanged(object? sender, RawGameController e) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshNow);

    public void RefreshNow()
    {
        if (!SettingsStore.Current.GamepadBatteryEnabled)
        {
            if (Devices.Count > 0)
            {
                Devices.Clear();
                App.Bluetooth?.Hide([]);
                Changed?.Invoke();
            }
            return;
        }
        if (_busy)
        {
            _again = true;
            return;
        }
        _busy = true;
        Task.Run(Read).ContinueWith(t =>
        {
            var found = t.IsCompletedSuccessfully ? t.Result : [];
            if (t.Exception != null) Log.Warn("Game controller battery read failed", t.Exception);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Apply(found);
                _busy = false;
                if (_again)
                {
                    _again = false;
                    RefreshNow();
                }
            });
        });
    }

    private sealed record Reading(string Id, string Name, string Connection, int Percent, bool Charging, bool Full);

    private void Apply(List<Reading> found)
    {
        _last = found;
        for (int i = Devices.Count - 1; i >= 0; i--)
            if (!found.Any(f => f.Id == Devices[i].Id)) Devices.RemoveAt(i);
        foreach (var f in found)
        {
            var item = Devices.FirstOrDefault(d => d.Id == f.Id);
            if (item == null)
            {
                item = new GamepadItem(f.Id);
                Devices.Add(item);
            }
            item.Name = Batteries.NameFor(f.Id, f.Name);
            item.Hidden = Batteries.IsHidden(f.Id);
            item.Percent = f.Percent;
            item.PercentText = f.Percent >= 0 ? LogiBatteryService.Pct(f.Percent) : "";
            item.Charging = f.Charging;
            item.Full = f.Full;
            item.Low = f.Percent >= 0 && !f.Charging && f.Percent <= SettingsStore.Current.LogiLowThreshold;
            string state = f.Charging ? L10n.Get("Logi_Charging") : f.Full ? L10n.Get("Logi_Full") : L10n.Get("Logi_OnBattery");
            item.StatusText = $"{state} · {f.Connection}";
        }
        // the same controller paired over Bluetooth is shown here, once
        App.Bluetooth?.Hide(Devices.Select(d => d.Id));
        Changed?.Invoke();
    }

    private static readonly Regex VidPid = new(@"vid[_&](?:0002)?([0-9a-f]{4})&pid[_&]([0-9a-f]{4})", RegexOptions.IgnoreCase);

    private List<Reading> Read()
    {
        var list = new List<Reading>();
        var bluetoothPads = new HashSet<(int Vid, int Pid)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Hid.DevicePaths())
        {
            string p = path.ToLowerInvariant();
            bool sony = p.Contains("054c"), microsoft = p.Contains("045e");
            if (!sony && !microsoft) continue;
            try
            {
                Reading? r = null;
                if (sony) r = PlayStation.Read(path);
                else if (IsBluetooth(p)) r = XboxOverBluetooth(path, bluetoothPads);
                if (r != null && seen.Add(r.Id)) list.Add(r);
            }
            catch (Exception ex)
            {
                Log.Warn("Game controller could not be read", ex);
            }
        }
        if (_listening) ReadXbox(list, bluetoothPads);
        return list;
    }

    /// <summary>HID over Bluetooth: classic HID (1124) or HID over GATT (1812).</summary>
    private static bool IsBluetooth(string path) =>
        path.Contains("{00001124-0000-1000-8000-00805f9b34fb}") || path.Contains("{00001812-0000-1000-8000-00805f9b34fb}");

    /// <summary>An Xbox controller paired over Bluetooth: the charge Windows reports for it.</summary>
    private static Reading? XboxOverBluetooth(string path, HashSet<(int, int)> bluetoothPads)
    {
        using (var dev = Hid.Open(path))
        {
            if (dev == null || dev.UsagePage != 0x01 || dev.Usage is not (0x04 or 0x05)) return null;
            bluetoothPads.Add((dev.VendorId, dev.ProductId));
        }
        string container = Hid.ContainerId(path);
        if (!Guid.TryParse(container, out var guid)) return null;
        var (pct, name) = BluetoothBatteryService.ReadContainer(guid);
        return new Reading(container, name.Length > 0 ? name : "Xbox Wireless Controller", "Bluetooth", pct, false, false);
    }

    /// <summary>Xbox controllers on USB or the Xbox wireless adapter (Bluetooth ones are read above).</summary>
    private static void ReadXbox(List<Reading> list, HashSet<(int Vid, int Pid)> bluetoothPads)
    {
        foreach (var c in RawGameController.RawGameControllers)
        {
            try
            {
                if (c.HardwareVendorId == 0x054C) continue; // PlayStation: read from its own report
                if (bluetoothPads.Contains((c.HardwareVendorId, c.HardwareProductId))) continue; // the same pad, over Bluetooth
                var report = c.TryGetBatteryReport();
                if (report == null || report.Status == Windows.System.Power.BatteryStatus.NotPresent) continue;
                if (report.FullChargeCapacityInMilliwattHours is not int full || full <= 0 ||
                    report.RemainingCapacityInMilliwattHours is not int left) continue;
                int pct = Math.Clamp((int)Math.Round(left * 100.0 / full), 0, 100);
                bool charging = report.Status == Windows.System.Power.BatteryStatus.Charging;
                bool full100 = !charging && pct >= 100 && report.Status == Windows.System.Power.BatteryStatus.Idle;
                string connection = c.IsWireless ? L10n.Get("Gamepad_Wireless") : "USB";
                // a generic HID name ("HID-compliant game controller") says nothing: call it what it is
                string name = string.IsNullOrWhiteSpace(c.DisplayName) || c.DisplayName.Contains("HID", StringComparison.OrdinalIgnoreCase)
                    ? "Xbox Controller" : c.DisplayName;
                list.Add(new Reading(c.NonRoamableId, name, connection, pct, charging, full100));
            }
            catch (Exception ex)
            {
                Log.Warn("Xbox controller battery could not be read", ex);
            }
        }
    }

    /// <summary>DualShock 4 / DualSense input reports: where the charge sits and what it means.</summary>
    private static class PlayStation
    {
        public static Reading? Read(string path)
        {
            using var dev = Hid.Open(path);
            if (dev == null || dev.VendorId != 0x054C || dev.UsagePage != 0x01 || dev.Usage != 0x05) return null;
            bool dualSense = dev.ProductId is 0x0CE6 or 0x0DF2;
            bool dualShock = dev.ProductId is 0x05C4 or 0x09CC or 0x0BA0;
            if (!dualSense && !dualShock) return null;
            string name = dev.ProductId switch
            {
                0x0DF2 => "DualSense Edge",
                0x0CE6 => "DualSense",
                _ => "DualShock 4",
            };
            // Bluetooth: the report is longer (and only the long one carries the battery)
            bool bluetooth = dev.InputLength > 64 || path.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);
            if (bluetooth)
            {
                // Over Bluetooth both start with a short report; reading the calibration feature report
                // switches them to the full one (Steam and DS4Windows do the same).
                dev.GetFeature(dualSense ? (byte)0x05 : (byte)0x02);
            }

            var deadline = Environment.TickCount64 + 1500;
            while (Environment.TickCount64 < deadline)
            {
                var report = dev.ReadInput((int)Math.Max(50, deadline - Environment.TickCount64));
                if (report == null) break;
                int at = (dualSense, report[0], bluetooth) switch
                {
                    (true, 0x01, false) => 53,
                    (true, 0x31, true) => 54,
                    (false, 0x01, false) => 30,
                    (false, 0x11, true) => 32,
                    _ => -1,
                };
                if (at < 0 || at >= report.Length) continue;
                byte s = report[at];
                int level = s & 0x0F;
                int pct;
                bool charging, full;
                if (dualSense)
                {
                    int state = s >> 4; // 0 on battery, 1 charging, 2 full
                    if (state > 2) return null; // error / temperature: no trustworthy level
                    pct = state == 2 ? 100 : Math.Min(level * 10 + 5, 100);
                    charging = state == 1;
                    full = state == 2;
                }
                else
                {
                    bool cable = (s & 0x10) != 0;
                    pct = Math.Min(level * 100 / (cable ? 11 : 8), 100);
                    charging = cable && level < 11;
                    full = cable && level >= 11;
                }
                string container = Hid.ContainerId(path);
                return new Reading(container.Length > 0 ? container : path, name, bluetooth ? "Bluetooth" : "USB", pct, charging, full);
            }
            return null;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (!_listening) return;
        RawGameController.RawGameControllerAdded -= OnControllersChanged;
        RawGameController.RawGameControllerRemoved -= OnControllersChanged;
    }
}
