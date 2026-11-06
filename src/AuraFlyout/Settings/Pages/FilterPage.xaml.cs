using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraFlyout.Core;
using AuraFlyout.Media;

namespace AuraFlyout.Settings.Pages;

public partial class FilterPage : UserControl
{
    public sealed record ActiveApp(string Name, string Id);

    public FilterPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshActive();
        SettingsStore.Current.ExcludedApps.CollectionChanged += (_, _) => RefreshActive();
    }

    private void RefreshActive()
    {
        // all sessions (including excluded ones are hidden by the service) – show what can still be excluded
        var items = App.Media.Sessions
            .Select(s => s.SourceAppUserModelId)
            .Distinct()
            .Select(id => new ActiveApp(AppInfoResolver.Resolve(id).Name, id))
            .ToList();
        ActiveList.ItemsSource = items;
        NoActive.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AddExclusion(string? id)
    {
        id = id?.Trim();
        if (string.IsNullOrEmpty(id)) return;
        var list = SettingsStore.Current.ExcludedApps;
        if (!list.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) list.Add(id);
    }

    private void Exclude_Click(object sender, RoutedEventArgs e) => AddExclusion((sender as FrameworkElement)?.Tag as string);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddExclusion(NewApp.Text);
        NewApp.Text = "";
    }

    private void NewApp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add_Click(sender, e);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) SettingsStore.Current.ExcludedApps.Remove(id);
    }
}
