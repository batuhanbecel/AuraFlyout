using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace AuraFlyout.Controls;

/// <summary>bool → Visible/Collapsed. ConverterParameter="Invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ (parameter as string == "Invert");
}

/// <summary>Null/empty → Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;
        if (parameter as string == "Invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Number formats of the interface language (a binding's own culture is always en-US).</summary>
internal static class UiFormat
{
    public static bool Turkish => Core.L10n.ActiveLanguage == "tr";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Turkish ? "tr-TR" : "en-US");
    public static string Seconds(string n) => n + (Turkish ? " sn" : " s");
    public static string Percent(double n) => Turkish ? $"%{n}" : $"{n}%";
}

/// <summary>Milliseconds → "2.5 s" / "2,5 sn".</summary>
public sealed class MsToSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int ms ? UiFormat.Seconds((ms / 1000.0).ToString("0.0", UiFormat.Culture)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Whole seconds → "60 s" / "60 sn".</summary>
public sealed class SecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int s ? UiFormat.Seconds(s.ToString(CultureInfo.InvariantCulture)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>TimeSpan → m:ss / h:mm:ss.</summary>
public sealed class TimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TimeSpan t) return "0:00";
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", culture) : t.ToString(@"m\:ss", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"auto"/"tr"/"en" ↔ 0/1/2 for combo boxes.</summary>
public sealed class LanguageIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch { "tr" => 1, "en" => 2, _ => 0 };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch { 1 => "tr", 2 => "en", _ => "auto" };
}

/// <summary>Volume (0-100) + muted → the speaker glyph the Windows volume flyout shows (Segoe Fluent Icons).</summary>
public sealed class VolumeIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double vol = values.Length > 0 && values[0] is double d ? d : 0;
        bool muted = values.Length > 1 && values[1] is true;
        if (muted) return FluentIcons.Mute;
        if (vol <= 0) return FluentIcons.Volume0;
        if (vol < 33) return FluentIcons.Volume1;
        if (vol < 66) return FluentIcons.Volume2;
        return FluentIcons.Volume3;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Repeat mode int → glyph (off shows "repeat all", dimmed by <see cref="RepeatOpacityConverter"/>).</summary>
public sealed class RepeatIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 1 ? FluentIcons.RepeatOne : FluentIcons.RepeatAll;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Repeat mode int → icon opacity: off is drawn dimmed, like Windows' media controls.</summary>
public sealed class RepeatOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is 1 or 2 ? 1.0 : 0.55;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → 1.0 / 0.45 opacity (for toggle-like icon buttons).</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 1.0 : 0.45;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..100 → star GridLength (ConverterParameter="Rest" gives the remainder), for progress bars.</summary>
public sealed class PercentStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value is int i ? i : value is double d ? d : 0;
        v = Math.Clamp(v, 0, 100);
        return new GridLength(parameter as string == "Rest" ? 100 - v : v, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>int → "{0}%" ("%{0}" in Turkish).</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            double d => UiFormat.Percent(Math.Round(d)),
            int i => UiFormat.Percent(i),
            _ => "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
