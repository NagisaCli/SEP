using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace SEP.App.Controls;

/// <summary>
/// The one section-header pattern: small icon tile + title (+ optional caption), and whatever the Content holds
/// (a link button, a badge) right-aligned. <see cref="Symbol"/> Empty hides the tile.
/// </summary>
public class SectionHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(SectionHeader), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(BadgeTone), typeof(SectionHeader), new PropertyMetadata(BadgeTone.Accent));

    /// <summary>Card-internal header (14 px title) instead of a section header between cards (16 px).</summary>
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(SectionHeader), new PropertyMetadata(false));

    static SectionHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SectionHeader), new FrameworkPropertyMetadata(typeof(SectionHeader)));
        FocusableProperty.OverrideMetadata(typeof(SectionHeader), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SectionHeader), new FrameworkPropertyMetadata(false));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
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

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }
}
