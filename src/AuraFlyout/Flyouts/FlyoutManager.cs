using System.ComponentModel;
using System.Windows.Threading;
using AuraFlyout.Audio;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>Decides which flyout to show for each key press / media event.</summary>
public sealed class FlyoutManager
{
    private long _lastMediaKey;

    public MediaFlyout Media { get; }
    public VolumeFlyout Volume { get; }
    public LockKeysFlyout LockKeys { get; }
    public UpNextFlyout UpNext { get; }
    public BrightnessFlyout Brightness { get; }

    public FlyoutManager()
    {
        Media = new MediaFlyout();
        Volume = new VolumeFlyout();
        LockKeys = new LockKeysFlyout();
        UpNext = new UpNextFlyout();
        Brightness = new BrightnessFlyout();

        App.Media.TrackChanged += OnTrackChanged;
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
    }

    private static AppSettings S => SettingsStore.Current;

    private static bool Blocked => S.DisableInFullscreen && FullscreenDetector.IsFullscreenAppRunning();

    private bool ReplacesVolumeOsd => S.VolumeFlyoutEnabled || (S.MediaFlyoutEnabled && S.MediaFlyoutOnVolumeKeys);

    /// <summary>
    /// The volume keys AuraFlyout handles itself when it replaces the Windows overlay: the key never
    /// reaches Windows, so its overlay never appears (its window can't be found reliably on every
    /// Windows build: on recent ones it has none of its own). Not while a full-screen app blocks our
    /// flyouts: then Windows' own feedback is all there is. Called in the keyboard hook: quick checks only.
    /// </summary>
    public bool TakesKey(int vk) =>
        vk is Native.VK_VOLUME_UP or Native.VK_VOLUME_DOWN or Native.VK_VOLUME_MUTE
        && S.HideNativeVolumeOsd && ReplacesVolumeOsd && !Blocked;

    public void OnKey(int vk, bool down, bool repeat, bool taken)
    {
        switch (vk)
        {
            case Native.VK_MEDIA_PLAY_PAUSE or Native.VK_MEDIA_NEXT_TRACK or Native.VK_MEDIA_PREV_TRACK or Native.VK_MEDIA_STOP:
                if (down && !repeat) OnMediaKey();
                break;

            case Native.VK_VOLUME_UP or Native.VK_VOLUME_DOWN or Native.VK_VOLUME_MUTE:
                if (down)
                {
                    PerfLog.Write($"volume key {vk:X2} taken={taken} hideNative={S.HideNativeVolumeOsd} replaces={ReplacesVolumeOsd} blocked={Blocked} volume={App.Audio?.Volume}");
                    if (taken) ApplyVolumeKey(vk, repeat);
                    OnVolumeKey(taken);
                }
                break;

            case Native.VK_CAPITAL when S.LockKeysCaps:
                if (!down) ShowLock("Caps Lock", Native.VK_CAPITAL);
                break;
            case Native.VK_NUMLOCK when S.LockKeysNum:
                if (!down) ShowLock("Num Lock", Native.VK_NUMLOCK);
                break;
            case Native.VK_SCROLL when S.LockKeysScroll:
                if (!down) ShowLock("Scroll Lock", Native.VK_SCROLL);
                break;
            case Native.VK_INSERT when S.LockKeysInsert:
                if (!down) ShowLock("Insert", Native.VK_INSERT);
                break;
        }
    }

    private void OnMediaKey()
    {
        if (S.MediaFlyoutEnabled && S.HideNativeVolumeOsd) HideNativeOsdSoon();

        long now = Environment.TickCount64;
        if (now - _lastMediaKey < 150) return; // debounce key bursts
        _lastMediaKey = now;

        // media info updates a moment after the key press, show right away anyway
        ShowMedia();
    }

    /// <summary>Same feedback as a volume key press (used by the taskbar widget's scroll).</summary>
    public void OnVolumeKeyFeedback() => OnVolumeKey(taken: true);

    /// <summary>What Windows does with a volume key, done here for a key it never saw.</summary>
    private static void ApplyVolumeKey(int vk, bool repeat)
    {
        var audio = App.Audio;
        if (audio == null) return;
        if (vk == Native.VK_VOLUME_MUTE)
        {
            if (!repeat) audio.ToggleMute();
            return;
        }
        // like Windows: either volume key ends the mute
        if (audio.IsMuted) audio.IsMuted = false;
        audio.Step(vk == Native.VK_VOLUME_UP ? S.VolumeStep : -S.VolumeStep);
    }

    private void OnVolumeKey(bool taken)
    {
        // a key Windows saw still brings up its overlay: hide its window where one can be found
        if (!taken && S.HideNativeVolumeOsd && ReplacesVolumeOsd) HideNativeOsdSoon();
        if (Blocked) return;

        if (S.MediaFlyoutEnabled && S.MediaFlyoutOnVolumeKeys && App.Media.HasSession)
        {
            Volume.HideFlyout();
            Media.Show(emphasizeVolume: true);
        }
        else if (S.VolumeFlyoutEnabled)
        {
            Volume.Show(expanded: false);
        }
    }

    public void ShowMedia(bool force = false, Native.RECT? anchor = null)
    {
        if (!force && !S.MediaFlyoutEnabled) return;
        if (!App.Media.HasSession)
        {
            if (force) Volume.Show(expanded: false);
            return;
        }
        if (!force && Blocked) return;

        UpNext.HideFlyout();
        Media.Show(emphasizeVolume: false, anchor);
    }

    /// <param name="anchor">Taskbar item that was clicked: the flyout opens over it.</param>
    public void ToggleMedia(Native.RECT? anchor = null)
    {
        // opened by a click: it stays until a click elsewhere (or Esc), like the quick settings
        if (!App.Media.HasSession)
        {
            if (Volume.IsShown) Volume.HideFlyout();
            else if (!Volume.JustHidden)
            {
                Volume.OpenedByClick();
                Volume.Show(expanded: false);
            }
            return;
        }
        if (Media.IsShown) Media.HideFlyout();
        else if (!Media.JustHidden) // the click that closed it by taking focus away is not a re-open
        {
            Media.OpenedByClick();
            ShowMedia(force: true, anchor);
        }
    }

    /// <summary>
    /// The taskbar's speaker icon was clicked: the mixer opens above it and stays until a click
    /// elsewhere, like the quick settings it replaces there; a second click closes it.
    /// </summary>
    public void ToggleMixer(Native.RECT anchor)
    {
        if (Volume.IsOpenByClick)
        {
            Volume.HideFlyout();
            return;
        }
        if (Volume.JustHidden) return; // the click that closed it by taking focus away is not a re-open
        Media.HideFlyout();
        Volume.OpenedByClick();
        Volume.Show(expanded: true, anchor);
    }

    /// <summary>The wheel over the taskbar's speaker icon: the volume key's step and feedback.</summary>
    public void OnSpeakerWheel(int notches)
    {
        var audio = App.Audio;
        if (audio == null) return;
        if (audio.IsMuted) audio.IsMuted = false;
        audio.Step(notches * S.VolumeStep);
        // an open mixer already shows the change; otherwise the usual volume feedback
        if (!Volume.IsOpenByClick) OnVolumeKeyFeedback();
    }

    private void ShowLock(string name, int vk)
    {
        if (!S.LockKeysEnabled || Blocked) return;
        // the toggle state is final once the key is released
        LockKeys.ShowKey(name, KeyboardHook.IsToggled(vk));
    }

    private void OnTrackChanged()
    {
        if (!S.UpNextEnabled || Media.IsShown || Blocked) return;
        UpNext.ShowFlyout();
    }

    private static void HideNativeOsdSoon()
    {
        // the shell creates / re-shows its overlay asynchronously: try a few times
        NativeOsd.Hide();
        foreach (int delay in new[] { 60, 250, 700 })
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                NativeOsd.Hide();
            };
            t.Start();
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.HideNativeVolumeOsd) or nameof(AppSettings.VolumeFlyoutEnabled)
            or nameof(AppSettings.MediaFlyoutEnabled))
        {
            if (!S.HideNativeVolumeOsd || (!ReplacesVolumeOsd && !S.MediaFlyoutEnabled))
                NativeOsd.Restore();
        }
    }

    public void HideAll()
    {
        Media.HideFlyout();
        Volume.HideFlyout();
        LockKeys.HideFlyout();
        UpNext.HideFlyout();
        Brightness.HideFlyout();
    }
}
