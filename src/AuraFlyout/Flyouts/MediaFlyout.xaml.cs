using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AuraFlyout.Core;
using Wpf.Ui.Controls;

namespace AuraFlyout.Flyouts;

public partial class MediaFlyout : FlyoutWindow
{
    private readonly DispatcherTimer _tick;
    private bool _seeking;
    private bool _volumeOnly;

    public MediaFlyout()
    {
        InitializeComponent();
        DataContext = App.Media;

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += (_, _) => UpdateLive();

        // A press on the bar (not the thumb) moves the value to that point and is marked handled by
        // the slider itself, so a plain handler never hears it: listen to handled ones too.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_MouseDown), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Seek_MouseUp), handledEventsToo: true);

        App.Media.PropertyChanged += OnMediaChanged;
        SettingsStore.Current.PropertyChanged += OnSettingsChanged;
        ShownChanged += shown =>
        {
            if (shown) _tick.Start();
            else _tick.Stop();
        };
        ApplyLayout();
        UpdatePlayIcon();
    }

    protected override int DurationMs => SettingsStore.Current.MediaFlyoutDuration;
    // shown by a volume key it stands in for the volume indicator: it appears where that one would
    protected override int PositionIndex => _volumeOnly ? SettingsStore.Current.VolumeFlyoutPosition : SettingsStore.Current.MediaFlyoutPosition;
    protected override FrameworkElement Surface => Root;

    /// <summary>
    /// Shows the flyout. <paramref name="emphasizeVolume"/> forces the volume row on;
    /// <paramref name="anchor"/> opens it over that taskbar item.
    /// </summary>
    public void Show(bool emphasizeVolume, Core.Native.RECT? anchor = null)
    {
        _volumeOnly = emphasizeVolume;
        ApplyLayout();
        UpdateLive();
        ShowFlyout(anchor);
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Media.MediaService.IsPlaying)) UpdatePlayIcon();
        // a new cover fades in instead of popping
        if (e.PropertyName == nameof(Media.MediaService.Cover) && IsShown) Controls.Motion.FadeIn(CoverBorder, 280);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var s = SettingsStore.Current;
        bool compact = s.MediaFlyoutCompact;
        CoverBorder.Width = CoverBorder.Height = compact ? 56 : 84;
        Root.Width = compact ? 330 : 368;
        SeekRow.Visibility = !compact && s.MediaFlyoutShowSeekbar ? Visibility.Visible : Visibility.Collapsed;
        VolumeRow.Visibility = s.MediaFlyoutShowVolume || _volumeOnly ? Visibility.Visible : Visibility.Collapsed;
        PlayerRow.Visibility = !compact && s.MediaFlyoutShowPlayer ? Visibility.Visible : Visibility.Collapsed;
        FlyoutSpectrum.Visibility = s.VisualizerInFlyout && s.VisualizerEnabled && !compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlayIcon() =>
        PlayIcon.Text = App.Media.IsPlaying ? Controls.FluentIcons.Pause : Controls.FluentIcons.Play;

    internal void UpdateLive()
    {
        var media = App.Media;
        double duration = media.Duration.TotalSeconds;
        if (!_seeking)
        {
            SeekSlider.Maximum = Math.Max(1, duration);
            SeekSlider.Value = Math.Min(SeekSlider.Maximum, media.Position.TotalSeconds);
            PositionText.Text = FormatTime(media.Position);
        }
        else
        {
            PositionText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        if (FlyoutSpectrum.Visibility == Visibility.Visible)
            FlyoutSpectrum.Levels = App.Spectrum.GetBands(24, SettingsStore.Current.VisualizerSensitivity);

        UpdateLyrics();
    }

    private void UpdateLyrics()
    {
        var (now, next) = App.Lyrics.At(App.Media.Position);
        bool show = now != null && SettingsStore.Current.MediaFlyoutLyrics && !SettingsStore.Current.MediaFlyoutCompact;
        LyricsRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        if (LyricNow.Text != now)
        {
            LyricNow.Text = now;
            Controls.Motion.Entrance(LyricNow, offset: 8, ms: 260); // the next line rises into place
        }
        string nextText = next ?? "";
        if (LyricNext.Text != nextText) LyricNext.Text = nextText;
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------- interaction ----------

    private void PlayPause_Click(object sender, RoutedEventArgs e) { App.Media.PlayPause(); Touch(); }
    private void Next_Click(object sender, RoutedEventArgs e) { App.Media.Next(); Touch(); }
    private void Previous_Click(object sender, RoutedEventArgs e) { App.Media.Previous(); Touch(); }
    private void Shuffle_Click(object sender, RoutedEventArgs e) { App.Media.ToggleShuffle(); Touch(); }
    private void Repeat_Click(object sender, RoutedEventArgs e) { App.Media.CycleRepeat(); Touch(); }
    private void SwitchSession_Click(object sender, RoutedEventArgs e) { App.Media.SwitchSession(); Touch(); }
    private void OpenPlayer_Click(object sender, RoutedEventArgs e) { App.Media.OpenPlayer(); HideFlyout(); }
    private void Cover_Click(object sender, MouseButtonEventArgs e) { App.Media.OpenPlayer(); HideFlyout(); }
    private void Mute_Click(object sender, RoutedEventArgs e) { App.Audio.ToggleMute(); Touch(); }
    private void AnySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Touch();

    private bool _dragging;

    /// <summary>
    /// Pressed on the bar: the live position stops moving the bar right away, or it would put the
    /// clicked value back (every 33 ms) before the button is released.
    /// </summary>
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled) return;
        _seeking = true;
        KeepOpen = true;
    }

    private void Seek_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragging = true;
        _seeking = true;
        KeepOpen = true;
    }

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        EndSeek();
    }

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // a click on the bar (a drag ends in Seek_DragCompleted)
        if (_dragging || !_seeking) return;
        // after the slider has moved its value to the clicked point
        Dispatcher.BeginInvoke(EndSeek, DispatcherPriority.Input);
    }

    private void EndSeek()
    {
        if (!_seeking) return;
        App.Media.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        _seeking = false;
        KeepOpen = false;
        Touch();
    }
}
