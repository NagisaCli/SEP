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
