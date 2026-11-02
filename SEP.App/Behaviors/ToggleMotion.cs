using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SEP.App.Behaviors;

/// <summary>
/// Motion for the Apple-style switch template (SepStyles.xaml). The template's triggers put the knob and the "on" fill
/// in their final state at once; on a real toggle this plays the move from the old state into it:
/// <c>beh:ToggleMotion.Slide="16"</c> on the knob slides it in from 16 px away, <c>beh:ToggleMotion.Fade="True"</c> on
/// the "on" layer fades it. Because only actual Checked/Unchecked changes of a loaded switch animate, a page full of
/// switches appears in place: nothing slides when a list is filled or a cached page comes back.
/// </summary>
public static class ToggleMotion
{
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(280));
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(180));

    public static readonly DependencyProperty SlideProperty = DependencyProperty.RegisterAttached(
        "Slide", typeof(double), typeof(ToggleMotion), new PropertyMetadata(0.0, OnChanged));

    public static double GetSlide(DependencyObject d) => (double)d.GetValue(SlideProperty);
    public static void SetSlide(DependencyObject d, double value) => d.SetValue(SlideProperty, value);

    public static readonly DependencyProperty FadeProperty = DependencyProperty.RegisterAttached(
        "Fade", typeof(bool), typeof(ToggleMotion), new PropertyMetadata(false, OnChanged));

    public static bool GetFade(DependencyObject d) => (bool)d.GetValue(FadeProperty);
    public static void SetFade(DependencyObject d, bool value) => d.SetValue(FadeProperty, value);

    // the switch a template part listens to, and the part's handler, kept so Unloaded can let go of both
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner", typeof(ToggleButton), typeof(ToggleMotion), new PropertyMetadata(null));

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(RoutedEventHandler), typeof(ToggleMotion), new PropertyMetadata(null));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement part) return;
        part.Loaded -= OnLoaded;
        part.Unloaded -= OnUnloaded;
        part.Loaded += OnLoaded;
        part.Unloaded += OnUnloaded;
        if (part.IsLoaded) Attach(part);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var part = (FrameworkElement)sender;
        if (part.GetValue(OwnerProperty) is ToggleButton owner) Subscribe(owner, part, false);
        part.ClearValue(OwnerProperty);
    }

    private static void Attach(FrameworkElement part)
    {
        if (part.GetValue(OwnerProperty) != null || part.TemplatedParent is not ToggleButton owner) return;
        part.SetValue(OwnerProperty, owner);
        Subscribe(owner, part, true);
    }

    private static void Subscribe(ToggleButton owner, FrameworkElement part, bool on)
    {
        // one handler per part, created once and reused when the part is unloaded and loaded again
        var handler = part.GetValue(HandlerProperty) as RoutedEventHandler ?? CreateHandler(part);
        if (on)
        {
            owner.Checked += handler;
            owner.Unchecked += handler;
        }
        else
        {
            owner.Checked -= handler;
            owner.Unchecked -= handler;
        }
    }

    private static RoutedEventHandler CreateHandler(FrameworkElement part)
    {
        RoutedEventHandler handler = (sender, _) =>
        {
            if (sender is ToggleButton owner && owner.IsLoaded && part.IsLoaded) Play(part, owner.IsChecked == true);
        };
        part.SetValue(HandlerProperty, handler);
        return handler;
    }

    private static void Play(FrameworkElement part, bool isChecked)
    {
        double slide = GetSlide(part);
        if (slide != 0)
        {
            if (part.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
            {
                shift = new TranslateTransform();
                part.RenderTransform = shift;
            }
            // the trigger already moved the knob; start it where it was and let it travel (FillBehavior.Stop ends on the trigger's value)
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(isChecked ? -slide : slide, 0, SlideDuration) { EasingFunction = SpringEase.Default, FillBehavior = FillBehavior.Stop });
        }
        if (GetFade(part))
        {
            part.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(isChecked ? 0 : 1, isChecked ? 1 : 0, FadeDuration) { FillBehavior = FillBehavior.Stop });
        }
    }
}
