#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AuraFlyout.Modules.SystemStatus
{
    // Minimal client for the PawnIO driver (https://pawnio.eu): loads a signed module and calls
    // its ioctl functions. Opening the device requires administrator rights.
    class PawnIo : IDisposable
    {
        const uint IOCTL_LOAD_BINARY = (41394u << 16) | (0x821 << 2);
        const uint IOCTL_EXECUTE_FN = (41394u << 16) | (0x841 << 2);
        const int FunctionNameLength = 32;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
            byte[] output, int outputSize, out int returned, IntPtr overlapped);

        readonly SafeFileHandle handle;

        PawnIo(SafeFileHandle handle) { this.handle = handle; }

        public static bool Installed
        {
            get
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                    return k != null;
            }
        }

        public static PawnIo Open(byte[] module)
        {
            SafeFileHandle h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000 /* GENERIC_READ|WRITE */,
                3 /* FILE_SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80, IntPtr.Zero);
            if (h.IsInvalid) return null;
            int returned;
            if (!DeviceIoControl(h, IOCTL_LOAD_BINARY, module, module.Length, null, 0, out returned, IntPtr.Zero))
            {
                h.Dispose();
                return null;
            }
            return new PawnIo(h);
        }

        public bool Execute(string function, ulong[] input, ulong[] output)
        {
            var inBuf = new byte[FunctionNameLength + input.Length * 8];
            byte[] name = System.Text.Encoding.ASCII.GetBytes(function);
            Buffer.BlockCopy(name, 0, inBuf, 0, Math.Min(name.Length, FunctionNameLength - 1));
            Buffer.BlockCopy(input, 0, inBuf, FunctionNameLength, input.Length * 8);
            var outBuf = new byte[output.Length * 8];
            int returned;
            if (!DeviceIoControl(handle, IOCTL_EXECUTE_FN, inBuf, inBuf.Length, outBuf, outBuf.Length, out returned, IntPtr.Zero))
                return false;
            Buffer.BlockCopy(outBuf, 0, output, 0, Math.Min(returned, outBuf.Length));
            return returned >= outBuf.Length;
        }

        public void Dispose() { handle.Dispose(); }
    }

    // CPU package temperature through PawnIO: AMD Zen (Tctl from SMN) and Intel (package thermal MSR).
    class PawnIoCpuTemp : IDisposable
    {
        const uint F17H_M01H_THM_TCON_CUR_TMP = 0x00059800;
        const uint MSR_TEMPERATURE_TARGET = 0x1A2;
        const uint IA32_PACKAGE_THERM_STATUS = 0x1B1;

        readonly PawnIo io;
        readonly bool amd;
        readonly float offset;    // Tctl -> Tdie correction on a few early Ryzen models
        readonly int tjMax;
        readonly Mutex pciMutex;  // shared convention with other hardware monitors

        PawnIoCpuTemp(PawnIo io, bool amd, float offset, int tjMax, Mutex pciMutex)
        {
            this.io = io;
            this.amd = amd;
            this.offset = offset;
            this.tjMax = tjMax;
            this.pciMutex = pciMutex;
        }

        public static PawnIoCpuTemp TryCreate()
        {
            string vendor, name;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (k == null) return null;
                vendor = k.GetValue("VendorIdentifier") as string ?? "";
                name = k.GetValue("ProcessorNameString") as string ?? "";
            }

            bool amd = vendor == "AuthenticAMD";
            if (!amd && vendor != "GenuineIntel") return null;
            byte[] module = Resource(amd ? "AMDFamily17.bin" : "IntelMSR.bin");
            if (module == null) return null;
            PawnIo io = PawnIo.Open(module);   // fails without admin, or on CPUs the module does not support
            if (io == null) return null;

            if (amd)
            {
                float offset = 0;
                if (name.Contains("1600X") || name.Contains("1700X") || name.Contains("1800X")) offset = -20;
                else if (name.Contains("Threadripper 19") || name.Contains("Threadripper 29")) offset = -27;
                else if (name.Contains("2700X")) offset = -10;
                Mutex m = null;
                try { m = new Mutex(false, @"Global\Access_PCI"); }
                catch (Exception) { }
                return new PawnIoCpuTemp(io, true, offset, 0, m);
            }

            var outv = new ulong[1];
            int tj = io.Execute("ioctl_read_msr", new ulong[] { MSR_TEMPERATURE_TARGET }, outv) ? (int)((outv[0] >> 16) & 0xFF) : 0;
            return new PawnIoCpuTemp(io, false, 0, tj > 0 ? tj : 100, null);
        }

        public float? Read()
        {
            var outv = new ulong[1];
            if (!amd)
            {
                if (!io.Execute("ioctl_read_msr", new ulong[] { IA32_PACKAGE_THERM_STATUS }, outv)) return null;
                if ((outv[0] & 0x80000000) == 0) return null;   // reading not valid
                return tjMax - (int)((outv[0] >> 16) & 0x7F);
            }

            bool locked = false;
            try
            {
                if (pciMutex != null)
                {
                    try { locked = pciMutex.WaitOne(10); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked) return null;
                }
                if (!io.Execute("ioctl_read_smn", new ulong[] { F17H_M01H_THM_TCON_CUR_TMP }, outv)) return null;
            }
            finally
            {
                if (locked) pciMutex.ReleaseMutex();
            }

            uint raw = (uint)outv[0];
            float t = (raw >> 21) * 0.125f;
            if ((raw & 0x80000) != 0 || (raw & 0x30000) == 0x30000) t -= 49;
            return t + offset;
        }

        static byte[] Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var data = new byte[s.Length];
                int read = 0;
                while (read < data.Length) read += s.Read(data, read, data.Length - read);
                return data;
            }
        }

        public void Dispose()
        {
            io.Dispose();
            if (pciMutex != null) pciMutex.Dispose();
        }
    }
}
