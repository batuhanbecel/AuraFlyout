using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AuraFlyout.Core;
using AuraFlyout.Modules.LogiBattery;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Devices.Enumeration;
using Log = AuraFlyout.Core.Log;

namespace AuraFlyout.Modules.Bluetooth;

/// <summary>
/// A Bluetooth device: connected ones with the charge Windows reports (if any), and paired
/// headsets / speakers that are not connected right now (their icon connects them).
/// </summary>
public sealed partial class BluetoothDeviceItem : ObservableObject, IBatteryDevice
{
    public BluetoothDeviceItem(string id) => Id = id;

    public string Id { get; }

    /// <summary>The name Windows gives the device (before a rename).</summary>
    public string SourceName { get; set; } = "";

    // the same shape as LogiDeviceItem, so the battery panel draws both with one card template
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "Bluetooth";
    [ObservableProperty] public partial string PercentText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBattery))] public partial int Percent { get; set; } = -1;
    [ObservableProperty] public partial bool Low { get; set; }
    [ObservableProperty] public partial bool Connected { get; set; } = true;
    [ObservableProperty] public partial bool Hidden { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }

    /// <summary>The headset / speaker behind it: its icon connects or disconnects it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] public partial BluetoothAudioDevice? Audio { get; set; }

    public bool CanToggle => Audio != null;
    public bool HasBattery => Percent >= 0;
    public bool Charging => false;
    public bool Full => false;
    public bool CanForget => false;
}

/// <summary>
/// Bluetooth devices exactly as Windows shows them in Settings › Bluetooth &amp; devices: the
/// system's own battery property on the device's PnP nodes (hands-free headsets, LE mice,
/// keyboards, controllers) for connected devices, connected ones without a charge too, and paired
/// headsets / speakers that can be connected with one click.
/// </summary>
public sealed class BluetoothBatteryService : IDisposable
{
    // DEVPKEY_Bluetooth_Battery (what Settings reads), DEVPKEY_Device_ContainerId, DEVPKEY_Device_FriendlyName
    private static readonly Guid BatteryKey = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private static readonly Guid ContainerKey = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
    private static readonly Guid FriendlyNameKey = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly Guid ThisComputer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");
    private static readonly string[] Enumerators = ["BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM"];

    private readonly DispatcherTimer _timer;
    private bool _busy, _again;

    /// <summary>Development (--test-bluetooth): log every device found, connected or not.</summary>
    public static bool Verbose { get; set; }

    public ObservableCollection<BluetoothDeviceItem> Devices { get; } = [];

    public event Action? Changed;

    public BluetoothBatteryService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => RefreshNow();
    }

    public void Start()
    {
        App.BluetoothAudio.Changed += () => Apply(_last);
        Batteries.OnNamesChanged(() => Apply(_last));
        _timer.Start();
        RefreshNow();
        App.BluetoothAudio.Refresh();
    }

    public void RefreshNow()
    {
        if (!SettingsStore.Current.BluetoothBatteryEnabled)
        {
            if (Devices.Count > 0)
            {
                Devices.Clear();
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
        Task.Run(ReadAsync).ContinueWith(t =>
        {
            var found = t.IsCompletedSuccessfully ? t.Result : [];
            if (t.Exception != null) Log.Warn("Bluetooth battery read failed", t.Exception);
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

    private sealed record Found(string Id, string Name, string Glyph, int Percent);

    private List<Found> _last = [];
    private HashSet<string> _shownElsewhere = [];

    /// <summary>
    /// Devices another list already shows with more detail (game controllers, by container id): left
    /// out here.
    /// </summary>
    public void Hide(IEnumerable<string> containerIds)
    {
        var ids = new HashSet<string>(containerIds.Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (ids.SetEquals(_shownElsewhere)) return;
        _shownElsewhere = ids;
        Apply(_last);
    }

    private void Apply(List<Found> found)
    {
        _last = found;
        if (!SettingsStore.Current.BluetoothBatteryEnabled) return;
        // a Logitech device that is also paired over Bluetooth is already shown by the Logitech list
        var logi = new HashSet<string>(App.Logi?.Devices.Select(d => d.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        var entries = found.Where(f => !logi.Contains(f.Name) && !_shownElsewhere.Contains(f.Id)).ToList();

        // paired headsets / speakers: their connected row gets the connect switch, the others a row
        var audio = App.BluetoothAudio.Devices.ToList();
        var audioFor = new Dictionary<string, BluetoothAudioDevice>();
        foreach (var a in audio)
        {
            var match = entries.FirstOrDefault(f => string.Equals(f.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null) audioFor[match.Id] = a;
            else entries.Add(new Found("audio:" + a.Name, a.Name, "", -2)); // -2: paired, not connected
        }

        for (int i = Devices.Count - 1; i >= 0; i--)
            if (!entries.Any(f => f.Id == Devices[i].Id)) Devices.RemoveAt(i);
        foreach (var f in entries)
        {
            var item = Devices.FirstOrDefault(d => d.Id == f.Id);
            if (item == null)
            {
                item = new BluetoothDeviceItem(f.Id);
                Devices.Add(item);
            }
            bool connected = f.Percent != -2;
            item.SourceName = f.Name;
            item.Name = Batteries.NameFor(f.Id, f.Name);
            item.Connected = connected;
            item.Hidden = Batteries.IsHidden(f.Id);
            item.Audio = audioFor.TryGetValue(f.Id, out var a) ? a : audio.FirstOrDefault(x => "audio:" + x.Name == f.Id);
            item.Glyph = item.Audio != null ? "" : f.Glyph; // a headset / speaker looks like one
            item.Busy = item.Audio?.Busy == true;
            item.Percent = f.Percent >= 0 ? f.Percent : -1;
            item.PercentText = f.Percent >= 0 ? LogiBatteryService.Pct(f.Percent) : "";
            item.Low = f.Percent >= 0 && f.Percent <= SettingsStore.Current.LogiLowThreshold;
            string state = item.Busy ? L10n.Get("BtAudio_Busy")
                : !connected ? L10n.Get("BtAudio_Off")
                : f.Percent >= 0 ? L10n.Get("Logi_OnBattery")
                : L10n.Get("BtAudio_On");
            item.StatusText = state + " · Bluetooth";
        }
        Changed?.Invoke();
    }

    private static async Task<List<Found>> ReadAsync()
    {
        // one entry per physical device (a headset has several profile nodes): grouped by container;
        // devices that report no charge are kept too (-1) and listed when connected
        var byContainer = new Dictionary<Guid, (int Percent, string Name)>();
        foreach (var (container, pct, name) in Nodes())
        {
            if (!byContainer.TryGetValue(container, out var have)) have = (-1, "");
            byContainer[container] = (Math.Max(have.Percent, pct), have.Name.Length > 0 ? have.Name : name);
        }

        var result = new List<Found>();
        foreach (var (container, (pct, nodeName)) in byContainer)
        {
            string name = nodeName, glyph = "";
            bool connected = false;
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync("{" + container + "}",
                    ["System.ItemNameDisplay", "System.Devices.Connected", "System.Devices.Category"], DeviceInformationKind.DeviceContainer);
                if (info.Properties.TryGetValue("System.Devices.Connected", out var c) && c is bool b) connected = b;
                if (info.Properties.TryGetValue("System.ItemNameDisplay", out var dn) && dn is string s && s.Length > 0) name = s;
                if (info.Properties.TryGetValue("System.Devices.Category", out var cat) && cat is string[] cats) glyph = GlyphFor(cats);
            }
            catch
            {
                // container not reachable: a device with a charge is connected, one without is unknown
                connected = pct >= 0;
            }
            if (Verbose) Log.Info($"Bluetooth device: {name} {pct}% connected={connected} container={container}");
            if (connected) result.Add(new Found(container.ToString(), name, glyph, pct));
        }
        return result.OrderBy(r => r.Percent < 0).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Every present Bluetooth device node: its container, charge (-1 if none) and name.</summary>
    private static IEnumerable<(Guid Container, int Percent, string Name)> Nodes()
    {
        foreach (string enumerator in Enumerators)
        {
            foreach (string id in PresentDevices(enumerator))
            {
                if (CM_Locate_DevNodeW(out int node, id, 0) != 0) continue;
                Guid container = Property(node, ContainerKey, 2) is { Length: 16 } g ? new Guid(g) : Guid.Empty;
                if (container == Guid.Empty || container == ThisComputer) continue;
                int pct = Property(node, BatteryKey, 2) is { Length: >= 1 } battery && battery[0] <= 100 ? battery[0] : -1;
                // the device node itself ("BTHLE\DEV_…") carries the device's own name
                string name = Property(node, FriendlyNameKey, 14) is { } n ? Utf16(n) : "";
                bool deviceNode = id.Contains("\\DEV_", StringComparison.OrdinalIgnoreCase);
                yield return (container, pct, deviceNode ? name : "");
            }
        }
    }

    /// <summary>
    /// The charge Windows reports for one Bluetooth device (by container) and its name, as Settings
    /// shows them; -1 when it reports none.
    /// </summary>
    public static (int Percent, string Name) ReadContainer(Guid container)
    {
        int pct = -1;
        string name = "";
        foreach (var n in Nodes())
        {
            if (n.Container != container) continue;
            pct = Math.Max(pct, n.Percent);
            if (n.Name.Length > 0) name = n.Name;
        }
        return (pct, name);
    }

    private static string GlyphFor(string[] categories)
    {
        string all = string.Join(" ", categories);
        if (all.Contains("Headphone", StringComparison.OrdinalIgnoreCase) || all.Contains("Headset", StringComparison.OrdinalIgnoreCase)
            || all.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return "";
        if (all.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) return "";
        if (all.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return "";
        if (all.Contains("Gaming", StringComparison.OrdinalIgnoreCase) || all.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)) return "";
        if (all.Contains("Phone", StringComparison.OrdinalIgnoreCase)) return "";
        if (all.Contains("Pen", StringComparison.OrdinalIgnoreCase)) return "";
        return ""; // Bluetooth
    }

    // ---------- CfgMgr32 ----------

    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const int CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int length, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out int type, [Out] byte[]? buffer, ref int size, int flags);

    private static IEnumerable<string> PresentDevices(string enumerator)
    {
        const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out int len, enumerator, flags) != CR_SUCCESS || len <= 1) return [];
        var buffer = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, len, flags) != CR_SUCCESS) return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[]? Property(int node, Guid fmtid, int pid)
    {
        var key = new DEVPROPKEY { fmtid = fmtid, pid = pid };
        int size = 0;
        int cr = CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size <= 0) return null;
        var buffer = new byte[size];
        return CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) == CR_SUCCESS ? buffer : null;
    }

    private static string Utf16(byte[] b) => System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');

    public void Dispose() => _timer.Stop();
}
