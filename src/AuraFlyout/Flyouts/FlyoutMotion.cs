using System.Diagnostics;
using System.Runtime.InteropServices;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>
/// Moves sliding flyouts in step with the screen, like the shell's own animations: one move per
/// composed frame, at the display's refresh rate (144 moves a second on a 144 Hz screen), each to
/// where the curve is at that frame's time. A thread of its own waits for every DWM composition
/// (DwmFlush), so the rhythm does not depend on WPF's render loop (60 frames a second at most, and
/// uneven) or on the UI thread being free. The moves are posted to the window (SWP_ASYNCWINDOWPOS):
/// DWM shows a moved window as it is, nothing has to be drawn again.
/// </summary>
internal static class FlyoutMotion
{
    /// <summary>One slide of one window, between "behind its edge" (0) and "in place" (1).</summary>
    internal sealed class Slide
    {
        public required IntPtr Hwnd { get; init; }
        public required bool In { get; init; }
        public required double From { get; init; }
        public required double DurationMs { get; init; }
        public required Action<Slide> Done { get; init; }

        // where it goes; updated from the UI thread when the target moves (a DPI change, say)
        public volatile int X;
        public volatile int Rest;   // resting bottom (top for a top taskbar)
        public volatile int Hidden; // hidden top (bottom for a top taskbar)
        public volatile bool FromTop;

        public double Shown;        // last position sent, 0..1
        public volatile bool Cancelled;
        internal double StartMs = -1;

        /// <summary>The window's top for <paramref name="shown"/> with its height now.</summary>
        public int TopFor(double shown, int height)
        {
            int restTop = FromTop ? Rest : Rest - height;
            int hiddenTop = FromTop ? Hidden - height : Hidden;
            return (int)Math.Round(hiddenTop + (restTop - hiddenTop) * shown);
        }
    }

    private static readonly object Gate = new();
    private static readonly List<Slide> Active = [];
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Thread? _thread;

    public static void Start(Slide slide)
    {
        lock (Gate)
        {
            foreach (var s in Active.Where(s => s.Hwnd == slide.Hwnd)) s.Cancelled = true;
            Active.RemoveAll(s => s.Hwnd == slide.Hwnd);
            Active.Add(slide);
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Flyout motion", Priority = ThreadPriority.Highest };
                _thread.Start();
            }
        }
        Wake.Set();
    }

    public static void Cancel(IntPtr hwnd)
    {
        lock (Gate)
        {
            foreach (var s in Active.Where(s => s.Hwnd == hwnd)) s.Cancelled = true;
            Active.RemoveAll(s => s.Hwnd == hwnd);
        }
    }

    private static void Run()
    {
        double lastFrame = 0;
        while (true)
        {
            Slide[] slides;
            lock (Gate) slides = Active.ToArray();
            if (slides.Length == 0)
            {
                Wake.WaitOne();
                lastFrame = 0;
                continue;
            }

            // the next composition: what is sent now is on screen with the frame after it
            if (DwmFlush() != 0) Thread.Sleep(4);
            double now = Clock.Elapsed.TotalMilliseconds;
            if (now - lastFrame < 2) Thread.Sleep(2); // DWM idle: no busy loop
            lastFrame = Clock.Elapsed.TotalMilliseconds;

            foreach (var s in slides)
            {
                if (s.Cancelled) continue;
                if (s.StartMs < 0) s.StartMs = lastFrame;
                double t = s.DurationMs < 1 ? 1 : Math.Clamp((lastFrame - s.StartMs) / s.DurationMs, 0, 1);
                // Windows' flyout curves: a long decelerating entrance (≈ cubic-bezier(0,0,0,1)),
                // a short accelerating exit
                double shown = s.In ? s.From + (1 - s.From) * (1 - Math.Pow(1 - t, 5)) : s.From * (1 - t * t * t);
                s.Shown = shown;
                if (Native.GetWindowRect(s.Hwnd, out var r))
                    Native.SetWindowPos(s.Hwnd, IntPtr.Zero, s.X, s.TopFor(shown, r.Height), 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
                if (PerfLog.Enabled) PerfLog.Write($"motion {(s.In ? "in " : "out")} t={t:F3} shown={shown:F3}");
                if (t < 1) continue;
                lock (Gate) Active.Remove(s);
                s.Done(s);
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
