#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AuraFlyout.Modules.LogiBattery
{
    // Finds Logitech devices, keeps their battery and connection state current and reports changes.
    // All device I/O runs on one worker thread; HID reader threads only post work to it.
    class DeviceManager : IDisposable
    {
        const ushort Logitech = 0x046D;
        static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(20);
        static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(4);
        static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(8);   // cable <-> wireless hand-over, short drop-outs

        public event Action<List<LogiDevice>> Changed;   // raised on the worker thread

        public TimeSpan PollInterval = TimeSpan.FromSeconds(60);
        public string RegistryKey;                        // known devices are remembered here

        readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        readonly Thread worker;
        readonly Dictionary<string, Endpoint> endpoints = new Dictionary<string, Endpoint>();
        readonly Dictionary<string, Record> records = new Dictionary<string, Record>();
        DateTime rescanAt = DateTime.MinValue;
        volatile bool stopping;
        bool dirty;

        public DeviceManager()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "Device worker" };
        }

        public void Start() { worker.Start(); }

        // Device arrival/removal (WM_DEVICECHANGE); coalesced.
        public void RequestRescan()
        {
            Post(() => rescanAt = DateTime.UtcNow.AddMilliseconds(700));
        }

        // Reads every connected device now.
        public void RefreshNow()
        {
            Post(() =>
            {
                foreach (Source s in AllSources()) s.NextCheck = DateTime.UtcNow;
                rescanAt = DateTime.UtcNow;
            });
        }

        public void Forget(string id)
        {
            Post(() =>
            {
                Record r;
                if (records.TryGetValue(id, out r) && !r.Device.Connected)
                {
                    records.Remove(id);
                    DeleteRemembered(id);
                    dirty = true;
                }
            });
        }

        void Post(Action a)
        {
            if (!stopping) work.Add(a);
        }

        // --- worker ---

        class Record
        {
            public LogiDevice Device = new LogiDevice();
            public DateTime? OfflineSince;    // all sources offline, waiting out the grace period
            public bool Verified;             // battery read at least once (in this or an earlier session)
        }

        void Run()
        {
            LoadRemembered();
            Rescan();
            Publish();
            while (!stopping)
            {
                Action a;
                if (work.TryTake(out a, 250))
                {
                    try { a(); }
                    catch (Exception ex) { Log.Write("work item: " + ex); }
                    if (work.Count > 0) continue;
                }
                try { Tick(); }
                catch (Exception ex) { Log.Write("tick: " + ex); }
            }
        }

        void Tick()
        {
            DateTime now = DateTime.UtcNow;
            if (rescanAt != DateTime.MinValue && now >= rescanAt)
            {
                rescanAt = DateTime.MinValue;
                Rescan();
            }
            foreach (Source s in AllSources())
            {
                if (now < s.NextCheck) continue;
                Check(s);
                if (work.Count > 0) break;   // keep notifications snappy
            }
            foreach (Record r in records.Values) UpdateConnected(r);
            if (dirty) Publish();
        }

        IEnumerable<Source> AllSources()
        {
            return endpoints.Values.SelectMany(e => e.Sources).ToList();
        }

        // Reads one source's battery; failures twice in a row mark it offline.
        void Check(Source s)
        {
            if (s.Endpoint.IsClosed) return;
            s.LastCheck = DateTime.UtcNow;
            BatteryReading b = null;
            try
            {
                if (!s.Ready) s.Ready = s.Init();
                if (s.Ready) b = s.Read();
            }
            catch (Exception ex) { Log.Write("read " + s + ": " + ex.Message); }

            DateTime now = DateTime.UtcNow;
            if (b != null)
            {
                s.Failures = 0;
                s.Online = true;
                s.NextCheck = now + PollInterval;
                Apply(s, b);
            }
            else
            {
                s.Failures++;
                bool wasOnline = s.Online;
                if (s.Failures >= 2 || !s.Online || s.HardOffline) s.Online = false;
                s.HardOffline = false;
                s.NextCheck = now + (s.Online ? FailureRetry : OfflineRetry);
                if (wasOnline && !s.Online) Log.Write(s + " offline");
            }
        }

        void Apply(Source s, BatteryReading b)
        {
            Record r = RecordFor(s);
            r.Verified = true;
            LogiDevice d = r.Device;
            d.LastSeen = DateTime.UtcNow;
            if (!b.SameAs(d.Battery) || !d.Connected || d.Connection != s.Connection)
            {
                d.Battery = b;
                d.Connection = s.Connection;
                dirty = true;
                Remember(d);
            }
            UpdateConnected(r);
        }

        Record RecordFor(Source s)
        {
            Record r;
            if (!records.TryGetValue(s.DeviceId, out r))
            {
                r = new Record();
                r.Device.Id = s.DeviceId;
                records[s.DeviceId] = r;
            }
            if (!string.IsNullOrEmpty(s.Name) && r.Device.Name != s.Name)
            {
                r.Device.Name = s.Name;
                dirty = true;
            }
            if (s.Kind != DeviceKind.Other && r.Device.Kind != s.Kind)
            {
                r.Device.Kind = s.Kind;
                dirty = true;
            }
            return r;
        }

        // A device is connected while any of its sources is online (it may be on a cable and a receiver).
        void UpdateConnected(Record r)
        {
            bool online = AllSources().Any(s => s.Online && s.DeviceId == r.Device.Id);
            if (online)
            {
                r.OfflineSince = null;
                if (!r.Device.Connected && r.Verified)
                {
                    r.Device.Connected = true;
                    dirty = true;
                }
                return;
            }
            if (!r.Device.Connected) return;
            if (r.OfflineSince == null) r.OfflineSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - r.OfflineSince.Value >= DisconnectGrace)
            {
                r.Device.Connected = false;
                r.OfflineSince = null;
                dirty = true;
                Remember(r.Device);
            }
        }

        void Publish()
        {
            dirty = false;
            var list = records.Values.Where(r => r.Verified).Select(r => r.Device.Clone())
                .OrderBy(d => d.Kind == DeviceKind.Headset ? 0 : d.Kind == DeviceKind.Keyboard ? 1 : d.Kind == DeviceKind.Mouse ? 2 : 3)
                .ThenBy(d => d.Name).ToList();
            var h = Changed;
            if (h != null) h(list);
        }

        // --- discovery ---

        void Rescan()
        {
            List<HidInfo> infos;
            try { infos = Hid.Enumerate(Logitech); }
            catch (Exception ex) { Log.Write("enumerate: " + ex.Message); return; }
            var groups = infos.GroupBy(i => i.InterfaceKey).ToDictionary(g => g.Key, g => g.ToList());

            foreach (string key in endpoints.Keys.ToList())
            {
                Endpoint e = endpoints[key];
                if (groups.ContainsKey(key) && !e.IsClosed) continue;
                Log.Write("removed: " + e);
                e.Dispose();
                endpoints.Remove(key);
            }

            foreach (var g in groups)
            {
                if (endpoints.ContainsKey(g.Key)) continue;
                Endpoint e = null;
                try
                {
                    HidInfo centurion = g.Value.Find(i => i.UsagePage == CenturionEndpoint.UsagePage && i.OutputLength >= 64);
                    if (centurion != null) e = CenturionDevice.Open(this, centurion);
                    if (e == null) e = HidppDevice.Open(this, g.Value);
                }
                catch (Exception ex) { Log.Write("open " + g.Key + ": " + ex.Message); }
                if (e == null) continue;
                Log.Write("added: " + e);
                endpoints[g.Key] = e;
            }
        }

        // --- persistence (HKCU\...\Devices\<id>) ---

        void LoadRemembered()
        {
            if (RegistryKey == null) return;
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(RegistryKey + @"\Devices"))
            {
                if (root == null) return;
                foreach (string id in root.GetSubKeyNames())
                {
                    using (RegistryKey k = root.OpenSubKey(id))
                    {
                        var d = new LogiDevice
                        {
                            Id = id,
                            Name = k.GetValue("Name") as string ?? id,
                            Kind = (DeviceKind)(k.GetValue("Kind") as int? ?? 0),
                            Connection = k.GetValue("Connection") as string,
                        };
                        object seen = k.GetValue("LastSeen");
                        if (seen is long) d.LastSeen = DateTime.FromBinary((long)seen);
                        if (k.GetValue("Percent") is int)
                            d.Battery = new BatteryReading
                            {
                                Percent = (int)k.GetValue("Percent"),
                                State = (ChargeState)(k.GetValue("State") as int? ?? 0),
                                Approximate = (k.GetValue("Approximate") as int? ?? 0) != 0
                            };
                        records[id] = new Record { Device = d, Verified = true };
                    }
                }
            }
        }

        void Remember(LogiDevice d)
        {
            if (RegistryKey == null) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegistryKey + @"\Devices\" + d.Id))
                {
                    k.SetValue("Name", d.Name ?? d.Id);
                    k.SetValue("Kind", (int)d.Kind, RegistryValueKind.DWord);
                    if (d.Connection != null) k.SetValue("Connection", d.Connection);
                    k.SetValue("LastSeen", d.LastSeen.ToBinary(), RegistryValueKind.QWord);
                    if (d.Battery != null)
                    {
                        k.SetValue("Percent", d.Battery.Percent, RegistryValueKind.DWord);
                        k.SetValue("State", (int)d.Battery.State, RegistryValueKind.DWord);
                        k.SetValue("Approximate", d.Battery.Approximate ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex) { Log.Write("remember: " + ex.Message); }
        }

        void DeleteRemembered(string id)
        {
            if (RegistryKey == null) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(RegistryKey + @"\Devices\" + id, false); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            stopping = true;
            work.CompleteAdding();
            if (worker.IsAlive) worker.Join(2000);
            foreach (Endpoint e in endpoints.Values) e.Dispose();
        }

        // ===================================================================================
        // Endpoints (one per USB interface) and sources (one per device behind it)
        // ===================================================================================

        abstract class Endpoint : IDisposable
        {
            public readonly List<Source> Sources = new List<Source>();
            public abstract bool IsClosed { get; }
            public abstract void Dispose();
        }

        abstract class Source
        {
            public Endpoint Endpoint;
            public string DeviceId;
            public string Name;
            public DeviceKind Kind;
            public string Connection;
            public bool Ready, Online, HardOffline;
            public int Failures;
            public DateTime NextCheck = DateTime.MinValue, LastCheck = DateTime.MinValue;

            public abstract bool Init();             // feature discovery; false while unreachable
            public abstract BatteryReading Read();   // null when unreachable
        }

        // --- HID++ (receivers, cabled devices, older headsets) ---

        class HidppDevice : Endpoint
        {
            DeviceManager owner;
            public HidppEndpoint Ep;
            public bool Receiver;

            public override bool IsClosed { get { return Ep.IsClosed; } }

            public static Endpoint Open(DeviceManager owner, List<HidInfo> sameInterface)
            {
                HidppEndpoint ep = HidppEndpoint.Open(sameInterface);
                if (ep == null) return null;
                var dev = new HidppDevice { owner = owner, Ep = ep };

                // A device answers a root ping at index 0xFF; a receiver rejects it with a HID++ 1.0 error.
                HidppReply ping = ep.Request(0xFF, 0, 1, 600, 0, 0, 0x5A);
                if (ping != null && ping.Error >= 0x100) dev.Receiver = true;
                else if (ping == null && sameInterface.Any(i => i.Product != null && i.Product.IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0))
                    dev.Receiver = true;

                if (dev.Receiver) dev.AddPairedSlots();
                else dev.Sources.Add(new HidppSource(dev, 0xFF) { Connection = ConnectionName(ep.Info.Pid) });

                ep.Notification += r => owner.Post(() => dev.OnNotification(r));
                ep.Closed += () => owner.Post(() => owner.rescanAt = DateTime.UtcNow);
                return dev;
            }

            // Receiver slots from the pairing registers (Unifying / LIGHTSPEED); for receivers without them
            // (Bolt) all six slots are tried and the empty ones simply stay offline.
            void AddPairedSlots()
            {
                for (byte slot = 1; slot <= 6; slot++)
                {
                    HidppReply pair = Ep.Register(0xFF, 0x83, 0xB5, 500, (byte)(0x1F + slot));
                    if (pair == null || !pair.Ok) continue;   // empty slot, or no pairing registers
                    int wpid = (pair[7] << 8) | pair[8];
                    if (wpid == 0) continue;
                    var s = new HidppSource(this, slot)
                    {
                        Wpid = wpid,
                        DeviceId = "w" + wpid.ToString("X4"),
                        Kind = KindFromReceiver(pair[11] & 0x0F),
                        Connection = ConnectionName(Ep.Info.Pid)
                    };
                    HidppReply name = Ep.Register(0xFF, 0x83, 0xB5, 500, (byte)(0x3F + slot));
                    if (name != null && name.Ok)
                    {
                        int len = Math.Min((int)name[5], 14);
                        var sb = new StringBuilder();
                        for (int i = 0; i < len; i++) sb.Append((char)name[6 + i]);
                        s.Name = KnownModels.Name(wpid, sb.ToString().Trim());
                    }
                    Sources.Add(s);
                }
                if (Sources.Count == 0)
                    for (byte slot = 1; slot <= 6; slot++)
                        Sources.Add(new HidppSource(this, slot) { Connection = ConnectionName(Ep.Info.Pid) });
            }

            public void RenameSource(Source s, string id) { owner.Rename(s, id); }

            void OnNotification(byte[] r)
            {
                byte index = r[1];
                HidppSource s = Sources.OfType<HidppSource>().FirstOrDefault(x => x.Index == index);

                // Receiver: device (dis)connected. r[4] bit 6 = link not established; r[5..6] = wireless pid.
                if (r[2] == 0x41 && Receiver)
                {
                    bool up = (r[4] & 0x40) == 0;
                    int wpid = (r[6] << 8) | r[5];
                    Log.Write("slot " + index + " link " + (up ? "up" : "down") + " wpid=" + wpid.ToString("X4"));
                    if (s == null)
                    {
                        if (!up || wpid == 0) return;
                        s = new HidppSource(this, index) { Wpid = wpid, DeviceId = "w" + wpid.ToString("X4"), Connection = ConnectionName(Ep.Info.Pid) };
                        Sources.Add(s);
                    }
                    if (up)
                    {
                        s.Ready = false;   // may have been re-paired or rebooted: rediscover features
                        s.NextCheck = DateTime.UtcNow.AddMilliseconds(1500);
                    }
                    else
                    {
                        s.Online = false;
                        s.NextCheck = DateTime.UtcNow + OfflineRetry;
                    }
                    return;
                }
                // Anything the device itself sends (a late reply, an event) means it is awake: look now.
                if (s != null && !s.Online && r[2] != 0x8F && r[2] != 0xFF && r[2] != 0x41)
                {
                    DateTime soon = DateTime.UtcNow.AddMilliseconds(300);
                    if (s.NextCheck > soon) s.NextCheck = soon;
                }
                if (s == null || !s.Ready || (r[3] & 0x0F) != 0) return;   // only events (software id 0)

                if (r[2] == s.BatteryIndex && r.Length >= 8)
                {
                    BatteryReading b = s.Parse(r);
                    if (b != null)
                    {
                        s.Online = true;
                        s.Failures = 0;
                        owner.Apply(s, b);
                    }
                }
                else if (r[2] == s.WirelessIndex)
                {
                    // 0x1D4B wireless device status: the device (re)connected.
                    s.Ready = false;
                    s.NextCheck = DateTime.UtcNow.AddMilliseconds(1500);
                }
            }

            public override void Dispose() { Ep.Dispose(); }

            public override string ToString() { return "HID++ " + Ep.Info + (Receiver ? " (receiver)" : ""); }
        }

        class HidppSource : Source
        {
            readonly HidppDevice dev;
            public readonly byte Index;
            public int Wpid;
            public byte BatteryIndex, WirelessIndex;
            ushort batteryFeature;
            bool socSupported = true;

            public HidppSource(HidppDevice dev, byte index)
            {
                this.dev = dev;
                Endpoint = dev;
                Index = index;
                DeviceId = "p" + dev.Ep.Info.Pid.ToString("X4") + (index != 0xFF ? "." + index : "");
            }

            HidppEndpoint Ep { get { return dev.Ep; } }

            byte FeatureIndex(ushort feature)
            {
                HidppReply r = Ep.Request(Index, 0, 0, 1000, (byte)(feature >> 8), (byte)feature);
                if (r == null) throw new TimeoutException();
                if (!r.Ok)
                {
                    if (r.Error >= 0x100) throw new TimeoutException();   // receiver: device not reachable
                    return 0;
                }
                return r[4];
            }

            public override bool Init()
            {
                // Is anybody there? (fast: the receiver answers for absent devices)
                // A sleeping wireless device takes a second or two to wake up and answer; the receiver answers
                // at once for an absent one, so the long timeout only costs time when it is needed.
                HidppReply ping = Ep.Request(Index, 0, 1, 2500, 0, 0, 0x5A);
                if (ping == null || !ping.Ok) return false;
                if (ping[4] < 2) return false;   // HID++ 1.0 devices are not supported

                try
                {
                    byte nameIndex = FeatureIndex(Feature.DeviceName);
                    string name = nameIndex != 0 ? ReadName(nameIndex) : null;
                    DeviceKind kind = Kind;
                    if (nameIndex != 0)
                    {
                        HidppReply t = Ep.Request(Index, nameIndex, 2, 1000);
                        if (t != null && t.Ok) kind = KindFromHidpp(t[4]);
                    }

                    // Device information: the wireless pid identifies the device on any connection.
                    byte infoIndex = FeatureIndex(0x0003);
                    if (infoIndex != 0 && Wpid == 0)
                    {
                        HidppReply info = Ep.Request(Index, infoIndex, 0, 1000);
                        if (info != null && info.Ok) Wpid = WirelessPid(info.Data);
                    }

                    batteryFeature = 0;
                    foreach (ushort f in new[] { Feature.UnifiedBattery, Feature.BatteryStatus, Feature.BatteryVoltage, Feature.AdcMeasurement })
                    {
                        byte idx = FeatureIndex(f);
                        if (idx == 0) continue;
                        BatteryIndex = idx;
                        batteryFeature = f;
                        break;
                    }
                    if (batteryFeature == 0) return false;
                    if (batteryFeature == Feature.UnifiedBattery)
                    {
                        HidppReply caps = Ep.Request(Index, BatteryIndex, 0, 1000);
                        socSupported = caps == null || !caps.Ok || (caps[5] & 0x02) != 0;
                    }
                    WirelessIndex = FeatureIndex(Feature.WirelessStatus);

                    string id = Wpid != 0 ? "w" + Wpid.ToString("X4") : DeviceId;
                    if (id != DeviceId) dev.RenameSource(this, id);
                    Name = KnownModels.Name(Wpid != 0 ? Wpid : dev.Ep.Info.Pid, name ?? Name ?? dev.Ep.Info.Product);
                    if (kind != DeviceKind.Other) Kind = kind;
                    if (Index == 0xFF && Kind == DeviceKind.Headset) Connection = "LIGHTSPEED";   // headset behind its own dongle
                    Log.Write("init " + this + ": " + Name + " battery=" + batteryFeature.ToString("X4") + "@" + BatteryIndex);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }

            string ReadName(byte idx)
            {
                HidppReply n = Ep.Request(Index, idx, 0, 1000);
                if (n == null || !n.Ok) return null;
                int len = Math.Min((int)n[4], 64);
                var sb = new StringBuilder();
                while (sb.Length < len)
                {
                    HidppReply part = Ep.Request(Index, idx, 1, 1000, (byte)sb.Length);
                    if (part == null || !part.Ok) break;
                    int before = sb.Length;
                    for (int i = 4; i < part.Data.Length && sb.Length < len; i++)
                    {
                        if (part.Data[i] == 0) { len = sb.Length; break; }
                        sb.Append((char)part.Data[i]);
                    }
                    if (sb.Length == before) break;
                }
                string s = sb.ToString().Trim();
                return s.Length > 0 ? s : null;
            }

            // 0x0003 getDeviceInfo: [9..10] transport bits (BT, BTLE, eQuad, USB), then one model id per transport.
            static int WirelessPid(byte[] r)
            {
                int transports = (r[9] << 8) | r[10];
                int slot = 0;
                for (int bit = 0; bit < 4; bit++)
                {
                    if ((transports & (1 << bit)) == 0) continue;
                    int id = (r[11 + slot * 2] << 8) | r[12 + slot * 2];
                    if (bit == 2) return id;
                    slot++;
                    if (slot >= 3) break;
                }
                return 0;
            }

            public override BatteryReading Read()
            {
                int function = batteryFeature == Feature.UnifiedBattery ? 1 : 0;
                HidppReply r = Ep.Request(Index, BatteryIndex, function, 2500);
                if (r == null) return null;
                if (!r.Ok)
                {
                    if (r.Error >= 0x100) HardOffline = true;   // receiver says the device is gone
                    else Ready = false;                           // feature table changed? rediscover
                    return null;
                }
                return Parse(r.Data);
            }

            // Same layout for the reply and for the device's own battery events.
            public BatteryReading Parse(byte[] r)
            {
                var b = new BatteryReading();
                switch (batteryFeature)
                {
                    case Feature.UnifiedBattery:
                        b.Percent = r[4];
                        if (!socSupported || b.Percent == 0 && r[5] != 0)
                        {
                            b.Approximate = true;
                            b.Percent = (r[5] & 8) != 0 ? 100 : (r[5] & 4) != 0 ? 50 : (r[5] & 2) != 0 ? 20 : 5;
                        }
                        b.State = r[6] == 1 || r[6] == 2 ? ChargeState.Charging : r[6] == 3 ? ChargeState.Full
                                : r[6] == 4 ? ChargeState.Error : ChargeState.Discharging;
                        break;
                    case Feature.BatteryStatus:
                        b.Percent = r[4];
                        b.State = r[6] == 1 || r[6] == 2 || r[6] == 4 ? ChargeState.Charging : r[6] == 3 ? ChargeState.Full
                                : r[6] >= 5 ? ChargeState.Error : ChargeState.Discharging;
                        if (b.State == ChargeState.Full && b.Percent == 0) b.Percent = 100;
                        break;
                    case Feature.BatteryVoltage:
                        b.Millivolts = (r[4] << 8) | r[5];
                        b.Percent = BatteryReading.PercentFromVoltage(b.Millivolts);
                        if ((r[6] & 0x80) != 0)
                        {
                            int c = r[6] & 0x07;
                            b.State = c == 0 ? ChargeState.Charging : c == 1 ? ChargeState.Full : c == 7 ? ChargeState.Error : ChargeState.Discharging;
                        }
                        break;
                    case Feature.AdcMeasurement:
                        b.Millivolts = (r[4] << 8) | r[5];
                        if (b.Millivolts < 1000) return null;   // headset off: the dongle reports a leftover few mV
                        b.Percent = BatteryReading.PercentFromVoltage(b.Millivolts);
                        b.State = (r[6] & 0x08) != 0 ? ChargeState.Error : (r[6] & 0x04) != 0 ? ChargeState.Full
                                : (r[6] & 0x02) != 0 ? ChargeState.Charging : ChargeState.Discharging;
                        break;
                    default:
                        return null;
                }
                b.Percent = Math.Max(0, Math.Min(100, b.Percent));
                return b;
            }

            public override string ToString() { return DeviceId + "@" + Index.ToString("X2"); }
        }

        // --- Centurion (PRO X 2 LIGHTSPEED and other new headsets) ---

        class CenturionDevice : Endpoint
        {
            public CenturionEndpoint Ep;
            public override bool IsClosed { get { return Ep.IsClosed; } }

            public static Endpoint Open(DeviceManager owner, HidInfo info)
            {
                CenturionEndpoint ep = CenturionEndpoint.Open(info);
                if (ep == null) return null;
                var dev = new CenturionDevice { Ep = ep };
                var s = new CenturionSource
                {
                    Endpoint = dev,
                    Ep = ep,
                    DeviceId = "c" + info.Pid.ToString("X4"),
                    Name = KnownModels.Name(info.Pid, info.Product),
                    Kind = DeviceKind.Headset,
                    Connection = "LIGHTSPEED"
                };
                dev.Sources.Add(s);
                ep.Notification += f => owner.Post(() =>
                {
                    BatteryReading b = ep.ParseBatteryEvent(f);
                    if (b != null)
                    {
                        s.Online = true;
                        s.Failures = 0;
                        owner.Apply(s, b);
                        return;
                    }
                    // Power on/off and other events: look again shortly (but not on every telemetry frame).
                    DateTime soon = DateTime.UtcNow.AddMilliseconds(1200);
                    if (s.NextCheck > soon && DateTime.UtcNow - s.LastCheck > TimeSpan.FromSeconds(5)) s.NextCheck = soon;
                });
                ep.Closed += () => owner.Post(() => owner.rescanAt = DateTime.UtcNow);
                return dev;
            }

            public override void Dispose() { Ep.Dispose(); }
            public override string ToString() { return "Centurion " + Ep.Info; }
        }

        class CenturionSource : Source
        {
            public CenturionEndpoint Ep;
            public override bool Init() { return true; }
            public override BatteryReading Read() { return Ep.ReadBattery(); }
            public override string ToString() { return DeviceId; }
        }

        // A cabled device learned its wireless pid: from now on it is the same device as on the receiver.
        void Rename(Source s, string id)
        {
            Record old;
            if (records.TryGetValue(s.DeviceId, out old) && !AllSources().Any(x => x != s && x.DeviceId == s.DeviceId))
            {
                records.Remove(s.DeviceId);
                if (old.Verified) DeleteRemembered(s.DeviceId);
            }
            s.DeviceId = id;
            dirty = true;
        }

        // --- helpers ---

        static string ConnectionName(ushort pid)
        {
            switch (pid)
            {
                case 0xC52B: case 0xC532: return "Unifying";
                case 0xC548: return "Bolt";
                case 0xC539: case 0xC53A: case 0xC53F: case 0xC541: case 0xC545: case 0xC547: case 0xC54D: return "LIGHTSPEED";
                default: return pid >= 0xC500 && pid <= 0xC5FF ? "Receiver" : "USB";
            }
        }

        static DeviceKind KindFromReceiver(int k)
        {
            switch (k)
            {
                case 1: return DeviceKind.Keyboard;
                case 2: return DeviceKind.Mouse;
                case 3: return DeviceKind.Numpad;
                case 4: return DeviceKind.Presenter;
                case 8: return DeviceKind.Trackball;
                default: return DeviceKind.Other;
            }
        }

        static DeviceKind KindFromHidpp(int t)
        {
            switch (t)
            {
                case 0: return DeviceKind.Keyboard;
                case 2: return DeviceKind.Numpad;
                case 3: case 4: return DeviceKind.Mouse;
                case 5: return DeviceKind.Trackball;
                case 6: return DeviceKind.Presenter;
                case 8: return DeviceKind.Headset;
                case 9: return DeviceKind.Webcam;
                case 10: case 18: return DeviceKind.Wheel;
                case 11: case 12: return DeviceKind.Gamepad;
                case 14: return DeviceKind.Speaker;
                default: return DeviceKind.Other;
            }
        }
    }

    // Marketing names for devices whose firmware reports something less helpful.
    static class KnownModels
    {
        static readonly Dictionary<int, string> names = new Dictionary<int, string>
        {
            { 0x40A9, "PRO X SUPERLIGHT 2" }, { 0xC09B, "PRO X SUPERLIGHT 2" },
            { 0x4093, "PRO X SUPERLIGHT" },   { 0xC094, "PRO X SUPERLIGHT" },
            { 0x0AF7, "PRO X 2 LIGHTSPEED" },
        };

        public static string Name(int pid, string reported)
        {
            string n;
            if (names.TryGetValue(pid, out n)) return n;
            return string.IsNullOrEmpty(reported) ? null : reported;
        }
    }
}
