using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace AuraFlyout.Controls;

/// <summary>
/// Windows-11-Settings style row: icon, title, description on the left, any control on the right.
/// The template lives in Resources/Styles.xaml.
/// </summary>
public class SettingCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(SettingCard), new PropertyMetadata(SymbolRegular.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>
/// WinUI's SettingsExpander: a card whose header opens a group of related rows (the rows are
/// SettingCards drawn as "SettingRow": no card of their own, divided by lines). Opens with the
/// chevron turning and the rows sliding in.
/// </summary>
public class SettingExpander : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingExpander), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingExpander), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(SettingExpander), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty HeaderContentProperty =
        DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(SettingExpander), new PropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SettingExpander),
            new PropertyMetadata(false, (d, _) => ((SettingExpander)d).ApplyExpanded(animate: true)));

    private FrameworkElement? _body, _chevron;

    public SettingExpander()
    {
        // every SettingCard inside is drawn as a row of this group (set before the rows are styled)
        if (Application.Current?.TryFindResource("SettingRow") is Style row) Resources[typeof(SettingCard)] = row;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>A control in the header, left of the chevron (e.g. the group's on / off switch).</summary>
    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _body = GetTemplateChild("PART_Body") as FrameworkElement;
        _chevron = GetTemplateChild("PART_Chevron") as FrameworkElement;
        if (GetTemplateChild("PART_Header") is FrameworkElement header)
            header.MouseLeftButtonUp += (_, e) =>
            {
                // a click on the header's own control (switch) does not open / close the group
                if (e.OriginalSource is DependencyObject d && HeaderContent is DependencyObject hc && IsInside(d, hc)) return;
                IsExpanded = !IsExpanded;
            };
        ApplyExpanded(animate: false);
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var p = d; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p == container) return true;
        return false;
    }

    private void ApplyExpanded(bool animate)
    {
        if (_body == null) return;
        _body.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (_chevron != null) Motion.Rotate(_chevron, IsExpanded ? 180 : 0, animate ? 200 : 0);
        if (IsExpanded && animate) Motion.Entrance(_body, offset: -10, ms: 300);
    }
}

/// <summary>Section header used between groups of setting cards.</summary>
public class SettingHeader : System.Windows.Controls.TextBlock
{
}
