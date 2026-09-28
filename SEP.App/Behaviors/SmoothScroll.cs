using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SEP.App.Behaviors;

/// <summary>
/// Attached behavior for ScrollViewers:
///  - High-performance, physics-based, frame-rate independent smooth mouse wheel scrolling (vertical & horizontal with Shift).
///  - Native browser-style middle mouse button autoscroll (click-to-lock or hold-to-drag).
/// Usage: <c>behaviors:SmoothScroll.IsEnabled="True"</c>.
/// </summary>
public static class SmoothScroll
{
    private const double WheelStepPixels = 120.0;
    private const double SmoothingConstant = 24.0;
    private const double AutoScrollDeadZone = 10.0;
    private const double AutoScrollMaxPixelsPerSecond = 2400.0;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false));

    private static readonly DependencyProperty WheelSessionProperty = DependencyProperty.RegisterAttached(
        "WheelSession", typeof(WheelScrollSession), typeof(SmoothScroll), new PropertyMetadata(null));

    private static readonly DependencyProperty AutoScrollSessionProperty = DependencyProperty.RegisterAttached(
        "AutoScrollSession", typeof(AutoScrollSession), typeof(SmoothScroll), new PropertyMetadata(null));

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

        sv.ScrollChanged += (s, e) =>
        {
            var v = (ScrollViewer)s!;
            if (v.GetValue(WheelSessionProperty) is WheelScrollSession sess)
            {
                if (!sess.IsRunning)
                {
                    sess.CurrentVerticalOffset = v.VerticalOffset;
                    sess.TargetVerticalOffset = v.VerticalOffset;
                    sess.CurrentHorizontalOffset = v.HorizontalOffset;
                    sess.TargetHorizontalOffset = v.HorizontalOffset;
                }
                else
                {
                    // Detect manual drag of scrollbar thumb or keyboard page jumps
                    if (e.VerticalChange != 0 && Math.Abs(v.VerticalOffset - sess.CurrentVerticalOffset) > 4.0)
                    {
                        sess.CurrentVerticalOffset = v.VerticalOffset;
                        sess.TargetVerticalOffset = v.VerticalOffset;
                    }
                    if (e.HorizontalChange != 0 && Math.Abs(v.HorizontalOffset - sess.CurrentHorizontalOffset) > 4.0)
                    {
                        sess.CurrentHorizontalOffset = v.HorizontalOffset;
                        sess.TargetHorizontalOffset = v.HorizontalOffset;
                    }
                }
            }
        };
    }

    // ── wheel smooth scrolling ────────────────────────────────────────────────────────

    private sealed class WheelScrollSession
    {
        public readonly ScrollViewer ScrollViewer;
        public double TargetVerticalOffset;
        public double CurrentVerticalOffset;
        public double TargetHorizontalOffset;
        public double CurrentHorizontalOffset;
        public long LastTimestamp;
        public bool IsRunning;

        public WheelScrollSession(ScrollViewer sv)
        {
            ScrollViewer = sv;
            TargetVerticalOffset = sv.VerticalOffset;
            CurrentVerticalOffset = sv.VerticalOffset;
            TargetHorizontalOffset = sv.HorizontalOffset;
            CurrentHorizontalOffset = sv.HorizontalOffset;
        }

        public void Stop()
        {
            if (IsRunning)
            {
                CompositionTarget.Rendering -= OnRendering;
                IsRunning = false;
            }
        }

        public void Start()
        {
            if (!IsRunning)
            {
                LastTimestamp = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRendering;
                IsRunning = true;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - LastTimestamp) / (double)Stopwatch.Frequency;
            LastTimestamp = now;

            if (dt <= 0 || dt > 0.1) dt = 0.016;

            double factor = 1.0 - Math.Exp(-SmoothingConstant * dt);

            bool verticalFinished = true;
            if (ScrollViewer.ScrollableHeight > 0)
            {
                double dy = TargetVerticalOffset - CurrentVerticalOffset;
                if (Math.Abs(dy) < 0.4)
                {
                    CurrentVerticalOffset = TargetVerticalOffset;
                }
                else
                {
                    CurrentVerticalOffset += dy * factor;
                    verticalFinished = false;
                }
                ScrollViewer.ScrollToVerticalOffset(CurrentVerticalOffset);
            }
            else
            {
                CurrentVerticalOffset = 0;
                TargetVerticalOffset = 0;
            }

            bool horizontalFinished = true;
            if (ScrollViewer.ScrollableWidth > 0)
            {
                double dx = TargetHorizontalOffset - CurrentHorizontalOffset;
                if (Math.Abs(dx) < 0.4)
                {
                    CurrentHorizontalOffset = TargetHorizontalOffset;
                }
                else
                {
                    CurrentHorizontalOffset += dx * factor;
                    horizontalFinished = false;
                }
                ScrollViewer.ScrollToHorizontalOffset(CurrentHorizontalOffset);
            }
            else
            {
                CurrentHorizontalOffset = 0;
                TargetHorizontalOffset = 0;
            }

            if (verticalFinished && horizontalFinished)
            {
                Stop();
            }
        }
    }

    private static WheelScrollSession GetOrCreateWheelSession(ScrollViewer sv)
    {
        if (sv.GetValue(WheelSessionProperty) is not WheelScrollSession session)
        {
            session = new WheelScrollSession(sv);
            sv.SetValue(WheelSessionProperty, session);
        }
        return session;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (e.Delta == 0) return;

        bool shiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        bool canScrollVertical = sv.ScrollableHeight > 0 && !shiftHeld;
        bool canScrollHorizontal = sv.ScrollableWidth > 0 && shiftHeld;

        if (!canScrollVertical && !canScrollHorizontal) return;

        var inner = FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject);
        if (inner != null && !ReferenceEquals(inner, sv))
        {
            if (canScrollVertical && inner.ScrollableHeight > 0 &&
                ((e.Delta > 0 && inner.VerticalOffset > 0) || (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5)))
                return;
            if (canScrollHorizontal && inner.ScrollableWidth > 0 &&
                ((e.Delta > 0 && inner.HorizontalOffset > 0) || (e.Delta < 0 && inner.HorizontalOffset < inner.ScrollableWidth - 0.5)))
                return;
        }

        EndAutoScroll(sv);

        var session = GetOrCreateWheelSession(sv);

        if (canScrollHorizontal)
        {
            if (!session.IsRunning) session.CurrentHorizontalOffset = sv.HorizontalOffset;
            double step = e.Delta / 120.0 * WheelStepPixels;
            session.TargetHorizontalOffset = Math.Clamp(session.TargetHorizontalOffset - step, 0, sv.ScrollableWidth);
            session.Start();
        }
        else
        {
            if (!session.IsRunning) session.CurrentVerticalOffset = sv.VerticalOffset;
            double step = e.Delta / 120.0 * WheelStepPixels;
            session.TargetVerticalOffset = Math.Clamp(session.TargetVerticalOffset - step, 0, sv.ScrollableHeight);
            session.Start();
        }

        e.Handled = true;
    }

    // ── middle-button autoscroll ─────────────────────────────────────────────────────

    private sealed class AutoScrollSession
    {
        public required Point Anchor;
        public Point Current;
        public readonly Stopwatch Held = Stopwatch.StartNew();
        public long LastTick = Stopwatch.GetTimestamp();
        public bool Locked;
        public AutoScrollAdorner? Adorner;
        public EventHandler? Rendering;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        bool active = sv.GetValue(AutoScrollSessionProperty) != null;
        if (active)
        {
            EndAutoScroll(sv);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle || sv.ScrollableHeight <= 0) return;

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
        sv.SetValue(AutoScrollSessionProperty, session);

        // Cancel any active wheel smoothing
        if (sv.GetValue(WheelSessionProperty) is WheelScrollSession wheel)
        {
            wheel.Stop();
        }

        sv.Cursor = Cursors.ScrollNS;
        sv.CaptureMouse();
        session.Rendering = (_, _) => Tick(sv, session);
        CompositionTarget.Rendering += session.Rendering;
        e.Handled = true;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.GetValue(AutoScrollSessionProperty) is not AutoScrollSession session) return;
        session.Current = e.GetPosition(sv);
        double dy = session.Current.Y - session.Anchor.Y;
        sv.Cursor = dy < -AutoScrollDeadZone ? Cursors.ScrollN : dy > AutoScrollDeadZone ? Cursors.ScrollS : Cursors.ScrollNS;
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.GetValue(AutoScrollSessionProperty) is not AutoScrollSession session || e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        bool quickClick = session.Held.ElapsedMilliseconds < 300 &&
                          (session.Current - session.Anchor).Length < 6;
        if (quickClick && !session.Locked) session.Locked = true;
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

        double magnitude = Math.Min(AutoScrollMaxPixelsPerSecond, Math.Pow(Math.Abs(dy) - AutoScrollDeadZone, 1.25) * 1.6);
        double next = Math.Clamp(sv.VerticalOffset + Math.Sign(dy) * magnitude * dt, 0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(next);
    }

    private static bool EndAutoScroll(ScrollViewer sv)
    {
        if (sv.GetValue(AutoScrollSessionProperty) is not AutoScrollSession session) return false;
        sv.SetValue(AutoScrollSessionProperty, null);
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
