using System.Windows;
using System.Windows.Media;

namespace SEP.App.Behaviors;

/// <summary>
/// Clips an element to a rounded rectangle of its own size (<c>beh:RoundedClip.Radius="14"</c>). A Border's
/// CornerRadius only rounds its own background; content and decorations inside it (the canvas glows, the hero's
/// grid) would still show in the corners. One geometry per element, rebuilt only when the size changes.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0)
        {
            element.SizeChanged += OnSizeChanged;
            Update(element);
        }
        else
        {
            element.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        double r = GetRadius(element);
        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), r, r);
        clip.Freeze();
        element.Clip = clip;
    }
}
