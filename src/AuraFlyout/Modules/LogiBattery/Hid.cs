#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AuraFlyout.Modules.LogiBattery
{
    // One HID top-level collection as Windows exposes it (\\?\hid#vid_046d&pid_c547&mi_02&col02#...).
    class HidInfo
    {
        public string Path;
        public ushort Vid, Pid;
        public ushort UsagePage, Usage;
        public int InputLength, OutputLength;
        public string Product;

        // Collections of the same USB interface share this key (the path without &colNN and the collection suffix).
        public string InterfaceKey;

        public override string ToString()
        {
            return string.Format("{0:X4}:{1:X4} page={2:X4} usage={3:X4} in={4} out={5} {6}",
                Vid, Pid, UsagePage, Usage, InputLength, OutputLength, Product);
        }
    }

    static class Hid
    {
        // Lists the HID collections of one vendor. Collections that cannot even be queried are left out.
        public static List<HidInfo> Enumerate(ushort vendorId)
        {
            var list = new List<HidInfo>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE_VALUE) return list;
            try
            {
                var iface = new SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(iface);
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
                {
                    string path = InterfacePath(set, ref iface);
                    if (path == null || path.IndexOf("vid_" + vendorId.ToString("x4"), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    HidInfo info = Query(path);
                    if (info != null && info.Vid == vendorId) list.Add(info);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static string InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
        {
            uint size = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, ref size, IntPtr.Zero);
            if (size == 0) return null;
            IntPtr detail = Marshal.AllocHGlobal((int)size);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, ref size, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        // Opens the collection without read/write access, which works even for keyboards and mice.
        static HidInfo Query(string path)
        {
            using (SafeFileHandle h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var info = new HidInfo { Path = path, InterfaceKey = InterfaceKeyOf(path) };

                var attr = new HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(attr);
                if (!HidD_GetAttributes(h, ref attr)) return null;
                info.Vid = attr.VendorID;
                info.Pid = attr.ProductID;

                IntPtr preparsed;
                if (!HidD_GetPreparsedData(h, out preparsed)) return null;
                try
                {
                    var caps = new HIDP_CAPS();
                    if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS) return null;
                    info.UsagePage = caps.UsagePage;
                    info.Usage = caps.Usage;
                    info.InputLength = caps.InputReportByteLength;
                    info.OutputLength = caps.OutputReportByteLength;
                }
                finally { HidD_FreePreparsedData(preparsed); }

                var sb = new StringBuilder(128);
                if (HidD_GetProductString(h, sb, sb.Capacity * 2)) info.Product = sb.ToString().Trim();
                return info;
            }
        }

        static string InterfaceKeyOf(string path)
        {
            string p = path.ToLowerInvariant();
            int col = p.IndexOf("&col");
            if (col >= 0)
            {
                int end = p.IndexOf('#', col);
                if (end > col) p = p.Remove(col, end - col);
            }
            // \\?\hid#vid_..&pid_..&mi_02#8&7c0bba&0&0001#{guid}: the last &NNNN is the collection number.
            string[] parts = p.Split('#');
            if (parts.Length >= 3)
            {
                int amp = parts[2].LastIndexOf('&');
                if (amp > 0) parts[2] = parts[2].Substring(0, amp);
                return parts[1] + "#" + parts[2];
            }
            return p;
        }

        // --- native ---

        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int HIDP_STATUS_SUCCESS = 0x110000;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder sb, int bytes);
        [DllImport("hid.dll")] internal static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] report, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, ref uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }

    // An open HID collection. A background thread reads input reports and hands them to Received;
    // writes are synchronous with a timeout. Opening fails for collections Windows keeps exclusive
    // (keyboard, mouse), which never carry HID++ anyway.
    class HidStream : IDisposable
    {
        public readonly HidInfo Info;
        public event Action<HidStream, byte[]> Received;
        public event Action<HidStream> Closed;   // the device went away (read failed)

        readonly SafeFileHandle handle;
        readonly Thread reader;
        readonly object writeLock = new object();
        volatile bool disposed;

        HidStream(HidInfo info, SafeFileHandle handle)
        {
            Info = info;
            this.handle = handle;
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "HID reader" };
        }

        public static HidStream Open(HidInfo info)
        {
            SafeFileHandle h = Hid.CreateFile(info.Path, Hid.GENERIC_READ | Hid.GENERIC_WRITE,
                Hid.FILE_SHARE_READ | Hid.FILE_SHARE_WRITE, IntPtr.Zero, Hid.OPEN_EXISTING, Hid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) return null;
            return new HidStream(info, h);
        }

        public void Start() { reader.Start(); }

        // The report is padded to the collection's output length. Falls back to HidD_SetOutputReport for
        // endpoints that refuse WriteFile.
        public bool Write(byte[] report, int timeoutMs = 1000)
        {
            if (disposed) return false;
            var buf = new byte[Math.Max(Info.OutputLength, report.Length)];
            Array.Copy(report, buf, report.Length);
            lock (writeLock)
            {
                if (Overlapped(false, buf, timeoutMs) == buf.Length) return true;
                return !disposed && Hid.HidD_SetOutputReport(handle, buf, buf.Length);
            }
        }

        void ReadLoop()
        {
            var buf = new byte[Math.Max(Info.InputLength, 1)];
            int failures = 0;
            while (!disposed)
            {
                int n = Overlapped(true, buf, 500);
                if (n > 0)
                {
                    failures = 0;
                    var report = new byte[n];
                    Array.Copy(buf, report, n);
                    var handler = Received;
                    if (handler != null)
                    {
                        try { handler(this, report); }
                        catch (Exception ex) { Log.Write("HID handler: " + ex); }
                    }
                }
                else if (n < 0 && !disposed && ++failures >= 3)
                {
                    var closed = Closed;
                    if (closed != null) closed(this);
                    return;
                }
            }
        }

        // Returns bytes transferred, 0 on timeout, -1 on error.
        int Overlapped(bool read, byte[] buf, int timeoutMs)
        {
            using (var done = new ManualResetEvent(false))
            {
                IntPtr ov = Marshal.AllocHGlobal(32);
                IntPtr data = Marshal.AllocHGlobal(buf.Length);
                try
                {
                    for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(ov, i, 0);
                    Marshal.WriteIntPtr(ov, IntPtr.Size == 8 ? 24 : 16, done.SafeWaitHandle.DangerousGetHandle());
                    if (!read) Marshal.Copy(buf, 0, data, buf.Length);

                    uint n;
                    bool ok = read ? ReadFile(handle, data, (uint)buf.Length, IntPtr.Zero, ov)
                                   : WriteFile(handle, data, (uint)buf.Length, IntPtr.Zero, ov);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING) return -1;
                        if (!done.WaitOne(timeoutMs))
                        {
                            CancelIoEx(handle, ov);
                            GetOverlappedResult(handle, ov, out n, true);   // wait for the cancel before freeing ov
                            return 0;
                        }
                    }
                    if (!GetOverlappedResult(handle, ov, out n, false)) return -1;
                    if (read) Marshal.Copy(data, buf, 0, (int)n);
                    return (int)n;
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                    Marshal.FreeHGlobal(ov);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelIoEx(handle, IntPtr.Zero);
            if (reader.IsAlive && Thread.CurrentThread != reader) reader.Join(1000);
            handle.Dispose();
        }

        const int ERROR_IO_PENDING = 997;
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buf, uint len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint n, bool wait);
        [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    }
}
