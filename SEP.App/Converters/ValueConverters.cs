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

/// <summary>
/// Status token ("进行中", "已完成", …) to its localized label. values[0] is the token; values[1] is any
/// Loc indexer binding so the label re-evaluates when the language changes.
/// </summary>
public class StatusLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string token = values.Length > 0 ? values[0] as string ?? string.Empty : string.Empty;
        return ViewModels.StatusOption.LabelOf(token);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>A hex colour string ("#4F8CFF") to a frozen brush; anything unparsable gives the neutral text colour.</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && s.Length > 0)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
                brush.Freeze();
                return brush;
            }
            catch (FormatException) { }
        }
        return Application.Current?.TryFindResource("SepText2Brush") ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>Bytes to a short human size ("12.4 KB").</summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double bytes = value switch { long l => l, int i => i, double d => d, _ => 0 };
        string[] units = { "B", "KB", "MB", "GB" };
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return u == 0 ? $"{bytes:0} {units[u]}" : $"{bytes:0.#} {units[u]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>A fraction 0..1 to a width in pixels for the distribution bars: parameter is the full width.</summary>
public class FractionToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fraction = value is double d ? d : 0;
        double full = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 200;
        return Math.Max(2, Math.Round(full * Math.Clamp(fraction, 0, 1)));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>A fraction 0..1 to a star GridLength; parameter "rest" gives the complement, so two columns split a track.</summary>
public class FractionToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fraction = Math.Clamp(value is double d ? d : 0, 0, 1);
        if (string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase)) fraction = 1 - fraction;
        return new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>Visible when a number is not zero (or a collection is not empty).</summary>
public class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool nonZero = value switch
        {
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => false,
        };
        return nonZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>Visible when a number is zero (or a collection is empty).</summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isZero = value switch
        {
            int i => i == 0,
            long l => l == 0,
            double d => Math.Abs(d) < 0.0001,
            System.Collections.ICollection c => c.Count == 0,
            _ => true,
        };
        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
