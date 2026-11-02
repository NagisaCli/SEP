using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEP.App.Controls;

/// <summary>
/// Vertical stack for inset grouped lists (the Settings app): a hairline between consecutive visible rows, inset from
/// the leading edge by <see cref="SeparatorInset"/> (usually where the row text starts, past its icon). The rows stay
/// borderless and the first one never gets a line, whatever is added, removed or collapsed. Works as an ItemsPanel too.
/// </summary>
public class SeparatedStackPanel : StackPanel
{
    public static readonly DependencyProperty SeparatorBrushProperty =
        DependencyProperty.Register(nameof(SeparatorBrush), typeof(Brush), typeof(SeparatedStackPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeparatorInsetProperty =
        DependencyProperty.Register(nameof(SeparatorInset), typeof(double), typeof(SeparatedStackPanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeparatorTrailingInsetProperty =
        DependencyProperty.Register(nameof(SeparatorTrailingInset), typeof(double), typeof(SeparatedStackPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<double> _lines = new();

    public Brush? SeparatorBrush
    {
        get => (Brush?)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <summary>Gap between the leading edge and the start of each separator.</summary>
    public double SeparatorInset
    {
        get => (double)GetValue(SeparatorInsetProperty);
        set => SetValue(SeparatorInsetProperty, value);
    }

    public double SeparatorTrailingInset
    {
        get => (double)GetValue(SeparatorTrailingInsetProperty);
        set => SetValue(SeparatorTrailingInsetProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);

        // Where the lines go: the top of every visible row after the first. Rows can move without the panel changing
        // size (one collapses while another appears), so compare with the last render instead of relying on resize.
        // (A vertical StackPanel gives every child its desired height, one after another.)
        var lines = new List<double>();
        bool first = true;
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child == null || child.Visibility == Visibility.Collapsed) continue;
            if (!first) lines.Add(y);
            y += child.DesiredSize.Height;
            first = false;
        }
        if (!Same(lines, _lines))
        {
            _lines.Clear();
            _lines.AddRange(lines);
            InvalidateVisual();
        }
        return size;
    }

    private static bool Same(List<double> a, List<double> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i] - b[i]) > 0.1) return false;
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var brush = SeparatorBrush;
        if (brush == null || _lines.Count == 0) return;

        // one device pixel, snapped to the pixel grid so the hairline stays sharp at any scaling
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double thickness = 1 / scale;
        double x = SeparatorInset;
        double width = ActualWidth - x - SeparatorTrailingInset;
        if (width <= 0) return;
        foreach (double top in _lines)
        {
            double y = Math.Round(top * scale) / scale - thickness;
            dc.DrawRectangle(brush, null, new Rect(x, y, width, thickness));
        }
    }
}
