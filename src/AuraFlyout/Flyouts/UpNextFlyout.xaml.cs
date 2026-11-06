using System.Windows;
using System.Windows.Input;
using AuraFlyout.Core;

namespace AuraFlyout.Flyouts;

/// <summary>Small "Up next" card shown when the track changes on its own.</summary>
public partial class UpNextFlyout : FlyoutWindow
{
    public UpNextFlyout()
    {
        InitializeComponent();
        DataContext = App.Media;
    }

    protected override int DurationMs => SettingsStore.Current.UpNextDuration;
    protected override int PositionIndex => SettingsStore.Current.UpNextPosition;
    protected override FrameworkElement Surface => Root;

    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        HideFlyout();
        App.Flyouts.ShowMedia(force: true);
    }
}
