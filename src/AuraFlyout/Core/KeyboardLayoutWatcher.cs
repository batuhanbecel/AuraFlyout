using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AuraFlyout.Core;

/// <summary>
/// Notices the user switching the keyboard language (Win+Space, Alt+Shift, the language button) and
/// names the new one: "Türkçe (Türkiye)" + "TR · Türkçe Q". Polls the layout of the window in front;
/// a change while the same window stays in front is a switch (moving to another app is not).
/// </summary>
public sealed class KeyboardLayoutWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private IntPtr _lastHkl, _lastWindow;

    /// <summary>Language name, short code + layout name.</summary>
    public event Action<string, string>? Switched;

    public KeyboardLayoutWatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        uint thread = Native.GetWindowThreadProcessId(fg, out _);
        IntPtr hkl = GetKeyboardLayout(thread);
        if (hkl == IntPtr.Zero) return;
        if (fg == _lastWindow && _lastHkl != IntPtr.Zero && hkl != _lastHkl)
        {
            var (title, subtitle) = Describe(hkl);
            Switched?.Invoke(title, subtitle);
        }
        _lastWindow = fg;
        _lastHkl = hkl;
    }

    public static (string Title, string Subtitle) Describe(IntPtr hkl)
    {
        long v = hkl.ToInt64();
        int lang = (int)(v & 0xFFFF), device = (int)((v >> 16) & 0xFFFF);
        string title, code;
        try
        {
            var culture = new CultureInfo(lang);
            title = char.ToUpper(culture.NativeName[0], culture) + culture.NativeName[1..];
            code = culture.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch
        {
            title = $"0x{lang:X4}";
            code = "";
        }
        string? layout = LayoutName(lang, device);
        string subtitle = string.Join(" · ", new[] { code, layout }.Where(s => !string.IsNullOrEmpty(s)));
        return (title, subtitle);
    }

    /// <summary>"Türkçe Q", "Türkçe F", "ABD": the layout's own name from the registry.</summary>
    private static string? LayoutName(int lang, int device)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            if (root == null) return null;
            string? klid = null;
            if (device == lang) klid = $"0000{lang:X4}";
            else if ((device & 0xF000) == 0xF000)
            {
                // a variant layout: find the KLID whose "Layout Id" matches
                string id = (device & 0x0FFF).ToString("X4");
                foreach (string name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("Layout Id") is string lid && lid.Equals(id, StringComparison.OrdinalIgnoreCase))
                    {
                        klid = name;
                        break;
                    }
                }
            }
            else klid = $"{device:X4}{lang:X4}";
            if (klid == null) return null;
            using var key = root.OpenSubKey(klid);
            // "Layout Display Name" is the localised one (@%SystemRoot%\system32\input.dll,-5052)
            if (key?.GetValue("Layout Display Name") is string display && display.StartsWith('@'))
            {
                var sb = new System.Text.StringBuilder(256);
                if (SHLoadIndirectString(display, sb, sb.Capacity, IntPtr.Zero) == 0) return sb.ToString();
            }
            return key?.GetValue("Layout Text") as string;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _timer.Stop();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, int size, IntPtr reserved);
}
