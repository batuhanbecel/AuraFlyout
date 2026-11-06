using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using AuraFlyout.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using Log = AuraFlyout.Core.Log;

namespace AuraFlyout.Modules.Bluetooth;

/// <summary>A paired Bluetooth headset / speaker and whether it is connected right now.</summary>
public sealed partial class BluetoothAudioDevice : ObservableObject
{
    public BluetoothAudioDevice(string name) => Name = name;

    public string Name { get; }
    internal List<string> EndpointIds { get; } = [];

    [ObservableProperty] public partial bool Connected { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
}

/// <summary>
/// Connects and disconnects paired Bluetooth audio devices with one click, through the same switch
/// the Windows sound settings and the quick settings use: the Bluetooth audio driver's one-shot
/// reconnect / disconnect property (KSPROPSETID_BtAudio), reached over the endpoint's device topology.
/// </summary>
public sealed class BluetoothAudio
{
    public ObservableCollection<BluetoothAudioDevice> Devices { get; } = [];

    public event Action? Changed;

    /// <summary>Lists the paired Bluetooth audio endpoints (connected or not), one row per device.</summary>
    public void Refresh()
    {
        Task.Run(() =>
        {
            var found = new Dictionary<string, (bool Connected, List<string> Ids)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Unplugged))
                {
                    try
                    {
                        string? connectedTo = ConnectedDeviceId(d.ID);
                        if (connectedTo == null || !(connectedTo.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                                                     connectedTo.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase))) continue;
                        // "LBS-R116" and "LBS-R116 Hands-Free" are the same headset
                        string name = d.DeviceFriendlyName.Replace(" Hands-Free AG Audio", "").Replace(" Hands-Free", "").Trim();
                        if (!found.TryGetValue(name, out var entry)) entry = (false, []);
                        entry.Ids.Add(d.ID);
                        entry.Connected |= d.State == DeviceState.Active;
                        found[name] = entry;
                    }
                    finally
                    {
                        d.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Bluetooth audio devices could not be listed", ex);
            }
            return found;
        }).ContinueWith(t =>
        {
            var found = t.Result;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                for (int i = Devices.Count - 1; i >= 0; i--)
                    if (!found.ContainsKey(Devices[i].Name)) Devices.RemoveAt(i);
                foreach (var (name, (connected, ids)) in found.OrderBy(f => f.Key))
                {
                    var dev = Devices.FirstOrDefault(d => d.Name == name);
                    if (dev == null) Devices.Add(dev = new BluetoothAudioDevice(name));
                    dev.EndpointIds.Clear();
                    dev.EndpointIds.AddRange(ids);
                    if (!dev.Busy) dev.Connected = connected;
                }
                Changed?.Invoke();
            });
        });
    }

    /// <summary>Connects a disconnected device, disconnects a connected one.</summary>
    public void Toggle(BluetoothAudioDevice dev)
    {
        if (dev.Busy) return;
        dev.Busy = true;
        Changed?.Invoke();
        bool connect = !dev.Connected;
        var ids = dev.EndpointIds.ToList();
        Task.Run(() =>
        {
            bool ok = false;
            foreach (string id in ids)
            {
                try { ok |= SendOneShot(id, connect ? 0u : 1u); }
                catch (Exception ex) { Log.Warn($"Bluetooth {(connect ? "connect" : "disconnect")} failed for {dev.Name}", ex); }
                if (ok && connect) break; // one profile reconnects the whole headset
            }
            return ok;
        }).ContinueWith(_ =>
        {
            // the driver needs a moment; the endpoint state tells the truth afterwards
            Task.Delay(connect ? 4000 : 1500).ContinueWith(__ => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                dev.Busy = false;
                Refresh();
                App.Bluetooth?.RefreshNow(); // connected or not, and the charge
            }));
        });
    }

    // ---------- COM: endpoint → device topology → the Bluetooth filter's KS control ----------

    private static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint KSPROPERTY_TYPE_GET = 1;
    private const int CLSCTX_ALL = 0x17;

    private static string? ConnectedDeviceId(string endpointId)
    {
        var connector = Connector(endpointId);
        if (connector == null) return null;
        try
        {
            connector.GetDeviceIdConnectedTo(out string id);
            return id;
        }
        catch
        {
            return null;
        }
    }

    // by CLSID: NAudio already declares a class for the same CLSID, so "new" would hand back its type
    private static IMMDeviceEnumerator NewEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    private static IConnector? Connector(string endpointId)
    {
        if (NewEnumerator().GetDevice(endpointId, out var device) != 0) return null;
        var iid = typeof(IDeviceTopology).GUID;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o) != 0) return null;
        var topology = (IDeviceTopology)o;
        return topology.GetConnector(0, out var connector) == 0 ? connector : null;
    }

    /// <summary>
    /// The KS control of the Bluetooth audio filter an endpoint is wired to. The connector's other
    /// side names that filter as a device ("{2}.\\?\bthhfenum#…"); the filter device itself (not the
    /// part in the topology, which has no KS control) gives the IKsControl.
    /// </summary>
    private static IKsControl? FilterControl(string endpointId, out string filterId, out int hr)
    {
        filterId = "";
        hr = -1;
        string? id = ConnectedDeviceId(endpointId);
        if (id == null) return null;
        filterId = id;
        hr = NewEnumerator().GetDevice(id, out var filter);
        if (hr != 0) return null;
        var iid = typeof(IKsControl).GUID;
        hr = filter.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o);
        return hr == 0 ? (IKsControl)o : null;
    }

    /// <summary>0 = KSPROPERTY_ONESHOT_RECONNECT, 1 = KSPROPERTY_ONESHOT_DISCONNECT.</summary>
    private static bool SendOneShot(string endpointId, uint property)
    {
        var ks = FilterControl(endpointId, out string filterId, out int hr);
        if (ks == null)
        {
            Log.Warn($"Bluetooth audio: no KS control for {filterId} (0x{hr:X8})");
            return false;
        }
        var prop = new KSIDENTIFIER { Set = KSPROPSETID_BtAudio, Id = property, Flags = KSPROPERTY_TYPE_GET };
        hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KSIDENTIFIER>(), IntPtr.Zero, 0, out _);
        Log.Info($"Bluetooth audio {(property == 0 ? "connect" : "disconnect")} via {filterId}: 0x{hr:X8}");
        return hr >= 0;
    }

    /// <summary>Development (--test-btaudio): whether each endpoint's KS control can be reached, nothing sent.</summary>
    public string Probe(BluetoothAudioDevice dev) => string.Join("; ", dev.EndpointIds.Select(id =>
    {
        try
        {
            var ks = FilterControl(id, out string filterId, out int hr);
            return $"{filterId} ks={(ks != null ? "ok" : $"0x{hr:X8}")}";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }));

    [StructLayout(LayoutKind.Sequential)]
    private struct KSIDENTIFIER
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSIDENTIFIER property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
    }
}
