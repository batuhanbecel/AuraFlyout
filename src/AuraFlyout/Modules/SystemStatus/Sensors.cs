#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AuraFlyout.Modules.SystemStatus
{
    // GPU temperature, fan and power straight from the display driver (the same source Task Manager
    // uses). Vendor neutral, no admin needed; requires a WDDM 2.4+ driver that reports it.
    class GpuSensor : IDisposable
    {
        const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
        const int KMTQAITYPE_ADAPTERPERFDATA = 62;
        const int AdapterInfoSize = 20;     // D3DKMT_ADAPTERINFO
        const int PerfDataSize = 64;        // D3DKMT_ADAPTER_PERFDATA
        const int RegistryInfoSize = 2080;  // D3DKMT_ADAPTERREGISTRYINFO (4 x WCHAR[260])

        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(IntPtr enumAdapters);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(IntPtr query);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(IntPtr close);

        int[] adapters = new int[0];
        int failures;
        int namedAdapter;

        public float? Temperature { get; private set; }
        public int FanRpm { get; private set; }
        public float PowerPercent { get; private set; }
        public string Name { get; private set; }

        public void Update()
        {
            if (adapters.Length == 0 || failures >= 30)
            {
                Enumerate();
                failures = 0;
            }

            float best = -1;
            int bestAdapter = 0;
            IntPtr perf = Marshal.AllocHGlobal(PerfDataSize);
            try
            {
                foreach (int adapter in adapters)
                {
                    Zero(perf, PerfDataSize);
                    if (Query(adapter, KMTQAITYPE_ADAPTERPERFDATA, perf, PerfDataSize) != 0) continue;
                    int deciCelsius = Marshal.ReadInt32(perf, 56);
                    if (deciCelsius <= 0 || deciCelsius / 10f <= best) continue;
                    best = deciCelsius / 10f;
                    bestAdapter = adapter;
                    FanRpm = Marshal.ReadInt32(perf, 48);
                    PowerPercent = Marshal.ReadInt32(perf, 52) / 10f;
                }
            }
            finally { Marshal.FreeHGlobal(perf); }

            if (best < 0)
            {
                Temperature = null;
                failures++;
                return;
            }
            Temperature = best;
            if (bestAdapter != namedAdapter)
            {
                Name = QueryName(bestAdapter);
                namedAdapter = bestAdapter;
            }
        }

        void Enumerate()
        {
            Close();
            IntPtr e = Marshal.AllocHGlobal(16);   // D3DKMT_ENUMADAPTERS2 { ULONG NumAdapters; D3DKMT_ADAPTERINFO* pAdapters; }
            IntPtr list = IntPtr.Zero;
            try
            {
                Zero(e, 16);
                if (D3DKMTEnumAdapters2(e) != 0) return;
                int count = Marshal.ReadInt32(e, 0);
                if (count <= 0) return;
                list = Marshal.AllocHGlobal(count * AdapterInfoSize);
                Marshal.WriteIntPtr(e, 8, list);
                if (D3DKMTEnumAdapters2(e) != 0) return;
                count = Marshal.ReadInt32(e, 0);
                adapters = new int[count];
                for (int i = 0; i < count; i++) adapters[i] = Marshal.ReadInt32(list, i * AdapterInfoSize);
            }
            finally
            {
                Marshal.FreeHGlobal(e);
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            }
        }

        static string QueryName(int adapter)
        {
            IntPtr info = Marshal.AllocHGlobal(RegistryInfoSize);
            try
            {
                Zero(info, RegistryInfoSize);
                if (Query(adapter, KMTQAITYPE_ADAPTERREGISTRYINFO, info, RegistryInfoSize) != 0) return null;
                string name = Marshal.PtrToStringUni(info);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        static int Query(int adapter, int type, IntPtr data, int size)
        {
            IntPtr q = Marshal.AllocHGlobal(24);   // D3DKMT_QUERYADAPTERINFO
            try
            {
                Marshal.WriteInt32(q, 0, adapter);
                Marshal.WriteInt32(q, 4, type);
                Marshal.WriteIntPtr(q, 8, data);
                Marshal.WriteInt32(q, 16, size);
                return D3DKMTQueryAdapterInfo(q);
            }
            finally { Marshal.FreeHGlobal(q); }
        }

        static void Zero(IntPtr p, int size)
        {
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
        }

        void Close()
        {
            IntPtr c = Marshal.AllocHGlobal(4);
            foreach (int adapter in adapters)
            {
                Marshal.WriteInt32(c, 0, adapter);
                D3DKMTCloseAdapter(c);
            }
            Marshal.FreeHGlobal(c);
            adapters = new int[0];
        }

        public void Dispose() { Close(); }
    }

    // Windows has no driver-free way to read a desktop CPU's temperature. The PawnIO driver is the
    // primary source; otherwise the value is borrowed from tools that already have a driver loaded:
    // HWiNFO (shared memory) or LibreHardwareMonitor / OpenHardwareMonitor (WMI). The ACPI thermal
    // zone is a last resort (mostly laptops).
    class CpuTempMonitor
    {
        static readonly string[] HwinfoLabels = { "CPU (Tctl/Tdie)", "CPU Package", "CPU (Tdie)", "CPU (Tctl)", "CPU Die (average)" };
        static readonly string[] LhmNames = { "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Max", "Core Average" };
        static readonly string[] WmiNamespaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };

        DateTime nextWmiTry, nextZoneTry, nextPawnIoTry;
        PerformanceCounterCategory zones;
        PawnIoCpuTemp pawnIo;

        public string Source { get; private set; }

        public float? Read()
        {
            if (pawnIo == null && DateTime.UtcNow >= nextPawnIoTry)
            {
                pawnIo = PawnIoCpuTemp.TryCreate();
                if (pawnIo == null) nextPawnIoTry = DateTime.UtcNow.AddSeconds(60);
            }
            float? t = pawnIo != null ? pawnIo.Read() : null;
            if (t.HasValue) { Source = "PawnIO"; return t; }

            t = ReadHwinfo();
            if (t.HasValue) { Source = "HWiNFO"; return t; }
            t = ReadWmi();
            if (t.HasValue) return t;
            t = ReadThermalZone();
            if (t.HasValue) { Source = "ACPI"; return t; }
            Source = null;
            return null;
        }

        // --- HWiNFO "Shared Memory Support" ---

        const uint FILE_MAP_READ = 4;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static float? ReadHwinfo()
        {
            IntPtr map = OpenFileMapping(FILE_MAP_READ, false, @"Global\HWiNFO_SENS_SM2");
            if (map == IntPtr.Zero) return null;
            IntPtr view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            try
            {
                if (view == IntPtr.Zero) return null;
                // Packed header: signature, version, revision, poll_time(int64), sensor section (3 dwords), reading section (3 dwords).
                long pollTime = Marshal.ReadInt64(view, 12);
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                if (Math.Abs(now - pollTime) > 10) return null;   // HWiNFO is gone; the mapping is stale

                int offset = Marshal.ReadInt32(view, 32);
                int size = Marshal.ReadInt32(view, 36);
                int count = Marshal.ReadInt32(view, 40);
                int bestRank = int.MaxValue;
                float? best = null;
                for (int i = 0; i < count; i++)
                {
                    IntPtr r = view + offset + i * size;
                    if (Marshal.ReadInt32(r, 0) != 1) continue;   // SENSOR_TYPE_TEMP
                    int rank = Array.IndexOf(HwinfoLabels, Marshal.PtrToStringAnsi(r + 12));
                    if (rank < 0 || rank >= bestRank) continue;
                    bestRank = rank;
                    best = (float)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(r, 284));
                }
                return best;
            }
            finally
            {
                if (view != IntPtr.Zero) UnmapViewOfFile(view);
                CloseHandle(map);
            }
        }

        // --- LibreHardwareMonitor / OpenHardwareMonitor WMI ---

        float? ReadWmi()
        {
            if (DateTime.UtcNow < nextWmiTry) return null;
            foreach (string ns in WmiNamespaces)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(ns,
                        "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType='Temperature'"))
                    {
                        int bestRank = int.MaxValue;
                        float? best = null;
                        foreach (ManagementBaseObject o in searcher.Get())
                        {
                            string id = (o["Identifier"] as string) ?? "";
                            if (id.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            int rank = Array.IndexOf(LhmNames, o["Name"] as string);
                            if (rank < 0) rank = LhmNames.Length;
                            if (rank >= bestRank) continue;
                            bestRank = rank;
                            best = Convert.ToSingle(o["Value"]);
                        }
                        if (best.HasValue)
                        {
                            Source = ns.Substring(5);
                            return best;
                        }
                    }
                }
                catch (ManagementException) { }
                catch (COMException) { }
            }
            nextWmiTry = DateTime.UtcNow.AddSeconds(15);
            return null;
        }

        // --- ACPI thermal zone (Kelvin) ---

        float? ReadThermalZone()
        {
            if (DateTime.UtcNow < nextZoneTry) return null;
            try
            {
                if (zones == null) zones = new PerformanceCounterCategory("Thermal Zone Information");
                InstanceDataCollectionCollection all = zones.ReadCategory();
                InstanceDataCollection hp = all["High Precision Temperature"];
                InstanceDataCollection k = hp ?? all["Temperature"];
                float scale = hp != null ? 10f : 1f;
                float max = float.MinValue;
                if (k != null)
                    foreach (InstanceData d in k.Values)
                        max = Math.Max(max, d.RawValue / scale - 273.15f);
                if (max > 0 && max < 125) return max;
            }
            catch { }
            zones = null;
            nextZoneTry = DateTime.UtcNow.AddSeconds(60);
            return null;
        }
    }

    // Download/upload speed over the interfaces that carry traffic (those with a default gateway).
    class NetMonitor
    {
        NetworkInterface[] nics;
        DateTime nextRefresh;
        long lastRx, lastTx;
        bool primed;
        readonly Stopwatch clock = new Stopwatch();

        public double Down { get; private set; }   // bytes/s
        public double Up { get; private set; }

        public void Update()
        {
            if (nics == null || DateTime.UtcNow > nextRefresh)
            {
                nics = Pick();
                nextRefresh = DateTime.UtcNow.AddSeconds(30);
                primed = false;
            }

            long rx = 0, tx = 0;
            foreach (NetworkInterface n in nics)
            {
                try
                {
                    IPInterfaceStatistics s = n.GetIPStatistics();
                    rx += s.BytesReceived;
                    tx += s.BytesSent;
                }
                catch (NetworkInformationException) { }
            }

            double seconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            if (primed && seconds > 0)
            {
                Down = Math.Max(0, (rx - lastRx) / seconds);
                Up = Math.Max(0, (tx - lastTx) / seconds);
            }
            lastRx = rx;
            lastTx = tx;
            primed = true;
        }

        static NetworkInterface[] Pick()
        {
            var withGateway = new List<NetworkInterface>();
            var up = new List<NetworkInterface>();
            try
            {
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    up.Add(n);
                    if (n.GetIPProperties().GatewayAddresses.Count > 0) withGateway.Add(n);
                }
            }
            catch (NetworkInformationException) { }
            return (withGateway.Count > 0 ? withGateway : up).ToArray();
        }

        public static string Format(double bytesPerSecond)
        {
            double kb = bytesPerSecond / 1024;
            if (kb < 1000) return kb.ToString("0") + " KB/s";
            double mb = kb / 1024;
            return mb.ToString(mb < 10 ? "0.0" : "0") + " MB/s";
        }
    }
}
