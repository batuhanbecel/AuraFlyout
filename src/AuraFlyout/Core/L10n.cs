using System.Globalization;
using System.Windows;

namespace AuraFlyout.Core;

/// <summary>
/// Swaps the string dictionary at runtime. XAML uses {DynamicResource Key},
/// code uses <see cref="Get"/>.
/// </summary>
public static class L10n
{
    private static ResourceDictionary? _current;

    public static string ActiveLanguage { get; private set; } = "en";

    public static event Action? LanguageChanged;

    public static void Apply(string setting)
    {
        string lang = setting switch
        {
            "tr" => "tr",
            "en" => "en",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en",
        };

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/AuraFlyout;component/Resources/Strings.{lang}.xaml", UriKind.Absolute),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        ActiveLanguage = lang;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
