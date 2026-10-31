using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SEP.App.Models;
using SEP.App.Services;
using SEP.App.ViewModels;

namespace SEP.App.Behaviors;

public sealed record ProjectDragPayload(ProjectItem Project, string? SourceGroupId);

/// <summary>
/// Drag-and-drop interactions for project rows and cards:
///  - Reorder projects within groups or move between groups in "My Projects".
///  - Drag from Projects page into My Projects navigation item or groups.
///  - Visual insertion line indicator and floating drag preview adorner.
/// </summary>
public static class ProjectDragDrop
{
    public const string DragFormat = "SEP_ProjectDrag";

    private static Point _dragStartPoint;
    /// <summary>The draggable element the left button went down on, or null when the press began on a control
    /// (button, check box, text box, scroll bar) or outside any draggable element.</summary>
    private static FrameworkElement? _pressSource;
    private static bool _isDragging;
    private static DragGhostAdorner? _activeGhostAdorner;
    private static DropInsertionAdorner? _activeInsertionAdorner;

    #region Attached Properties

    // ── Draggable Item ───────────────────────────────────────────────────────────

    public static readonly DependencyProperty IsDraggableProperty = DependencyProperty.RegisterAttached(
        "IsDraggable", typeof(bool), typeof(ProjectDragDrop), new PropertyMetadata(false, OnIsDraggableChanged));

    public static bool GetIsDraggable(DependencyObject d) => (bool)d.GetValue(IsDraggableProperty);
    public static void SetIsDraggable(DependencyObject d, bool value) => d.SetValue(IsDraggableProperty, value);

    public static readonly DependencyProperty GroupIdProperty = DependencyProperty.RegisterAttached(
        "GroupId", typeof(string), typeof(ProjectDragDrop), new PropertyMetadata(null));

    public static string? GetGroupId(DependencyObject d) => (string?)d.GetValue(GroupIdProperty);
    public static void SetGroupId(DependencyObject d, string? value) => d.SetValue(GroupIdProperty, value);

    // ── Drop Target Item (reorder before/after) ──────────────────────────────────

    public static readonly DependencyProperty IsDropTargetItemProperty = DependencyProperty.RegisterAttached(
        "IsDropTargetItem", typeof(bool), typeof(ProjectDragDrop), new PropertyMetadata(false, OnIsDropTargetItemChanged));

    public static bool GetIsDropTargetItem(DependencyObject d) => (bool)d.GetValue(IsDropTargetItemProperty);
    public static void SetIsDropTargetItem(DependencyObject d, bool value) => d.SetValue(IsDropTargetItemProperty, value);

    // ── Drop Target Group (drop onto group header or container to move into group) ──

    public static readonly DependencyProperty IsDropTargetGroupProperty = DependencyProperty.RegisterAttached(
        "IsDropTargetGroup", typeof(bool), typeof(ProjectDragDrop), new PropertyMetadata(false, OnIsDropTargetGroupChanged));

    public static bool GetIsDropTargetGroup(DependencyObject d) => (bool)d.GetValue(IsDropTargetGroupProperty);
    public static void SetIsDropTargetGroup(DependencyObject d, bool value) => d.SetValue(IsDropTargetGroupProperty, value);

    #endregion

    #region Drag Source Handling

    private static void OnIsDraggableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            fe.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            fe.PreviewMouseMove += OnPreviewMouseMove;
        }
        else
        {
            fe.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            fe.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            fe.PreviewMouseMove -= OnPreviewMouseMove;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A press on a control inside the card (launch, folder, "…", star, batch check box) belongs to that
        // control: it must never become a drag. The old code only skipped recording the start point, so the
        // next mouse move compared against a stale point (often 0,0), started DoDragDrop, and the drag's mouse
        // capture swallowed the button's Click — card buttons "did nothing" whenever the hand moved a pixel.
        _pressSource = null;
        if (sender is not FrameworkElement fe || IsOnControl(e.OriginalSource as DependencyObject, fe)) return;
        _pressSource = fe;
        _dragStartPoint = e.GetPosition(null);
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _pressSource = null;

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressSource = null;
            return;
        }
        // only the element the press began on drags, and only when the press began on it (not on a button,
        // and not outside it with the button held down while moving in)
        if (_isDragging || !ReferenceEquals(sender, _pressSource)) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not ProjectItem project) return;

        Point current = e.GetPosition(null);
        Vector diff = _dragStartPoint - current;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _pressSource = null;
            StartDrag(fe, project);
        }
    }

    /// <summary>True when <paramref name="node"/> lies in an interactive control inside <paramref name="root"/>.</summary>
    private static bool IsOnControl(DependencyObject? node, FrameworkElement root)
    {
        while (node != null && !ReferenceEquals(node, root))
        {
            if (node is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Thumb or ScrollBar or Slider) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private static void StartDrag(FrameworkElement sourceElement, ProjectItem project)
    {
        _isDragging = true;
        string? groupId = GetGroupId(sourceElement);
        var payload = new ProjectDragPayload(project, groupId);

        var window = Window.GetWindow(sourceElement);
        AdornerLayer? layer = null;
        if (window?.Content is UIElement root)
        {
            layer = AdornerLayer.GetAdornerLayer(root);
            if (layer != null)
            {
                var ghost = CreateGhostElement(project);
                _activeGhostAdorner = new DragGhostAdorner(root, ghost);
                layer.Add(_activeGhostAdorner);
                _activeGhostAdorner.UpdatePosition(Mouse.GetPosition(root));
            }
        }

        var data = new DataObject();
        data.SetData(DragFormat, payload);
        data.SetData("SEP_ProjectItem", project);

        try
        {
            DragDrop.DoDragDrop(sourceElement, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            if (layer != null && _activeGhostAdorner != null)
            {
                layer.Remove(_activeGhostAdorner);
                _activeGhostAdorner = null;
            }
            RemoveActiveInsertionAdorner();
            _isDragging = false;
        }
    }

    #endregion

    #region Drop Target Handling: Items

    private static void OnIsDropTargetItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.AllowDrop = true;
            fe.DragOver += OnItemDragOver;
            fe.DragLeave += OnItemDragLeave;
            fe.Drop += OnItemDrop;
        }
        else
        {
            fe.DragOver -= OnItemDragOver;
            fe.DragLeave -= OnItemDragLeave;
            fe.Drop -= OnItemDrop;
        }
    }

    private static void OnItemDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        if (sender is not FrameworkElement fe || fe.DataContext is not ProjectItem targetProject) return;

        var payload = e.Data.GetData(DragFormat) as ProjectDragPayload;
        if (payload != null && ReferenceEquals(payload.Project, targetProject))
        {
            RemoveActiveInsertionAdorner();
            e.Handled = true;
            return;
        }

        Point pos = e.GetPosition(fe);
        bool insertAfter = pos.Y > (fe.ActualHeight / 2.0);

        var layer = AdornerLayer.GetAdornerLayer(fe);
        if (layer != null)
        {
            if (_activeInsertionAdorner == null || !ReferenceEquals(_activeInsertionAdorner.AdornedElement, fe))
            {
                RemoveActiveInsertionAdorner();
                _activeInsertionAdorner = new DropInsertionAdorner(fe) { InsertAfter = insertAfter };
                layer.Add(_activeInsertionAdorner);
            }
            else if (_activeInsertionAdorner.InsertAfter != insertAfter)
            {
                _activeInsertionAdorner.InsertAfter = insertAfter;
                _activeInsertionAdorner.InvalidateVisual();
            }
        }

        UpdateGhostPosition(fe, e);
        e.Handled = true;
    }

    private static void OnItemDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && _activeInsertionAdorner != null &&
            ReferenceEquals(_activeInsertionAdorner.AdornedElement, fe))
        {
            RemoveActiveInsertionAdorner();
        }
    }

    private static void OnItemDrop(object sender, DragEventArgs e)
    {
        RemoveActiveInsertionAdorner();

        if (sender is not FrameworkElement fe || fe.DataContext is not ProjectItem targetProject) return;
        if (e.Data.GetData(DragFormat) is not ProjectDragPayload payload) return;

        if (ReferenceEquals(payload.Project, targetProject))
        {
            e.Handled = true;
            return;
        }

        Point pos = e.GetPosition(fe);
        bool insertAfter = pos.Y > (fe.ActualHeight / 2.0);
        string? targetGroupId = targetProject.MyGroupId ?? GetGroupId(fe);

        var page = FindAncestor<Page>(fe);
        if (page?.DataContext is MyProjectsViewModel vm)
        {
            vm.ReorderProject(payload.Project, targetGroupId, targetProject, insertAfter);
        }
        else
        {
            var catalog = App.Services.GetService(typeof(IProjectCatalog)) as IProjectCatalog;
            catalog?.ReorderMyProject(payload.Project, targetGroupId, targetProject, insertAfter);
        }

        e.Handled = true;
    }

    #endregion

    #region Drop Target Handling: Groups

    private static void OnIsDropTargetGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.AllowDrop = true;
            fe.DragOver += OnGroupDragOver;
            fe.DragLeave += OnGroupDragLeave;
            fe.Drop += OnGroupDrop;
        }
        else
        {
            fe.DragOver -= OnGroupDragOver;
            fe.DragLeave -= OnGroupDragLeave;
            fe.Drop -= OnGroupDrop;
        }
    }

    private static void OnGroupDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        if (sender is Border border)
        {
            border.BorderBrush = Application.Current.TryFindResource("SepAccentBrush") as Brush;
        }
        UpdateGhostPosition(sender as UIElement, e);
        e.Handled = true;
    }

    private static void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }
    }

    private static void OnGroupDrop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }

        if (sender is not FrameworkElement fe) return;
        if (e.Data.GetData(DragFormat) is not ProjectDragPayload payload) return;

        string? targetGroupId = GetGroupId(fe);
        if (fe.DataContext is MyProjectGroupViewModel grpVm)
        {
            targetGroupId = grpVm.Id;
        }

        var page = FindAncestor<Page>(fe);
        if (page?.DataContext is MyProjectsViewModel vm)
        {
            vm.ReorderProject(payload.Project, targetGroupId, null, false);
        }
        else
        {
            var catalog = App.Services.GetService(typeof(IProjectCatalog)) as IProjectCatalog;
            catalog?.ReorderMyProject(payload.Project, targetGroupId, null, false);
        }

        e.Handled = true;
    }

    #endregion

    #region Helpers & Adorners

    private static void UpdateGhostPosition(UIElement? relativeTo, DragEventArgs e)
    {
        if (_activeGhostAdorner == null || relativeTo == null) return;
        var window = Window.GetWindow(relativeTo);
        if (window?.Content is UIElement root)
        {
            _activeGhostAdorner.UpdatePosition(e.GetPosition(root));
        }
    }

    private static void RemoveActiveInsertionAdorner()
    {
        if (_activeInsertionAdorner != null)
        {
            var layer = AdornerLayer.GetAdornerLayer(_activeInsertionAdorner.AdornedElement);
            layer?.Remove(_activeInsertionAdorner);
            _activeInsertionAdorner = null;
        }
    }

    // Theme brush for the drag visuals; the fallback only matters if a key were ever missing.
    private static Brush ThemeBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>The floating card under the pointer: the project's monogram and names in the current theme.</summary>
    private static FrameworkElement CreateGhostElement(ProjectItem project)
    {
        var border = new Border
        {
            Background = ThemeBrush("SepSurfaceBrush", Color.FromRgb(0x16, 0x1A, 0x22)),
            BorderBrush = ThemeBrush("SepAccentBrush", Color.FromRgb(0x2D, 0xD4, 0xFF)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 14, 6),
            Opacity = 0.96,
            // one element, only while dragging: the single place a shadow is allowed outside dialogs
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.35,
                Color = Colors.Black
            }
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var avatar = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = SEP.App.Converters.PaletteConverter.ForName(project.Badge),
            Margin = new Thickness(0, 0, 10, 0)
        };
        var avatarText = new TextBlock
        {
            Text = project.Badge,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            FontFamily = Application.Current?.TryFindResource("SepMonoFont") as FontFamily ?? new FontFamily("Consolas"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        avatar.Child = avatarText;
        sp.Children.Add(avatar);

        var textSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textSp.Children.Add(new TextBlock
        {
            Text = project.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ThemeBrush("SepTextBrush", Colors.White)
        });
        if (!string.IsNullOrEmpty(project.LibraryName))
        {
            textSp.Children.Add(new TextBlock
            {
                Text = project.LibraryName,
                FontSize = 11,
                Foreground = ThemeBrush("SepText3Brush", Color.FromRgb(0x9C, 0xA3, 0xAF)),
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        sp.Children.Add(textSp);

        border.Child = sp;
        return border;
    }

    private sealed class DropInsertionAdorner : Adorner
    {
        // the theme's accent, picked when the adorner is created (a drag never outlives a theme switch)
        private readonly Brush _brush;
        private readonly Pen _pen;

        public bool InsertAfter { get; set; }

        public DropInsertionAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
            _brush = ThemeBrush("SepAccentBrush", Color.FromRgb(0x2D, 0xD4, 0xFF));
            // not frozen: freezing the pen would freeze the shared theme brush with it
            _pen = new Pen(_brush, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        }

        protected override void OnRender(DrawingContext dc)
        {
            double y = InsertAfter ? AdornedElement.RenderSize.Height : 0;
            double w = AdornedElement.RenderSize.Width;
            dc.DrawLine(_pen, new Point(8, y), new Point(w - 8, y));
            dc.DrawEllipse(_brush, null, new Point(8, y), 3.5, 3.5);
            dc.DrawEllipse(_brush, null, new Point(w - 8, y), 3.5, 3.5);
        }
    }

    private sealed class DragGhostAdorner : Adorner
    {
        private readonly UIElement _child;
        private Point _offset;

        public DragGhostAdorner(UIElement adornedElement, UIElement child) : base(adornedElement)
        {
            _child = child;
            AddVisualChild(_child);
            IsHitTestVisible = false;
        }

        public void UpdatePosition(Point p)
        {
            _offset = p;
            InvalidateArrange();
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(constraint);
            return _child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(_offset.X + 16, _offset.Y + 16, _child.DesiredSize.Width, _child.DesiredSize.Height));
            return finalSize;
        }
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

    #endregion
}
