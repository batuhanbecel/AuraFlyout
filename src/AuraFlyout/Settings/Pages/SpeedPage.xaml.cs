using System.Windows;
using System.Windows.Controls;
using AuraFlyout.Core;

namespace AuraFlyout.Settings.Pages;

public partial class SpeedPage : UserControl
{
    private bool _loading;

    public SpeedPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            Select(LengthCombo, SettingsStore.Current.SpeedSeconds, 1);
            Select(AutoCombo, SettingsStore.Current.SpeedAutoTestHours, 0);
            _loading = false;
            UpdateRunButton();
        };
        App.Speed.Updated += UpdateRunButton;
        Unloaded += (_, _) => App.Speed.Updated -= UpdateRunButton;
    }

    private static void Select(ComboBox combo, int value, int fallback)
    {
        combo.SelectedIndex = fallback;
        foreach (ComboBoxItem item in combo.Items)
            if (int.Parse((string)item.Tag) == value) combo.SelectedItem = item;
    }

    private static int TagOf(ComboBox combo) => combo.SelectedItem is ComboBoxItem i ? int.Parse((string)i.Tag) : 0;

    private void Length_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) SettingsStore.Current.SpeedSeconds = TagOf(LengthCombo);
    }

    private void Auto_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) SettingsStore.Current.SpeedAutoTestHours = TagOf(AutoCombo);
    }

    private void UpdateRunButton() =>
        RunButton.Content = L10n.Get(App.Speed.IsRunning ? "Speed_Stop" : "Speed_RunTest");

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        App.Speed.Toggle();
        UpdateRunButton();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => SetClipboard(App.Speed.LastAsText());
    private void Csv_Click(object sender, RoutedEventArgs e) => SetClipboard(App.Speed.HistoryAsCsv());
    private void Clear_Click(object sender, RoutedEventArgs e) => App.Speed.ClearHistory();

    private static void SetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn("Clipboard failed", ex); }
    }

    private void Layout_Click(object sender, RoutedEventArgs e) => App.Instance.OpenSettings("Nav_Layout");
}
