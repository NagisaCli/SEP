using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SEP.App.Converters;

public class BoolToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "★" : "☆";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LockToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new((Color)ColorConverter.ConvertFromString("#064E3B"));
    private static readonly SolidColorBrush AmberBrush = new((Color)ColorConverter.ConvertFromString("#78350F"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? AmberBrush : GreenBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LockToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenText = new((Color)ColorConverter.ConvertFromString("#34D399"));
    private static readonly SolidColorBrush AmberText = new((Color)ColorConverter.ConvertFromString("#FBBF24"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? AmberText : GreenText;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// Picks one of several localized texts by a bool/enum/int selector so the choice re-evaluates when the
/// language changes. values[0] is the selector; a bool picks values[1] (true) or values[2] (false);
/// an enum or int n picks values[1 + n].
/// </summary>
public class SwitchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        int index = values[0] switch
        {
            bool b => b ? 0 : 1,
            Enum e => System.Convert.ToInt32(e),
            int i => i,
            _ => 0,
        };
        int slot = 1 + index;
        return slot < values.Length ? values[slot] ?? string.Empty : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>string.Format with a localized format string: values[0] is the format, the rest are arguments.</summary>
public class FormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not string format) return string.Empty;
        var args = new object[values.Length - 1];
        for (int i = 1; i < values.Length; i++)
        {
            args[i - 1] = values[i] == DependencyProperty.UnsetValue ? string.Empty : values[i];
        }
        try { return string.Format(culture, format, args); }
        catch (FormatException) { return format; }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>Joins a sequence of strings with ", "; an empty sequence renders as an em dash.</summary>
public class JoinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is System.Collections.Generic.IEnumerable<string> items)
        {
            string joined = string.Join(", ", items);
            return joined.Length > 0 ? joined : "—";
        }
        return value?.ToString() ?? "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>Highlights the active filter tab: Primary when the bound value equals the parameter, else Secondary.</summary>
public class SelectedToAppearanceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// A stable accent colour for a name (project, user, team): the same ten-colour palette the web UI used, picked
/// by a hash of the text so an item keeps its colour across sessions and pages.
/// </summary>
public class PaletteConverter : IValueConverter
{
    private static readonly SolidColorBrush[] Palette = CreatePalette(
        "#4F8CFF", "#2DD4A7", "#F5B85C", "#FF5D6C", "#B07BFF", "#36B6E8", "#FF8F6B", "#8BD66B", "#E86B9A", "#9AA8FF");

    private static SolidColorBrush[] CreatePalette(params string[] hex)
    {
        var brushes = new SolidColorBrush[hex.Length];
        for (int i = 0; i < hex.Length; i++)
        {
            brushes[i] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex[i]));
            brushes[i].Freeze();
        }
        return brushes;
    }

    public static SolidColorBrush ForName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return Palette[0];
        int hash = 0;
        foreach (char c in name.ToUpperInvariant()) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return Palette[hash % Palette.Length];
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => ForName(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
