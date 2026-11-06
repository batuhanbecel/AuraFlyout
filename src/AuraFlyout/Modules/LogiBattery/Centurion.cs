#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;
using System.Collections.Generic;
using System.Threading;

namespace AuraFlyout.Modules.LogiBattery
{
    // Logitech's "Centurion" protocol, used by newer headsets (PRO X 2 LIGHTSPEED and friends) instead of HID++.
    // Frames are 64-byte reports with id 0x51 on the 0xFFA0 vendor collection:
    //   [0]=0x51 [1]=payload length + 1 [2]=flags [3..]=payload
    // The headset sits behind a "bridge" feature of the dongle, so battery requests are wrapped:
    //   payload = bridge index, 0x10|swid, size hi, size lo, then the sub-message 00, feature index, function|swid, args.
    // Feature discovery follows HeadsetControl (GPL-2.0); only the protocol facts are reused here.
    class CenturionEndpoint : IDisposable
    {
        public const ushort UsagePage = 0xFFA0;
        const byte ReportId = 0x51, SoftwareId = 0x0A;
        const ushort FeatureSet = 0x0001, Bridge = 0x0003, BatterySoc = 0x0104;

        public readonly HidInfo Info;
        public event Action<byte[]> Notification;   // unsolicited frames (battery / power events)
        public event Action Closed;

        readonly HidStream stream;
        readonly object requestLock = new object();
        readonly Queue<byte[]> inbox = new Queue<byte[]>();
        readonly AutoResetEvent arrived = new AutoResetEvent(false);
        volatile bool waiting, closed;

        // Discovered lazily; the fallback values are what PRO X 2 firmware uses.
        int bridgeIndex = -1, batteryIndex = -1;

        CenturionEndpoint(HidInfo info, HidStream stream)
        {
            Info = info;
            this.stream = stream;
            stream.Received += OnFrame;
            stream.Closed += _ =>
            {
                closed = true;
                var h = Closed;
                if (h != null) h();
            };
        }

        public static CenturionEndpoint Open(HidInfo info)
        {
            if (info.UsagePage != UsagePage || info.OutputLength < 64) return null;
            HidStream s = HidStream.Open(info);
            if (s == null) return null;
            var ep = new CenturionEndpoint(info, s);
            s.Start();
            return ep;
        }

        public bool IsClosed { get { return closed; } }

        void OnFrame(HidStream s, byte[] f)
        {
            if (f.Length < 4 || f[0] != ReportId) return;
            if (Log.Enabled || Log.Echo != null) Log.Write("  <= " + Log.Hex(f, 16));
            if (waiting)
            {
                lock (inbox) inbox.Enqueue(f);
                arrived.Set();
                return;
            }
            var h = Notification;
            if (h != null) h(f);
        }

        byte[] Next(int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                lock (inbox) if (inbox.Count > 0) return inbox.Dequeue();
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0 || !arrived.WaitOne(left)) return null;
            }
        }

        bool Write(byte[] payload)
        {
            var frame = new byte[64];
            frame[0] = ReportId;
            frame[1] = (byte)(payload.Length + 1);
            Array.Copy(payload, 0, frame, 3, Math.Min(payload.Length, 61));
            if (Log.Enabled || Log.Echo != null) Log.Write("  => " + Log.Hex(frame, 16));
            return stream.Write(frame);
        }

        static byte[] Payload(byte[] frame)
        {
            int len = frame[1];
            if (len <= 1 || len + 2 > frame.Length) return null;
            var p = new byte[len - 1];
            Array.Copy(frame, 3, p, 0, p.Length);
            return p;
        }

        // A request to the dongle itself. Returns the parameters of the reply.
        byte[] Direct(byte feature, int function, params byte[] args)
        {
            var payload = new byte[2 + args.Length];
            payload[0] = feature;
            payload[1] = (byte)((function << 4) | SoftwareId);
            Array.Copy(args, 0, payload, 2, args.Length);
            return Exchange(payload, reply =>
            {
                if (reply.Length < 2 || reply[0] != feature) return null;
                var r = new byte[reply.Length - 2];
                Array.Copy(reply, 2, r, 0, r.Length);
                return r;
            });
        }

        // A request forwarded through the bridge to the headset. Returns the parameters of the reply,
        // or null when the headset is unreachable (off / out of range) or did not answer.
        byte[] Bridged(byte feature, int function, params byte[] args)
        {
            if (bridgeIndex < 0) return null;
            int size = 3 + args.Length;
            var payload = new byte[4 + size];
            payload[0] = (byte)bridgeIndex;
            payload[1] = 0x10 | SoftwareId;
            payload[2] = (byte)((size >> 8) & 0x0F);
            payload[3] = (byte)size;
            payload[4] = 0x00;
            payload[5] = feature;
            payload[6] = (byte)((function << 4) | SoftwareId);
            Array.Copy(args, 0, payload, 7, args.Length);
            return Exchange(payload, reply =>
            {
                // Event from the bridge (function 1, software id 0) carrying the headset's answer.
                if (reply.Length < 7 || reply[0] != bridgeIndex || (reply[1] & 0xF0) != 0x10 || (reply[1] & 0x0F) != 0) return null;
                if (reply[4] != 0x00) return null;
                if (reply[5] == 0xFF) return reply.Length > 6 && reply[6] == feature ? new byte[0] : null;   // rejected
                if (reply[5] != feature) return null;
                var r = new byte[reply.Length - 7];
                Array.Copy(reply, 7, r, 0, r.Length);
                return r;
            });
        }

        byte[] Exchange(byte[] payload, Func<byte[], byte[]> match)
        {
            lock (requestLock)
            {
                lock (inbox) inbox.Clear();
                waiting = true;
                try
                {
                    if (!Write(payload)) return null;
                    for (int i = 0; i < 12; i++)
                    {
                        byte[] f = Next(400);
                        if (f == null) return null;
                        byte[] p = Payload(f);
                        if (p == null)
                        {
                            var h = Notification;
                            if (h != null) h(f);
                            continue;
                        }
                        byte[] r = match(p);
                        if (r != null) return r;
                        if (p.Length >= 2 && (p[1] & 0x0F) == SoftwareId) continue;   // ack of our own request
                        var n = Notification;
                        if (n != null) n(f);
                    }
                    return null;
                }
                finally { waiting = false; }
            }
        }

        // Finds the bridge and the headset's battery feature. Cheap after the first success.
        bool Discover()
        {
            if (bridgeIndex >= 0 && batteryIndex >= 0) return true;
            byte[] fs = Direct(0, 0, FeatureSet >> 8, FeatureSet & 0xFF);
            if (fs == null || fs.Length < 1 || fs[0] == 0) return false;
            byte fsIndex = fs[0];
            byte[] count = Direct(fsIndex, 0);
            if (count == null || count.Length < 1) return false;
            for (int i = 0; i < count[0] && bridgeIndex < 0; i++)
            {
                byte[] f = Direct(fsIndex, 1, (byte)i);
                if (f != null && f.Length >= 3 && ((f[1] << 8) | f[2]) == Bridge) bridgeIndex = i;
            }
            if (bridgeIndex < 0) return false;

            byte[] sfs = Bridged(0, 0, FeatureSet >> 8, FeatureSet & 0xFF);
            if (sfs == null || sfs.Length < 1 || sfs[0] == 0) return false;   // headset off: try again later
            byte sfsIndex = sfs[0];
            byte[] sub = Bridged(sfsIndex, 0);
            if (sub == null || sub.Length < 1) return false;
            for (int i = 0; i < sub[0]; i++)
            {
                byte[] f = Bridged(sfsIndex, 1, (byte)i);
                if (f != null && f.Length >= 3 && ((f[1] << 8) | f[2]) == BatterySoc)
                {
                    batteryIndex = i;
                    break;
                }
            }
            return batteryIndex >= 0;
        }

        // Reads the battery. Returns null when the headset could not be reached (powered off, out of range).
        public BatteryReading ReadBattery()
        {
            if (closed || !Discover()) return null;
            byte[] r = Bridged((byte)batteryIndex, 0);
            if (r == null || r.Length < 3) return null;
            return Parse(r);
        }

        // Battery SoC reply/event parameters: [0]=percent, [1]=?, [2]=charge state.
        public static BatteryReading Parse(byte[] p)
        {
            int percent = Math.Min(100, (int)p[0]);
            ChargeState state;
            switch (p[2])
            {
                case 1:
                case 2: state = ChargeState.Charging; break;
                case 3: state = ChargeState.Full; break;
                default: state = ChargeState.Discharging; break;
            }
            return new BatteryReading { Percent = percent, State = state };
        }

        // Battery events the headset pushes on its own (through the bridge, software id 0).
        public BatteryReading ParseBatteryEvent(byte[] frame)
        {
            byte[] p = Payload(frame);
            if (p == null || bridgeIndex < 0 || batteryIndex < 0) return null;
            if (p.Length >= 10 && p[0] == bridgeIndex && (p[1] & 0xF0) == 0x10 && p[4] == 0 && p[5] == batteryIndex)
            {
                var r = new byte[p.Length - 7];
                Array.Copy(p, 7, r, 0, r.Length);
                return Parse(r);
            }
            return null;
        }

        public void Dispose()
        {
            closed = true;
            stream.Dispose();
        }
    }
}
