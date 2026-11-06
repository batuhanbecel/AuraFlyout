#nullable disable
// Ported from the author's standalone app; kept close to the original on purpose.
using System;

namespace AuraFlyout.Modules.LogiBattery
{
    enum ChargeState { Discharging, Charging, Full, Error }

    enum DeviceKind { Other, Keyboard, Mouse, Headset, Gamepad, Trackball, Numpad, Presenter, Webcam, Speaker, Wheel }

    class BatteryReading
    {
        public int Percent;          // 0-100
        public bool Approximate;     // only a coarse level is known (full / good / low / critical)
        public ChargeState State;
        public int Millivolts;       // voltage-type features only

        public bool OnCharger { get { return State == ChargeState.Charging || State == ChargeState.Full; } }

        public BatteryReading Clone() { return (BatteryReading)MemberwiseClone(); }

        public bool SameAs(BatteryReading other)
        {
            return other != null && other.Percent == Percent && other.State == State && other.Approximate == Approximate;
        }

        // Li-ion discharge curve (mV -> %) for devices that only report a voltage (HID++ 0x1001 / 0x1F20).
        static readonly int[,] Curve =
        {
            { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
            { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 }
        };

        public static int PercentFromVoltage(int mv)
        {
            if (mv >= Curve[0, 0]) return 100;
            for (int i = 1; i < Curve.GetLength(0); i++)
            {
                if (mv < Curve[i, 0]) continue;
                double t = (double)(mv - Curve[i, 0]) / (Curve[i - 1, 0] - Curve[i, 0]);
                return (int)Math.Round(Curve[i, 1] + t * (Curve[i - 1, 1] - Curve[i, 1]));
            }
            return 0;
        }
    }

    // A device as the UI sees it (a snapshot; the DeviceManager owns the live copy).
    class LogiDevice
    {
        public string Id;              // stable: "w<wireless pid>", "c<usb pid>", "p<usb pid>"
        public string Name;
        public DeviceKind Kind;
        public bool Connected;
        public string Connection;      // "LIGHTSPEED", "USB", ...
        public BatteryReading Battery; // last known (kept while disconnected)
        public DateTime LastSeen;      // UTC

        public LogiDevice Clone()
        {
            var d = (LogiDevice)MemberwiseClone();
            if (Battery != null) d.Battery = Battery.Clone();
            return d;
        }
    }
}
