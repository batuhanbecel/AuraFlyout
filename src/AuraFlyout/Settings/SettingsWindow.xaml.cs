using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AuraFlyout.Core;
using AuraFlyout.Settings.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace AuraFlyout.Settings;

public sealed partial class NavEntry : ObservableObject
{
    public NavEntry(string titleKey, SymbolRegular icon, Func<UserControl> factory, string groupKey = "")
    {
        TitleKey = titleKey;
        Icon = icon;
        Factory = factory;
        GroupKey = groupKey;
        Title = L10n.Get(titleKey);
        Group = groupKey.Length > 0 ? L10n.Get(groupKey) : "";
    }

    public string TitleKey { get; }
    public string GroupKey { get; }
    public SymbolRegular Icon { get; }
    public Func<UserControl> Factory { get; }

    [ObservableProperty] public partial string Title { get; set; }

    /// <summary>The navigation section the page sits in ("" = no header, the home page).</summary>
    [ObservableProperty] public partial string Group { get; set; }
}

public partial class SettingsWindow : FluentWindow
{
    private readonly ObservableCollection<NavEntry> _entries;
    private readonly System.Windows.Data.ListCollectionView _view;
    private readonly Dictionary<NavEntry, UserControl> _pages = [];

    public SettingsWindow()
    {
        InitializeComponent();
        FontFamily = Taskbar.DockWindow.TaskbarFont; // same typeface as the flyouts and taskbar items
        DataContext = SettingsStore.Current;

        // grouped like Windows Settings, by what the user deals with: media (the flyout, the taskbar
        // widget and its visualiser are one thing), what pops up on keys, what sits on the taskbar,
        // games, the app itself
        const string GMedia = "Nav_GroupMedia", GFlyouts = "Nav_GroupFlyouts", GTaskbar = "Nav_GroupTaskbar",
            GGames = "Nav_GroupGames", GGeneral = "Nav_GroupGeneral";
        _entries =
        [
            new NavEntry("Nav_Home", SymbolRegular.Home24, () => new HomePage()),
            new NavEntry("Nav_Media", SymbolRegular.MusicNote224, () => new MediaPage(), GMedia),
            new NavEntry("Nav_Taskbar", SymbolRegular.TabDesktopBottom24, () => new TaskbarPage(), GMedia),
            new NavEntry("Nav_Visualizer", SymbolRegular.DataBarVertical24, () => new VisualizerPage(), GMedia),
            new NavEntry("Nav_Volume", SymbolRegular.Speaker224, () => new VolumePage(), GFlyouts),
            new NavEntry("Nav_LockKeys", SymbolRegular.Keyboard24, () => new LockKeysPage(), GFlyouts),
            new NavEntry("Nav_Extras", SymbolRegular.BrightnessHigh24, () => new ExtrasPage(), GFlyouts),
            new NavEntry("Nav_Layout", SymbolRegular.TabDesktopMultipleBottom24, () => new LayoutPage(), GTaskbar),
            new NavEntry("Nav_Status", SymbolRegular.DesktopPulse24, () => new StatusPage(), GTaskbar),
            new NavEntry("Nav_Logi", SymbolRegular.Battery524, () => new LogiPage(), GTaskbar),
            new NavEntry("Nav_Speed", SymbolRegular.TopSpeed24, () => new SpeedPage(), GTaskbar),
            new NavEntry("Nav_Overlay", SymbolRegular.Games24, () => new OverlayPage(), GGames),
            new NavEntry("Nav_Filter", SymbolRegular.Filter24, () => new FilterPage(), GGeneral),
            new NavEntry("Nav_Hotkeys", SymbolRegular.Keyboard24, () => new HotkeysPage(), GGeneral),
            new NavEntry("Nav_System", SymbolRegular.Settings24, () => new SystemPage(), GGeneral),
            new NavEntry("Nav_About", SymbolRegular.Info24, () => new AboutPage(), GGeneral),
        ];
        _view = new System.Windows.Data.ListCollectionView(_entries);
        _view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(NavEntry.Group)));
        Nav.ItemsSource = _view;
        Nav.SelectedIndex = 0;

        L10n.LanguageChanged += OnLanguageChanged;
        ThemeService.Changed += ApplyTextScale;
        Closed += (_, _) =>
        {
            L10n.LanguageChanged -= OnLanguageChanged;
            ThemeService.Changed -= ApplyTextScale;
        };
        ApplyTextScale();
    }

    /// <summary>Settings › Accessibility › Text size enlarges the whole window content, like a WinUI app's text.</summary>
    private void ApplyTextScale()
    {
        double s = ThemeService.TextScale;
        Body.LayoutTransform = Math.Abs(s - 1) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
    }

    private void OnLanguageChanged()
    {
        foreach (var e in _entries)
        {
            e.Title = L10n.Get(e.TitleKey);
            e.Group = e.GroupKey.Length > 0 ? L10n.Get(e.GroupKey) : "";
        }
        var selected = Nav.SelectedItem;
        _view.Refresh(); // the group headers carry the language too
        Nav.SelectedItem = selected;
        _index = null; // titles changed: index again on the next search
        // value labels ("2,5 sn", "%40") are formatted by converters in the interface language
        foreach (var page in _pages.Values) RefreshTexts(page);
    }

    private static void RefreshTexts(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb)
            System.Windows.Data.BindingOperations.GetBindingExpression(tb, System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) RefreshTexts(d);
    }

    // ---------- "Find a setting" ----------

    /// <summary>One search result: a page, or a setting card on a page.</summary>
    private sealed record SearchHit(NavEntry Entry, string Title, string Page, string Text, FrameworkElement? Target);

    private List<SearchHit>? _index;

    /// <summary>Every page and every setting card (title + description), built on the first search.</summary>
    private List<SearchHit> Index()
    {
        if (_index != null) return _index;
        var list = new List<SearchHit>();
        foreach (var entry in _entries)
        {
            var page = PageFor(entry);
            list.Add(new SearchHit(entry, entry.Title, entry.Title, entry.Title, null));
            foreach (var card in Descendants(page).OfType<Controls.SettingCard>())
            {
                string title = card.Title ?? "";
                if (title.Length == 0) continue;
                list.Add(new SearchHit(entry, title, entry.Title, title + " " + card.Description, card));
            }
        }
        return _index = list;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            SearchPopup.IsOpen = false;
            return;
        }
        var culture = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text, string word) => culture.IndexOf(text, word, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = Index()
            .Where(h => words.All(w => Has(h.Text, w)))
            .OrderByDescending(h => Has(h.Title, q)) // title matches first, like Windows Settings
            .ThenBy(h => h.Target != null)
            .Take(12)
            .ToList();
        SearchResults.ItemsSource = hits;
        NoResults.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Visibility = hits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchPopup.IsOpen = true;
    }

    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            SearchPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Down && SearchResults.Items.Count > 0)
        {
            SearchPopup.IsOpen = true;
            SearchResults.SelectedIndex = 0;
            (SearchResults.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && SearchResults.Items.Count > 0)
        {
            Open((SearchHit)SearchResults.Items[0]);
            e.Handled = true;
        }
    }

    private void SearchResults_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SearchHit hit) Open(hit);
    }

    private void SearchResults_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && SearchResults.SelectedItem is SearchHit hit)
        {
            Open(hit);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            SearchPopup.IsOpen = false;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Opens the result's page and scrolls its setting into view with a short highlight.</summary>
    private void Open(SearchHit hit)
    {
        SearchPopup.IsOpen = false;
        Nav.SelectedItem = hit.Entry;
        if (hit.Target is not { } target) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            target.BringIntoView();
            if (!ThemeService.AnimationsEnabled) return;
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(220))
            {
                AutoReverse = true,
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2),
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            };
            target.BeginAnimation(OpacityProperty, pulse);
        });
    }

    private UserControl PageFor(NavEntry entry)
    {
        if (!_pages.TryGetValue(entry, out var page))
        {
            page = entry.Factory();
            page.DataContext = SettingsStore.Current;
            _pages[entry] = page;
        }
        return page;
    }

    public void Navigate(int index) => Nav.SelectedIndex = index;

    /// <summary>Selects the page whose navigation key is <paramref name="titleKey"/> (e.g. "Nav_Speed").</summary>
    public void Navigate(string titleKey)
    {
        var entry = _entries.FirstOrDefault(e => e.TitleKey == titleKey);
        if (entry != null) Nav.SelectedItem = entry;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not NavEntry entry) return;
        PageHost.Content = PageFor(entry);
        PageScroller.ScrollToTop();
        // WinUI's page transition: the new page slides up into place while it fades in
        Controls.Motion.Entrance(PageHost);
    }
}
