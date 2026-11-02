using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
/// A small status capsule ("Free", "In progress", "2 online"), or with <see cref="IsPlain"/> just a coloured dot and
/// text. The template in Themes/Generic.xaml picks the soft tint / accessible text pair for <see cref="Tone"/> from
/// the current theme.
/// </summary>
public class Badge : ContentControl
{
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(BadgeTone), typeof(Badge), new PropertyMetadata(BadgeTone.Neutral));

    /// <summary>Filled pill (strong background, on-accent text) instead of the soft tint; used for the primary state.</summary>
    public static readonly DependencyProperty IsSolidProperty =
        DependencyProperty.Register(nameof(IsSolid), typeof(bool), typeof(Badge), new PropertyMetadata(false));

    /// <summary>Leading status dot: the "dot + pill" form used for states (project status, presence, reachability).</summary>
    public static readonly DependencyProperty HasDotProperty =
        DependencyProperty.Register(nameof(HasDot), typeof(bool), typeof(Badge), new PropertyMetadata(false));

    /// <summary>
    /// Plain status: no capsule, just the tone-coloured dot and secondary-label text ("● In progress"), the quiet form
    /// used on cards and rows. Implies the dot.
    /// </summary>
    public static readonly DependencyProperty IsPlainProperty =
        DependencyProperty.Register(nameof(IsPlain), typeof(bool), typeof(Badge), new PropertyMetadata(false));

    /// <summary>Colour of the dot when it differs from the text (a category colour on a neutral pill); null = Foreground.</summary>
    public static readonly DependencyProperty DotBrushProperty =
        DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(Badge), new PropertyMetadata(null));

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

    public bool HasDot
    {
        get => (bool)GetValue(HasDotProperty);
        set => SetValue(HasDotProperty, value);
    }

    public bool IsPlain
    {
        get => (bool)GetValue(IsPlainProperty);
        set => SetValue(IsPlainProperty, value);
    }

    public Brush? DotBrush
    {
        get => (Brush?)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }
}
