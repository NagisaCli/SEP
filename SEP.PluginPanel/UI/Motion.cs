using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SEP.PluginPanel.UI
{
    /// <summary>
    /// Entrance motion for cards and for parts that appear (card body, "More", forms): a short fade and slide.
    /// Run from code with FillBehavior.Stop, so when it ends the element is simply at its own values. Storyboards in
    /// styles hold their end value instead, and a theme switch (which re-applies the styles) left cards transparent.
    /// </summary>
    public static class Motion
    {
        /// <summary>Animate in when the element is loaded (cards).</summary>
        public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
            "Enter", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterChanged));

        public static bool GetEnter(DependencyObject d) => (bool)d.GetValue(EnterProperty);
        public static void SetEnter(DependencyObject d, bool value) => d.SetValue(EnterProperty, value);

        /// <summary>Animate in each time the element becomes visible (expanding parts).</summary>
        public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
            "Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealChanged));

        public static bool GetReveal(DependencyObject d) => (bool)d.GetValue(RevealProperty);
        public static void SetReveal(DependencyObject d, bool value) => d.SetValue(RevealProperty, value);

        private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe && (bool)e.NewValue) fe.Loaded += (s, _) => Play((FrameworkElement)s, 8, 240);
        }

        private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe && (bool)e.NewValue)
                fe.IsVisibleChanged += (s, args) => { if ((bool)args.NewValue) Play((FrameworkElement)s, -5, 200); };
        }

        private static void Play(FrameworkElement fe, double fromY, int ms)
        {
            if (!(fe.RenderTransform is TranslateTransform slide) || slide.IsFrozen)
            {
                slide = new TranslateTransform();
                fe.RenderTransform = slide;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms * 0.8)) { FillBehavior = FillBehavior.Stop });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
    }
}
