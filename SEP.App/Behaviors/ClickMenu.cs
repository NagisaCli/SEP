using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SEP.App.Behaviors;

/// <summary>
/// Opens a button's ContextMenu on a normal left click (the "…" button of a card), placed under the button,
/// so the same menu serves both the right-click and the explicit menu button.
/// Usage: <c>beh:ClickMenu.IsEnabled="True"</c> on a ButtonBase that has a ContextMenu.
/// </summary>
public static class ClickMenu
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ClickMenu), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        if ((bool)e.NewValue) button.Click += OnClick;
        else button.Click -= OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not { } menu) return;
        menu.PlacementTarget = fe;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.Closed -= OnMenuClosed;
        menu.Closed += OnMenuClosed;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // One menu serves every card of a page: once closed it must not keep pointing at (and holding on to) the last
    // card, nor hand its under-the-button placement to the next right-click. Closed comes after the clicked item's
    // command has run, so the command still sees its project.
    private static void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Closed -= OnMenuClosed;
        menu.ClearValue(ContextMenu.PlacementTargetProperty);
        menu.ClearValue(ContextMenu.PlacementProperty);
        menu.ClearValue(ContextMenu.HorizontalOffsetProperty);
    }
}
