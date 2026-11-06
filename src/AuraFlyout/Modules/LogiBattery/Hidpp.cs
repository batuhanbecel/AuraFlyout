#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;
using System.Collections.Generic;
using System.Threading;

namespace AuraFlyout.Modules.LogiBattery
{
    // A reply to a HID++ request: Data holds the report (index 4 onwards are the parameters), or Error is set.
    class HidppReply
    {
        public byte[] Data;
        public int Error;     // HID++ 2.0 error code, or 0x100 + HID++ 1.0 error code
        public bool Ok { get { return Data != null; } }
        public byte this[int i] { get { return Data != null && i < Data.Length ? Data[i] : (byte)0; } }
    }

    // The HID++ endpoint of one USB interface (a receiver, or a device connected by cable / its own dongle).
    // Windows splits short (0x10, 7 bytes) and long (0x11, 20 bytes) reports into separate collections,
    // so up to two streams are read and merged here.
    class HidppEndpoint : IDisposable
    {
        public const byte ShortReport = 0x10, LongReport = 0x11;
        const byte SoftwareId = 0x0B;

        public readonly HidInfo Info;          // the long-report collection
        public event Action<byte[]> Notification;
        public event Action Closed;

        readonly List<HidStream> streams = new List<HidStream>();
        readonly HidStream longStream;
        HidStream shortStream;
        readonly object requestLock = new object();
        readonly object pendingLock = new object();
        byte[] pendingKey;                     // device index, feature index, function|software id
        HidppReply pendingReply;
        readonly AutoResetEvent replied = new AutoResetEvent(false);
        volatile bool closed;

        HidppEndpoint(HidInfo info, HidStream longStream)
        {
            Info = info;
            this.longStream = longStream;
        }

        // Opens the HID++ collections of one interface; null when it has none usable.
        public static HidppEndpoint Open(List<HidInfo> sameInterface)
        {
            HidInfo longInfo = sameInterface.Find(h => h.UsagePage == 0xFF00 && h.OutputLength == 20);
            if (longInfo == null) longInfo = sameInterface.Find(h => h.UsagePage >= 0xFF00 && h.OutputLength == 20 && h.InputLength == 20);
            if (longInfo == null) return null;
            HidStream ls = HidStream.Open(longInfo);
            if (ls == null) return null;

            var ep = new HidppEndpoint(longInfo, ls);
            ep.Attach(ls);
            HidInfo shortInfo = sameInterface.Find(h => h.UsagePage == longInfo.UsagePage && h.InputLength == 7);
            if (shortInfo != null)
            {
                HidStream ss = HidStream.Open(shortInfo);
                if (ss != null)
                {
                    ep.Attach(ss);
                    ep.shortStream = ss;
                }
            }
            foreach (HidStream s in ep.streams) s.Start();
            return ep;
        }

        void Attach(HidStream s)
        {
            s.Received += OnReport;
            s.Closed += _ =>
            {
                if (closed) return;
                closed = true;
                var handler = Closed;
                if (handler != null) handler();
            };
            streams.Add(s);
        }

        public bool IsClosed { get { return closed; } }

        void OnReport(HidStream s, byte[] r)
        {
            if (r.Length < 4 || (r[0] != ShortReport && r[0] != LongReport && r[0] != 0x12)) return;
            if (Log.Enabled || Log.Echo != null) Log.Write("  <- " + Log.Hex(r, 20));
            lock (pendingLock)
            {
                byte[] k = pendingKey;
                if (k != null && r[1] == k[0])
                {
                    HidppReply reply = null;
                    if (r[2] == k[1] && r[3] == k[2]) reply = new HidppReply { Data = r };
                    else if (r[2] == 0xFF && r[3] == k[1] && r[4] == k[2] && r.Length > 5) reply = new HidppReply { Error = r[5] };
                    else if (r[2] == 0x8F && r[3] == k[1] && r[4] == k[2] && r.Length > 5) reply = new HidppReply { Error = 0x100 + r[5] };
                    if (reply != null)
                    {
                        pendingReply = reply;
                        pendingKey = null;
                        replied.Set();
                        return;
                    }
                }
            }
            var handler = Notification;
            if (handler != null) handler(r);
        }

        // HID++ 2.0 request: feature index + function (0..15). Returns null on timeout.
        public HidppReply Request(byte device, byte feature, int function, int timeoutMs, params byte[] args)
        {
            return Send(LongReport, device, feature, (byte)((function << 4) | SoftwareId), timeoutMs, args);
        }

        // HID++ 1.0 register access (receivers): subId 0x81 read short register, 0x83 read long register, 0x80 write.
        public HidppReply Register(byte device, byte subId, byte address, int timeoutMs, params byte[] args)
        {
            return Send(ShortReport, device, subId, address, timeoutMs, args);
        }

        HidppReply Send(byte reportId, byte device, byte b2, byte b3, int timeoutMs, byte[] args)
        {
            if (closed) return null;
            // Each collection accepts only its own report id. HID++ 2.0 requests always go out long;
            // receiver register requests must be short.
            HidStream stream = longStream;
            if (reportId == ShortReport)
            {
                if (shortStream == null) return null;
                stream = shortStream;
            }
            var report = new byte[reportId == ShortReport ? 7 : 20];
            report[0] = reportId;
            report[1] = device;
            report[2] = b2;
            report[3] = b3;
            if (args != null) Array.Copy(args, 0, report, 4, Math.Min(args.Length, report.Length - 4));

            lock (requestLock)
            {
                lock (pendingLock)
                {
                    pendingKey = new[] { device, b2, b3 };
                    pendingReply = null;
                    replied.Reset();
                }
                if (Log.Enabled || Log.Echo != null) Log.Write("  -> " + Log.Hex(report));
                if (!stream.Write(report))
                {
                    lock (pendingLock) pendingKey = null;
                    return null;
                }
                bool got = replied.WaitOne(timeoutMs);
                lock (pendingLock)
                {
                    pendingKey = null;
                    return got ? pendingReply : null;
                }
            }
        }

        public void Dispose()
        {
            closed = true;
            foreach (HidStream s in streams) s.Dispose();
            streams.Clear();
        }
    }

    // HID++ 2.0 feature ids used here.
    static class Feature
    {
        public const ushort Root = 0x0000, FeatureSet = 0x0001, DeviceName = 0x0005;
        public const ushort BatteryStatus = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004, AdcMeasurement = 0x1F20;
        public const ushort WirelessStatus = 0x1D4B;
    }
}
