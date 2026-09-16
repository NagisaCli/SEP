using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SEP.App.Behaviors;

/// <summary>
/// Attached behaviour for ScrollViewers (or any control that contains one, e.g. a ListView):
///  - mouse wheel scrolls by pixels with an eased animation instead of WPF's jumpy line steps;
///  - the middle mouse button autoscrolls like a browser: hold and drag for speed proportional to
///    the distance from the anchor, or click once to lock autoscroll until the next click / Esc.
/// Usage: <c>behaviors:SmoothScroll.IsEnabled="True"</c>.
/// </summary>
public static class SmoothScroll
{
    private const double WheelStepPixels = 110;                 // per wheel notch
    private static readonly Duration WheelDuration = new(TimeSpan.FromMilliseconds(240));
    private const double AutoScrollDeadZone = 10;               // px around the anchor with no movement
    private const double AutoScrollMaxPixelsPerSecond = 2400;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    // Animated by BeginAnimation; each change is pushed into the ScrollViewer (VerticalOffset itself is read-only).
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => (d as ScrollViewer)?.ScrollToVerticalOffset((double)e.NewValue)));

    // Where the current wheel animation is heading, so consecutive notches accumulate.
    private static readonly DependencyProperty TargetOffsetProperty = DependencyProperty.RegisterAttached(
        "TargetOffset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false));

    private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "Session", typeof(AutoScrollSession), typeof(SmoothScroll), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;
        if (fe.IsLoaded) Attach(fe);
        else fe.Loaded += (_, _) => Attach(fe);
    }

    private static void Attach(FrameworkElement host)
    {
        var sv = host as ScrollViewer ?? FindDescendant<ScrollViewer>(host);
        if (sv == null || (bool)sv.GetValue(AttachedProperty)) return;
        sv.SetValue(AttachedProperty, true);

        sv.PreviewMouseWheel += OnPreviewMouseWheel;
        sv.PreviewMouseDown += OnPreviewMouseDown;
        sv.PreviewMouseUp += OnPreviewMouseUp;
        sv.PreviewMouseMove += OnPreviewMouseMove;
        sv.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && EndAutoScroll((ScrollViewer)s!)) e.Handled = true; };
        sv.LostMouseCapture += (s, _) => EndAutoScroll((ScrollViewer)s!);
        // Dragging the scrollbar or programmatic scrolls must not be undone by a stale animation target.
        sv.ScrollChanged += (s, _) =>
        {
            var v = (ScrollViewer)s!;
            if (!(bool)v.GetValue(AnimatingProperty)) v.SetValue(TargetOffsetProperty, double.NaN);
        };
    }

    private static readonly DependencyProperty AnimatingProperty = DependencyProperty.RegisterAttached(
        "Animating", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false));

    // ── wheel ───────────────────────────────────────────────────────────────────────

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0 || e.Delta == 0) return;                 // nothing to scroll here: let a parent take it

        // A nested scrollable list under the pointer that can still move in this direction wins.
        var inner = FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject);
        if (inner != null && !ReferenceEquals(inner, sv) && inner.ScrollableHeight > 0 &&
            ((e.Delta > 0 && inner.VerticalOffset > 0) || (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5)))
            return;

        EndAutoScroll(sv);
        double target = (double)sv.GetValue(TargetOffsetProperty);
        if (double.IsNaN(target)) target = sv.VerticalOffset;
        target = Math.Clamp(target - e.Delta / 120.0 * WheelStepPixels, 0, sv.ScrollableHeight);
        e.Handled = true;

        sv.SetValue(TargetOffsetProperty, target);
        sv.SetValue(AnimatingProperty, true);
        var anim = new DoubleAnimation(sv.VerticalOffset, target, WheelDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => sv.SetValue(AnimatingProperty, false);
        sv.BeginAnimation(AnimatedOffsetProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    // ── middle-button autoscroll ─────────────────────────────────────────────────────

    private sealed class AutoScrollSession
    {
        public required Point Anchor;
        public Point Current;
        public readonly Stopwatch Held = Stopwatch.StartNew();
        public long LastTick = Stopwatch.GetTimestamp();
        public bool Locked;                 // click-and-release: stays active until the next click
        public AutoScrollAdorner? Adorner;
        public EventHandler? Rendering;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        bool active = sv.GetValue(SessionProperty) != null;
        if (active)
        {
            EndAutoScroll(sv);                // any click stops a locked autoscroll
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle || sv.ScrollableHeight <= 0) return;

        // The marker needs an adorner layer; the window's decorator or the viewer's own content presenter provides one.
        var layer = AdornerLayer.GetAdornerLayer(sv);
        var presenter = FindDescendant<ScrollContentPresenter>(sv);
        if (layer == null && presenter != null) layer = presenter.AdornerLayer;

        var session = new AutoScrollSession
        {
            Anchor = e.GetPosition(sv),
            Current = e.GetPosition(sv),
            Adorner = layer == null ? null : new AutoScrollAdorner((UIElement?)presenter ?? sv, e.GetPosition((IInputElement?)presenter ?? sv)),
        };
        if (layer != null && session.Adorner != null) layer.Add(session.Adorner);
        sv.SetValue(SessionProperty, session);
        sv.BeginAnimation(AnimatedOffsetProperty, null);
        sv.SetValue(TargetOffsetProperty, double.NaN);
        sv.Cursor = Cursors.ScrollNS;
        sv.CaptureMouse();
        session.Rendering = (_, _) => Tick(sv, session);
        CompositionTarget.Rendering += session.Rendering;
        e.Handled = true;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.GetValue(SessionProperty) is not AutoScrollSession session) return;
        session.Current = e.GetPosition(sv);
        double dy = session.Current.Y - session.Anchor.Y;
        sv.Cursor = dy < -AutoScrollDeadZone ? Cursors.ScrollN : dy > AutoScrollDeadZone ? Cursors.ScrollS : Cursors.ScrollNS;
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.GetValue(SessionProperty) is not AutoScrollSession session || e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        bool quickClick = session.Held.ElapsedMilliseconds < 300 &&
                          (session.Current - session.Anchor).Length < 6;
        if (quickClick && !session.Locked) session.Locked = true;   // browser-style: click once, scroll by moving the mouse
        else EndAutoScroll(sv);
    }

    private static void Tick(ScrollViewer sv, AutoScrollSession session)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - session.LastTick) / (double)Stopwatch.Frequency;
        session.LastTick = now;
        if (dt <= 0 || dt > 0.25) return;

        double dy = session.Current.Y - session.Anchor.Y;
        if (Math.Abs(dy) <= AutoScrollDeadZone) return;
        // Speed grows a little faster than linearly with the distance, capped for sanity.
        double magnitude = Math.Min(AutoScrollMaxPixelsPerSecond, Math.Pow(Math.Abs(dy) - AutoScrollDeadZone, 1.25) * 1.6);
        double next = Math.Clamp(sv.VerticalOffset + Math.Sign(dy) * magnitude * dt, 0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(next);
    }

    private static bool EndAutoScroll(ScrollViewer sv)
    {
        if (sv.GetValue(SessionProperty) is not AutoScrollSession session) return false;
        sv.SetValue(SessionProperty, null);
        if (session.Rendering != null) CompositionTarget.Rendering -= session.Rendering;
        if (session.Adorner != null) AdornerLayer.GetAdornerLayer(session.Adorner.AdornedElement)?.Remove(session.Adorner);
        sv.Cursor = null;
        if (sv.IsMouseCaptured) sv.ReleaseMouseCapture();
        return true;
    }

    /// <summary>The anchor marker: a ring with up/down arrows at the point where autoscroll started.</summary>
    private sealed class AutoScrollAdorner : Adorner
    {
        private readonly Point _center;
        private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1B, 0x22)));
        private static readonly Pen Ring = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), 1.5));
        private static readonly Brush Arrow = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));

        public AutoScrollAdorner(UIElement adorned, Point center) : base(adorned)
        {
            _center = center;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawEllipse(Fill, Ring, _center, 15, 15);
            dc.DrawGeometry(Arrow, null, Triangle(_center.X, _center.Y - 6, up: true));
            dc.DrawGeometry(Arrow, null, Triangle(_center.X, _center.Y + 6, up: false));
            dc.DrawEllipse(Arrow, null, _center, 1.5, 1.5);
        }

        private static Geometry Triangle(double x, double y, bool up)
        {
            double h = up ? -4 : 4;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, y + h), true, true);
                c.LineTo(new Point(x - 4, y - h * 0.4), false, false);
                c.LineTo(new Point(x + 4, y - h * 0.4), false, false);
            }
            g.Freeze();
            return g;
        }

        private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
    }

    // ── tree helpers ────────────────────────────────────────────────────────────────

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T hit) return hit;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
