using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AuraFlyout.Core;
using Wpf.Ui.Controls;

namespace AuraFlyout.Taskbar;

/// <summary>Media widget + audio visualizer, docked in the taskbar.</summary>
public partial class TaskbarHost : DockWindow
{
    private bool? _barsAccent;
    private bool _renderHooked;
    private readonly PropertyChangedEventHandler _mediaHandler;

    public TaskbarHost()
    {
        InitializeComponent();

        _mediaHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(Media.MediaService.Cover) or nameof(Media.MediaService.IsPlaying)) UpdateUi();
            if (e.PropertyName == nameof(Media.MediaService.Cover) && IsDocked) Controls.Motion.FadeIn(CoverBox, 280); // new cover fades in
        };
        App.Media.Updated += UpdateUi;
        App.Media.PropertyChanged += _mediaHandler;
        SettingsStore.Current.PropertyChanged += OnSettingChanged;
        ThemeService.Changed += UpdateUi;
        Loaded += (_, _) => UpdateUi();
    }

    private static AppSettings S => SettingsStore.Current;

    public override string Id => "media";
    public override int OffsetDip => S.TaskbarWidgetOffset;
    public override bool WantsVisible => WidgetVisible || BarsVisible;

    private bool WidgetVisible => S.TaskbarWidgetEnabled && (App.Media.HasSession || !S.TaskbarWidgetHideWhenIdle);
    private bool BarsVisible => S.VisualizerEnabled && App.Media.HasSession;

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && (e.PropertyName.StartsWith("TaskbarWidget") || e.PropertyName.StartsWith("Visualizer")))
            UpdateUi();
    }

    public void UpdateUi()
    {
        var m = App.Media;

        Widget.Visibility = WidgetVisible ? Visibility.Visible : Visibility.Collapsed;
        Bars.Visibility = BarsVisible ? Visibility.Visible : Visibility.Collapsed;

        PlaceBars();

        TitleText.Text = m.HasSession ? (string.IsNullOrEmpty(m.Title) ? m.AppName : m.Title) : L10n.Get("Widget_NoMedia");
        ArtistText.Text = m.HasSession ? m.Artist : L10n.Get("Widget_NoMediaSub");
        ArtistText.Visibility = string.IsNullOrEmpty(ArtistText.Text) ? Visibility.Collapsed : Visibility.Visible;

        CoverBox.Visibility = S.TaskbarWidgetShowCover ? Visibility.Visible : Visibility.Collapsed;
        CoverBrush.ImageSource = m.Cover;
        NoteIcon.Visibility = m.Cover == null ? Visibility.Visible : Visibility.Collapsed;

        bool dim = S.TaskbarWidgetPauseDim && m.HasSession && !m.IsPlaying;
        PausedOverlay.Visibility = dim && m.Cover != null ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Opacity = dim ? 0.7 : 1;

        ControlsPanel.Visibility = S.TaskbarWidgetShowControls && m.HasSession ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.IsEnabled = m.CanPrevious;
        NextButton.IsEnabled = m.CanNext;
        PlayButton.IsEnabled = m.CanPlayPause;
        PlayIcon.Text = m.IsPlaying ? Controls.FluentIcons.Pause : Controls.FluentIcons.Play;

        if (S.TaskbarWidgetFixedWidth)
        {
            int n = Math.Clamp(S.VisualizerBarCount, 1, 24);
            double inlineBars = Bars.Parent == WidgetRow && Bars.Visibility == Visibility.Visible
                ? n * Bars.BarWidth + (n - 1) * Bars.BarGap + Bars.Margin.Left + Bars.Margin.Right : 0;
            double fixedText = Math.Max(60, S.TaskbarWidgetWidth - (S.TaskbarWidgetShowCover ? 50 : 10) - (ControlsPanel.Visibility == Visibility.Visible ? 90 : 0) - inlineBars);
            TextPanel.Width = fixedText;
            TextPanel.MaxWidth = fixedText;
        }
        else
        {
            TextPanel.Width = double.NaN;
            TextPanel.MaxWidth = Math.Max(80, S.TaskbarWidgetWidth - 30);
        }

        if (_barsAccent != S.VisualizerUseAccent)
        {
            _barsAccent = S.VisualizerUseAccent;
            Bars.SetResourceReference(Controls.SpectrumBars.BarBrushProperty, _barsAccent == true ? "AuraTaskbarAccentBrush" : "AuraTaskbarForeground");
        }
        Bars.Centered = S.VisualizerCentered;
        Bars.BarCount = Math.Clamp(S.VisualizerBarCount, 1, 24);

        UpdateRenderHook();
        RequestLayout();
    }

    /// <summary>
    /// The visualizer before or after the widget, or in it next to the song's title (smaller there, to
    /// sit with the two text lines). Only touches the tree when the place changes.
    /// </summary>
    private void PlaceBars()
    {
        bool inside = S.VisualizerPosition == 2 && WidgetVisible;
        var target = inside ? WidgetRow : Row;
        int wanted = inside ? WidgetRow.Children.IndexOf(TextPanel) + 1 : S.VisualizerPosition == 1 ? Row.Children.Count - 1 : 0;
        if (Bars.Parent == target && target.Children.IndexOf(Bars) == wanted) return;

        (Bars.Parent as System.Windows.Controls.Panel)?.Children.Remove(Bars);
        int index = inside ? WidgetRow.Children.IndexOf(TextPanel) + 1 : S.VisualizerPosition == 1 ? Row.Children.Count : 0;
        target.Children.Insert(index, Bars);
        Bars.Height = inside ? 18 : 22;
        Bars.Margin = inside ? new Thickness(6, 0, 4, 0) : new Thickness(6, 0, 6, 0);
    }

    protected override void OnDockVisibilityChanged(bool visible) => UpdateRenderHook();

    // A plain 30 fps timer, not CompositionTarget.Rendering: subscribing to Rendering keeps WPF
    // rendering every frame of the monitor (144 Hz and more), and this window is a layered one whose
    // every frame is copied to the taskbar. Silent frames are not even drawn (see SpectrumBars.Levels).
    private System.Windows.Threading.DispatcherTimer? _barsTimer;

    private void UpdateRenderHook()
    {
        bool want = BarsVisible && IsDocked && App.Spectrum.IsRunning;
        if (want && !_renderHooked)
        {
            _barsTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33),
            };
            _barsTimer.Tick -= OnBarsTick;
            _barsTimer.Tick += OnBarsTick;
            _barsTimer.Start();
            _renderHooked = true;
        }
        else if (!want && _renderHooked)
        {
            _barsTimer?.Stop();
            _renderHooked = false;
            Bars.Levels = new float[Bars.BarCount];
        }
    }

    private void OnBarsTick(object? sender, EventArgs e)
    {
        if (!App.Spectrum.IsRunning)
        {
            UpdateRenderHook(); // capture stopped (paused for a while): stop drawing too
            return;
        }
        Bars.Levels = App.Spectrum.GetBands(Bars.BarCount, S.VisualizerSensitivity);
    }

    /// <summary>The visualizer capture started or stopped (App's spectrum controller).</summary>
    public void OnSpectrumStateChanged() => UpdateRenderHook();

    // ---------- input ----------

    private void Widget_MouseEnter(object sender, MouseEventArgs e)
    {
        if (S.TaskbarWidgetHoverBackground) Widget.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AuraTaskbarHover");
    }

    private void Widget_MouseLeave(object sender, MouseEventArgs e) => Widget.Background = Brushes.Transparent;

    private void Widget_Click(object sender, MouseButtonEventArgs e)
    {
        if (!App.Media.HasSession)
        {
            App.Instance.OpenSettings();
            return;
        }
        if (S.TaskbarWidgetClickOpensFlyout) App.Flyouts.ToggleMedia(S.MediaFlyoutAboveWidget ? ScreenRect : null);
        else App.Media.OpenPlayer();
    }

    private void Widget_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) App.Media.PlayPause();
    }

    private void Widget_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!S.TaskbarWidgetScrollVolume) return;
        App.Audio.Step(e.Delta > 0 ? S.VolumeStep : -S.VolumeStep);
        App.Flyouts.OnVolumeKeyFeedback();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => App.Media.Previous();
    private void Play_Click(object sender, RoutedEventArgs e) => App.Media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => App.Media.Next();

    protected override void OnClosed(EventArgs e)
    {
        _barsTimer?.Stop();
        App.Media.Updated -= UpdateUi;
        App.Media.PropertyChanged -= _mediaHandler;
        SettingsStore.Current.PropertyChanged -= OnSettingChanged;
        ThemeService.Changed -= UpdateUi;
        base.OnClosed(e);
    }
}
