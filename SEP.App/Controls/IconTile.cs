using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace SEP.App.Controls;

public enum IconTileSize
{
    /// <summary>26 px: list rows, settings rows, section headers.</summary>
    Small,
    /// <summary>32 px: card headers, widgets, dialog rows.</summary>
    Medium,
    /// <summary>56 px: empty states.</summary>
    Large,
}

/// <summary>
/// Squircle icon tile in the Apple manner: a white glyph on the tone's system colour (the Settings-app row icon). With
/// <see cref="IsSubtle"/> it is the quiet form instead, a soft tint with the glyph in the tone colour (empty states,
/// inline hints). Template and tone colours: Themes/Generic.xaml.
/// </summary>
public class IconTile : Control
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(IconTile), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(BadgeTone), typeof(IconTile), new PropertyMetadata(BadgeTone.Accent));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(IconTileSize), typeof(IconTile), new PropertyMetadata(IconTileSize.Medium));

    public static readonly DependencyProperty IsSubtleProperty =
        DependencyProperty.Register(nameof(IsSubtle), typeof(bool), typeof(IconTile), new PropertyMetadata(false));

    public static readonly DependencyProperty FilledProperty =
        DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(IconTile), new PropertyMetadata(false));

    static IconTile()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IconTile), new FrameworkPropertyMetadata(typeof(IconTile)));
        FocusableProperty.OverrideMetadata(typeof(IconTile), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(IconTile), new FrameworkPropertyMetadata(false));
    }

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public BadgeTone Tone
    {
        get => (BadgeTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public IconTileSize Size
    {
        get => (IconTileSize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool IsSubtle
    {
        get => (bool)GetValue(IsSubtleProperty);
        set => SetValue(IsSubtleProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }
}
