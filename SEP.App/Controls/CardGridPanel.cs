using System;
using System.Windows;
using System.Windows.Controls;

namespace SEP.App.Controls;

/// <summary>
/// Responsive card grid: as many columns as fit at <see cref="MinItemWidth"/>, every column the same width, so a row
/// of cards always spans the space instead of leaving a ragged gap on the right (a WrapPanel with fixed-width cards
/// did). Each child gets its column width (its own Margin is the gap); a row is as tall as its tallest card.
/// </summary>
public class CardGridPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(CardGridPanel),
            new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(CardGridPanel),
            new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Narrowest a column may get (child margin included) before a column is dropped.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    private int Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        return Math.Clamp((int)Math.Floor(width / Math.Max(1, MinItemWidth)), 1, Math.Max(1, MaxColumns));
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinItemWidth : available.Width;
        int cols = Columns(width);
        double colWidth = Math.Floor(width / cols);
        double total = 0, rowHeight = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++i % cols == 0) { total += rowHeight; rowHeight = 0; }
        }
        total += rowHeight;
        return new Size(double.IsInfinity(available.Width) ? colWidth * Math.Min(cols, InternalChildren.Count) : width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double colWidth = Math.Floor(final.Width / cols);
        var children = InternalChildren;
        double y = 0;
        for (int start = 0; start < children.Count; start += cols)
        {
            int end = Math.Min(start + cols, children.Count);
            double rowHeight = 0;
            for (int i = start; i < end; i++) rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            for (int i = start; i < end; i++) children[i].Arrange(new Rect((i - start) * colWidth, y, colWidth, rowHeight));
            y += rowHeight;
        }
        return final;
    }
}
