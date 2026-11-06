using System.Runtime.InteropServices;

namespace AuraFlyout.Core;

/// <summary>
/// Windows' power mode (Settings › System › Power &amp; battery › Power mode): best power efficiency,
/// balanced or best performance. Windows 11 24H2 and later keep one mode for "plugged in" and one
/// for "on battery" and switch them through the user power mode calls the Settings app uses; older
/// builds only have the active power overlay.
/// </summary>
public static class PowerMode
{
    private static readonly Guid[] Overlays =
    [
        new("961CC777-2547-4F9D-8174-7D86181B8A7A"), // best power efficiency
        Guid.Empty,                                   // balanced
        new("DED574B5-45A0-4F42-8737-46345C09C238"), // best performance
    ];

    private static bool? _userModes;

    /// <summary>The user power mode calls exist (Windows 11 24H2 and later).</summary>
    private static bool HasUserModes
    {
        get
        {
            if (_userModes is { } known) return known;
            IntPtr lib = NativeLibrary.Load("powrprof.dll");
            _userModes = NativeLibrary.TryGetExport(lib, "PowerSetUserConfiguredACPowerMode", out _) &&
                         NativeLibrary.TryGetExport(lib, "PowerSetUserConfiguredDCPowerMode", out _);
            return _userModes.Value;
        }
    }

    private static bool OnBattery => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    /// <summary>0 efficiency, 1 balanced, 2 performance; null when Windows has no power modes here.</summary>
    public static int? Get()
    {
        try
        {
            Guid g;
            uint r = HasUserModes
                ? OnBattery ? PowerGetUserConfiguredDCPowerMode(out g) : PowerGetUserConfiguredACPowerMode(out g)
                : PowerGetEffectiveOverlayScheme(out g);
            if (r != 0) return null;
            int i = Array.IndexOf(Overlays, g);
            return i >= 0 ? i : 1;
        }
        catch
        {
            return null;
        }
    }

    public static bool Set(int mode)
    {
        var g = Overlays[Math.Clamp(mode, 0, 2)];
        try
        {
            uint r = HasUserModes
                ? OnBattery ? PowerSetUserConfiguredDCPowerMode(ref g) : PowerSetUserConfiguredACPowerMode(ref g)
                : PowerSetActiveOverlayScheme(g);
            if (r != 0) Log.Warn($"Power mode {mode} could not be set (0x{r:X})");
            return r == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Power mode could not be set", ex);
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
}
