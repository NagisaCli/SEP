using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEP.App.Controls;

/// <summary>
/// A card surface: a <see cref="Border"/> that draws a soft drop shadow under itself in the light theme. The shadow is
/// painted with gradient brushes in <see cref="OnRender"/> (no effect, so ClearType stays on and repeated cards stay
/// cheap); <see cref="ShadowColor"/> is transparent in the dark theme, where depth comes from lighter surfaces.
/// Being a Border, it keeps everything a Border does (the drag-drop highlight sets its BorderBrush).
/// Default look: Themes/Generic.xaml.
/// </summary>
public class Card : Border
{
    public static readonly DependencyProperty ShadowColorProperty =
        DependencyProperty.Register(nameof(ShadowColor), typeof(Color), typeof(Card),
            new FrameworkPropertyMetadata(Colors.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShadowBlurProperty =
        DependencyProperty.Register(nameof(ShadowBlur), typeof(double), typeof(Card),
            new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShadowOffsetProperty =
        DependencyProperty.Register(nameof(ShadowOffset), typeof(double), typeof(Card),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static Card()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));
    }

    /// <summary>Colour (with alpha) of the soft shadow; transparent draws none.</summary>
    public Color ShadowColor
    {
        get => (Color)GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    /// <summary>Width of the blurred band around the card.</summary>
    public double ShadowBlur
    {
        get => (double)GetValue(ShadowBlurProperty);
        set => SetValue(ShadowBlurProperty, value);
    }

    /// <summary>How far the shadow falls below the card (light comes from above).</summary>
    public double ShadowOffset
    {
        get => (double)GetValue(ShadowOffsetProperty);
        set => SetValue(ShadowOffsetProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var color = ShadowColor;
        if (color.A > 0 && ActualWidth > 0 && ActualHeight > 0)
        {
            double radius = CornerRadius.TopLeft;
            // a wide ambient shadow and a tight contact shadow, like a lit object resting on the page
            SoftShadow.Draw(dc, new Rect(0, ShadowOffset, ActualWidth, ActualHeight), radius, ShadowBlur, color);
            SoftShadow.Draw(dc, new Rect(0, 1, ActualWidth, ActualHeight), radius, 3, Color.FromArgb((byte)(color.A * 0.55), color.R, color.G, color.B));
        }
        base.OnRender(dc);
    }
}

/// <summary>
/// Paints a soft rounded-rectangle shadow from nine gradient pieces (four radial corners, four linear edges, a solid
/// centre), the same technique as the system menu shadow: cheap, effect-free and crisp-edged. Brushes are frozen and
/// shared per (colour, radius, blur); every piece is drawn in a local frame (rotated in 90° steps), so one corner
/// brush and one edge brush serve all four sides of every card.
/// </summary>
internal static class SoftShadow
{
    // falloff across the blur band: 0 = where the solid core ends, 1 = fully transparent (a Gaussian-like ramp)
    private static readonly (double At, double Alpha)[] Ramp = { (0, 1), (0.18, 0.8), (0.38, 0.5), (0.58, 0.24), (0.78, 0.07), (1, 0) };
    private static readonly Dictionary<(Color, double, double), (Brush Corner, Brush Edge, Brush Solid)> Cache = new();
    private static readonly RotateTransform[] Turns = CreateTurns();

    private static RotateTransform[] CreateTurns()
    {
        var turns = new RotateTransform[4];
        for (int i = 0; i < 4; i++)
        {
            turns[i] = new RotateTransform(90 * i);
            turns[i].Freeze();
        }
        return turns;
    }

    public static void Draw(DrawingContext dc, Rect shape, double cornerRadius, double blur, Color color)
    {
        if (color.A == 0 || shape.Width <= 0 || shape.Height <= 0) return;
        double radius = Math.Round(Math.Max(0, Math.Min(cornerRadius, Math.Min(shape.Width, shape.Height) / 2)));
        double half = Math.Max(1, Math.Round(blur / 2));
        double inner = Math.Max(0, radius - half);   // where the band starts, measured from a corner centre
        double outer = radius + half;                 // where it has faded out
        var (corner, edge, solid) = Brushes(color, inner, outer);

        // corner centres, snapped so the nine pieces meet on whole pixels (no seams at 100 % scaling)
        double left = Math.Round(shape.Left + radius), top = Math.Round(shape.Top + radius);
        double right = Math.Round(shape.Right - radius), bottom = Math.Round(shape.Bottom - radius);
        double width = Math.Max(0, right - left), height = Math.Max(0, bottom - top);

        if (width > 0 && height > 0) dc.DrawRectangle(solid, null, new Rect(left, top, width, height));

        // each side: an edge strip along the side and the corner that follows it, in a frame turned 90° per side
        Piece(dc, left, top, 0, width, outer, corner, edge);
        Piece(dc, right, top, 1, height, outer, corner, edge);
        Piece(dc, right, bottom, 2, width, outer, corner, edge);
        Piece(dc, left, bottom, 3, height, outer, corner, edge);
    }

    // In the local frame the band lies above the x axis (y in [-outer, 0]); the strip runs along +x for `length`,
    // and the corner occupies the quadrant x in [-outer, 0].
    private static void Piece(DrawingContext dc, double x, double y, int turn, double length, double outer, Brush corner, Brush edge)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(Turns[turn]);
        if (length > 0) dc.DrawRectangle(edge, null, new Rect(0, -outer, length, outer));
        dc.DrawRectangle(corner, null, new Rect(-outer, -outer, outer, outer));
        dc.Pop();
        dc.Pop();
    }

    private static (Brush Corner, Brush Edge, Brush Solid) Brushes(Color color, double inner, double outer)
    {
        var key = (color, inner, outer);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (Cache.Count > 64) Cache.Clear();

            var stops = new GradientStopCollection();
            double start = outer > 0 ? inner / outer : 0;
            foreach (var (at, alpha) in Ramp)
                stops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(color.A * alpha), color.R, color.G, color.B), start + (1 - start) * at));
            stops.Freeze();

            var corner = new RadialGradientBrush(stops)
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(0, 0),
                GradientOrigin = new Point(0, 0),
                RadiusX = outer,
                RadiusY = outer,
            };
            corner.Freeze();
            var edge = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, -outer)) { MappingMode = BrushMappingMode.Absolute };
            edge.Freeze();
            var solid = new SolidColorBrush(color);
            solid.Freeze();

            var result = ((Brush)corner, (Brush)edge, (Brush)solid);
            Cache[key] = result;
            return result;
        }
    }
}
