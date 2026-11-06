using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using AuraFlyout.Audio;
using AuraFlyout.Core;
using Wpf.Ui.Controls;

namespace AuraFlyout.Flyouts;

/// <summary>Replacement for the Windows volume overlay, with an optional per-app mixer.</summary>
public partial class VolumeFlyout : FlyoutWindow
{
    private bool _expanded;

    public VolumeFlyout()
    {
        InitializeComponent();
        DataContext = App.Audio;
        App.Audio.DeviceChanged += () =>
        {
            if (_expanded) RefreshMixer();
        };
    }

    protected override int DurationMs => _expanded ? Math.Max(6000, SettingsStore.Current.VolumeFlyoutDuration) : SettingsStore.Current.VolumeFlyoutDuration;
    protected override int PositionIndex => SettingsStore.Current.VolumeFlyoutPosition;
    protected override FrameworkElement Surface => Root;

    /// <param name="anchor">Taskbar item that was clicked (the speaker icon): the flyout opens over it.</param>
    public void Show(bool expanded, Native.RECT? anchor = null)
    {
        App.Audio.SyncFromDevice();
        if (expanded != _expanded) SetExpanded(expanded);
        if (anchor != null) ShowFlyout(anchor);
        else ShowFlyout();
    }

    internal void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        MixerPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded && IsShown) Controls.Motion.Entrance(MixerPanel, offset: -8, ms: 300);
        ExpandIcon.Text = expanded ? Controls.FluentIcons.ChevronUp : Controls.FluentIcons.Mixer;
        if (expanded) RefreshMixer();
        App.Audio.SetPeakMonitoring(expanded && SettingsStore.Current.VolumeMixerHighlightActive);
    }

    private void RefreshMixer()
    {
        App.Audio.RefreshSessions();
        EmptyText.Visibility = App.Audio.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnHidden()
    {
        // collapse again next time (and stop meters)
        if (_expanded) SetExpanded(false);
        ShowOutputs(false);
    }

    private void Devices_Click(object sender, RoutedEventArgs e)
    {
        ShowOutputs(OutputsPane.Visibility != Visibility.Visible);
        Touch();
    }

    /// <summary>
    /// The device list takes the app list's place, like "Select a sound output" in the quick
    /// settings. It gets the app list's height (at least two rows; more scroll), so the flyout keeps its
    /// size: the window is anchored at its bottom edge, and a resize moves it at once while the new
    /// layout is drawn a frame later, which shows as a flicker.
    /// </summary>
    private void ShowOutputs(bool show)
    {
        bool shown = OutputsPane.Visibility == Visibility.Visible;
        DeviceChevron.Text = show ? Controls.FluentIcons.ChevronUp : "";
        if (show == shown) return;
        if (show)
        {
            OutputList.ItemsSource = App.Audio.GetOutputs();
            MixerBody.Height = Math.Max(MixerBody.ActualHeight, 72);
            SessionsPane.Visibility = Visibility.Collapsed;
            OutputsPane.Visibility = Visibility.Visible;
            OutputsPane.ScrollToTop();
            if (IsShown) Controls.Motion.Entrance(OutputsPane, offset: -8, ms: 250);
        }
        else
        {
            OutputsPane.Visibility = Visibility.Collapsed;
            SessionsPane.Visibility = Visibility.Visible;
            MixerBody.Height = double.NaN; // the app list's own height again: the same as before
            if (IsShown) Controls.Motion.Entrance(SessionsPane, offset: -8, ms: 250);
        }
    }

    private void Output_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AudioOutput o } && !o.IsDefault) App.Audio.SetDefaultOutput(o.Id);
        ShowOutputs(false); // the mixer refreshes itself when Windows reports the new default device
        Touch();
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(!_expanded);
        Touch();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        App.Audio.ToggleMute();
        Touch();
    }

    private void SessionMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AudioSessionItem item }) item.IsMuted = !item.IsMuted;
        Touch();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Touch();
    private void Slider_DragStarted(object sender, DragStartedEventArgs e) => KeepOpen = true;

    private void Slider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        KeepOpen = false;
        Touch();
    }

    private void SoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open sound settings", ex);
        }
        HideFlyout();
    }
}
