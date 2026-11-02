using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SEP.App.Behaviors;

namespace SEP.App.Controls;

/// <summary>
/// The segmented control: a grey track with a raised thumb under the checked segment; when the selection changes the
/// thumb slides over (spring ease) instead of jumping. Its children are the segments, toggle buttons such as
/// RadioButtons with the SepSegmentItem style; the panel only draws (track, thumb) and lays out, so every binding,
/// Click handler and command of the segments keeps working. Segments share one width unless
/// <see cref="IsUniform"/> is false. Default look: Themes/Generic.xaml.
/// </summary>
public class SegmentedPanel : Panel
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InsetProperty =
        DependencyProperty.Register(nameof(Inset), typeof(double), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsUniformProperty =
        DependencyProperty.Register(nameof(IsUniform), typeof(bool), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ThumbBrushProperty =
        DependencyProperty.Register(nameof(ThumbBrush), typeof(Brush), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbBorderBrushProperty =
        DependencyProperty.Register(nameof(ThumbBorderBrush), typeof(Brush), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Soft shadow under the thumb (light theme); transparent draws none.</summary>
    public static readonly DependencyProperty ThumbShadowColorProperty =
        DependencyProperty.Register(nameof(ThumbShadowColor), typeof(Color), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(Colors.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ThumbLeftProperty =
        DependencyProperty.Register("ThumbLeft", typeof(double), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ThumbWidthProperty =
        DependencyProperty.Register("ThumbWidth", typeof(double), typeof(SegmentedPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(300));

    private Rect _target = Rect.Empty;   // where the thumb is (or is heading)
    private bool _placed;                // the thumb sits on a segment; moves from there animate

    static SegmentedPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SegmentedPanel), new FrameworkPropertyMetadata(typeof(SegmentedPanel)));
    }

    public SegmentedPanel()
    {
        // segments are toggle buttons: their Checked event bubbles up here, also when a binding checks one
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => InvalidateArrange()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => InvalidateArrange()));
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Gap between the track edge and the thumb.</summary>
    public double Inset
    {
        get => (double)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    public bool IsUniform
    {
        get => (bool)GetValue(IsUniformProperty);
        set => SetValue(IsUniformProperty, value);
    }

    public Brush? ThumbBrush
    {
        get => (Brush?)GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    public Brush? ThumbBorderBrush
    {
        get => (Brush?)GetValue(ThumbBorderBrushProperty);
        set => SetValue(ThumbBorderBrushProperty, value);
    }

    public Color ThumbShadowColor
    {
        get => (Color)GetValue(ThumbShadowColorProperty);
        set => SetValue(ThumbShadowColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double inset = Inset, widest = 0, total = 0, tallest = 0;
        int count = 0;
        var free = new Size(double.PositiveInfinity, Math.Max(0, availableSize.Height - 2 * inset));
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(free);
            widest = Math.Max(widest, child.DesiredSize.Width);
            tallest = Math.Max(tallest, child.DesiredSize.Height);
            total += child.DesiredSize.Width;
            count++;
        }
        double width = IsUniform ? widest * count : total;
        return new Size(width + 2 * inset, tallest + 2 * inset);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double inset = Inset;
        int count = 0;
        double natural = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            natural += child.DesiredSize.Width;
            count++;
        }
        double inner = Math.Max(0, finalSize.Width - 2 * inset);
        double uniform = count > 0 ? inner / count : 0;
        // non-uniform segments share any extra width in proportion to their natural width
        double stretch = natural > 0 ? inner / natural : 1;

        double x = inset, height = Math.Max(0, finalSize.Height - 2 * inset);
        Rect selected = Rect.Empty;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            double w = IsUniform ? uniform : child.DesiredSize.Width * stretch;
            var slot = new Rect(x, inset, w, height);
            child.Arrange(slot);
            if (child is ToggleButton { IsChecked: true }) selected = slot;
            x += w;
        }
        MoveThumb(selected);
        return finalSize;
    }

    private void MoveThumb(Rect target)
    {
        if (target == _target) return;
        _target = target;
        double left = target.IsEmpty ? 0 : target.X, width = target.IsEmpty ? 0 : target.Width;

        // the first placement, and any change while the page is off screen, snaps: only a visible change slides
        if (!_placed || target.IsEmpty || !IsLoaded)
        {
            BeginAnimation(ThumbLeftProperty, null);
            BeginAnimation(ThumbWidthProperty, null);
            SetValue(ThumbLeftProperty, left);
            SetValue(ThumbWidthProperty, width);
            _placed = !target.IsEmpty;
            return;
        }

        // from wherever it is right now (possibly mid-slide) to the new segment
        BeginAnimation(ThumbLeftProperty, new DoubleAnimation(left, SlideDuration) { EasingFunction = SpringEase.Default });
        BeginAnimation(ThumbWidthProperty, new DoubleAnimation(width, SlideDuration) { EasingFunction = SpringEase.Default });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double radius = Math.Min(CornerRadius, ActualHeight / 2);
        if (Background != null)
            dc.DrawRoundedRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);

        double width = (double)GetValue(ThumbWidthProperty);
        if (width <= 0 || ThumbBrush == null) return;
        double inset = Inset;
        // concentric with the track: the thumb's radius is the track's minus the gap between them
        double inner = Math.Max(0, radius - inset);
        var thumb = new Rect((double)GetValue(ThumbLeftProperty), inset, width, Math.Max(0, ActualHeight - 2 * inset));
        var shadow = ThumbShadowColor;
        if (shadow.A > 0) SoftShadow.Draw(dc, new Rect(thumb.X, thumb.Y + 1.5, thumb.Width, thumb.Height), inner, 6, shadow);
        var pen = ThumbBorderBrush != null ? new Pen(ThumbBorderBrush, 0.5) : null;
        dc.DrawRoundedRectangle(ThumbBrush, pen, thumb, inner, inner);
    }
}
