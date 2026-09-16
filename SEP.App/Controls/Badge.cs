using System.Windows;
using System.Windows.Controls;

namespace SEP.App.Controls;

public enum BadgeTone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger,
    Info,
    Purple,
}

/// <summary>
/// A small rounded status pill ("FREE", "cached", "● Active"). The template in Themes/SepStyles.xaml picks the
/// soft background / strong foreground pair for <see cref="Tone"/> from the current theme.
/// </summary>
public class Badge : ContentControl
{
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(BadgeTone), typeof(Badge), new PropertyMetadata(BadgeTone.Neutral));

    /// <summary>Filled pill (strong background, on-accent text) instead of the soft tint; used for the primary state.</summary>
    public static readonly DependencyProperty IsSolidProperty =
        DependencyProperty.Register(nameof(IsSolid), typeof(bool), typeof(Badge), new PropertyMetadata(false));

    static Badge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(typeof(Badge)));
    }

    public BadgeTone Tone
    {
        get => (BadgeTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public bool IsSolid
    {
        get => (bool)GetValue(IsSolidProperty);
        set => SetValue(IsSolidProperty, value);
    }
}
