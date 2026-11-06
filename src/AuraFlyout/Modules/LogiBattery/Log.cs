#nullable disable
using System;

namespace AuraFlyout.Modules.LogiBattery
{
    // Diagnostic log used by the HID / HID++ / Centurion engine. Same API as the standalone app;
    // lines go to AuraFlyout's log while the "diagnostic log" setting is on.
    static class Log
    {
        public static bool Enabled;
        public static Action<string> Echo;

        public static void Write(string message)
        {
            var echo = Echo;
            if (echo != null) echo(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
            if (Enabled) AuraFlyout.Core.Log.Info("[logi] " + message);
        }

        public static string Hex(byte[] data, int count = int.MaxValue)
        {
            int n = Math.Min(count, data.Length);
            var chars = new char[n * 3];
            for (int i = 0; i < n; i++)
            {
                string h = data[i].ToString("X2");
                chars[i * 3] = h[0];
                chars[i * 3 + 1] = h[1];
                chars[i * 3 + 2] = ' ';
            }
            return new string(chars).TrimEnd();
        }
    }
}
