using System.ComponentModel;
using System.Windows;

namespace SEP.App.Behaviors;

/// <summary>
/// Keeps an element's corner radius at half its height (<c>beh:Capsule.IsEnabled="True"</c>), so it is a true capsule.
/// WPF does not clamp a large CornerRadius such as 999 to half the height: it scales the horizontal and vertical radii
/// separately to fit, which turns a wide button or badge into an ellipse. Works on any element with a CornerRadius
/// property (Border, WPF-UI buttons); the value is set again only when the height changes.
/// </summary>
public static class Capsule
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(Capsule), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= OnSizeChanged;
        if ((bool)e.NewValue)
        {
            element.SizeChanged += OnSizeChanged;
            Update(element);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged) Update((FrameworkElement)sender);
    }

    private static void Update(FrameworkElement element)
    {
        if (element.ActualHeight <= 0) return;
        var property = DependencyPropertyDescriptor.FromName("CornerRadius", element.GetType(), element.GetType())?.DependencyProperty;
        if (property?.PropertyType == typeof(CornerRadius))
            element.SetCurrentValue(property, new CornerRadius(element.ActualHeight / 2));
    }
}
