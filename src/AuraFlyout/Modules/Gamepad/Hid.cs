using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AuraFlyout.Modules.Gamepad;

/// <summary>
/// Just enough raw HID to read one input report: device paths, attributes, the top-level usage,
/// a feature report and an input report with a timeout. Devices are opened shared, so games and
/// other tools keep using them.
/// </summary>
internal sealed class Hid : IDisposable
{
    private readonly SafeFileHandle _handle;

    public ushort VendorId { get; private init; }
    public ushort ProductId { get; private init; }
    public ushort UsagePage { get; private init; }
    public ushort Usage { get; private init; }
    public int InputLength { get; private init; }
    public int FeatureLength { get; private init; }

    private Hid(SafeFileHandle handle) => _handle = handle;

    /// <summary>The interface paths of every present HID device.</summary>
    public static List<string> DevicePaths()
    {
        HidD_GetHidGuid(out Guid guid);
        if (CM_Get_Device_Interface_List_SizeW(out int length, ref guid, null, 0) != 0 || length <= 1) return [];
        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, 0) != 0) return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The device container the interface belongs to (shared with its Bluetooth node), or "".</summary>
    public static string ContainerId(string path)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), pid = 256 }; // DEVPKEY_Device_InstanceId
        int size = 0;
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0) != 0x1A /*CR_BUFFER_SMALL*/ || size <= 0) return "";
        var buffer = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, buffer, ref size, 0) != 0) return "";
        string instance = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out int node, instance, 0) != 0) return "";
        key = new DEVPROPKEY { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 }; // DEVPKEY_Device_ContainerId
        var guid = new byte[16];
        size = 16;
        return CM_Get_DevNode_PropertyW(node, ref key, out _, guid, ref size, 0) == 0 ? new Guid(guid).ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out int type, [Out] byte[]? buffer, ref int size, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int node, string instanceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(int node, ref DEVPROPKEY key, out int type, [Out] byte[]? buffer, ref int size, int flags);

    public static Hid? Open(string path)
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint SHARE = 0x1 | 0x2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        var handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateFileW(path, GENERIC_READ, SHARE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out IntPtr data))
        {
            handle.Dispose();
            return null;
        }
        try
        {
            if (HidP_GetCaps(data, out HIDP_CAPS caps) != 0x00110000 /*HIDP_STATUS_SUCCESS*/)
            {
                handle.Dispose();
                return null;
            }
            return new Hid(handle)
            {
                VendorId = attributes.VendorID,
                ProductId = attributes.ProductID,
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                InputLength = caps.InputReportByteLength,
                FeatureLength = caps.FeatureReportByteLength,
            };
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    /// <summary>Reads a feature report (the answer is not needed, only the request).</summary>
    public bool GetFeature(byte reportId)
    {
        if (FeatureLength <= 0) return false;
        var buffer = new byte[FeatureLength];
        buffer[0] = reportId;
        return HidD_GetFeature(_handle, buffer, buffer.Length);
    }

    /// <summary>The next input report (report id first), or null after <paramref name="timeoutMs"/>.</summary>
    public byte[]? ReadInput(int timeoutMs)
    {
        if (InputLength <= 0) return null;
        var buffer = new byte[InputLength];
        using var done = new ManualResetEvent(false);
        IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            if (!ReadFile(_handle, pin.AddrOfPinnedObject(), buffer.Length, IntPtr.Zero, overlapped) &&
                Marshal.GetLastWin32Error() != 997 /*ERROR_IO_PENDING*/) return null;
            if (!done.WaitOne(timeoutMs))
            {
                CancelIoEx(_handle, overlapped);
                GetOverlappedResult(_handle, overlapped, out _, true); // the cancel has landed: the buffer is free again
                return null;
            }
            return GetOverlappedResult(_handle, overlapped, out int read, false) && read > 0 ? buffer : null;
        }
        finally
        {
            pin.Free();
            Marshal.FreeHGlobal(overlapped);
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid guid, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string? deviceId, [Out] char[] buffer, int length, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int length, IntPtr read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
}
