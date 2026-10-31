using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SEP.App.Behaviors;

/// <summary>
/// Lets a card's entrance animation play only while its list fills for the first time. A cleared filter, a scan
/// or a re-sort re-inserts cards into a list that is already on screen; replaying the fade-in on each of those
/// made every key stroke in the search box ripple across the page.
/// Usage: <c>beh:CardEntrance.IsEnabled="True"</c> on the card root, and a trigger on
/// <c>beh:CardEntrance.IsEntering</c> whose EnterActions begin the storyboard.
/// </summary>
public static class CardEntrance
{
    private enum FillState { Empty, Filling, Settled }

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CardEntrance), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>Turns true once, when the card loads as part of its list's first fill.</summary>
    public static readonly DependencyProperty IsEnteringProperty = DependencyProperty.RegisterAttached(
        "IsEntering", typeof(bool), typeof(CardEntrance), new PropertyMetadata(false));

    public static bool GetIsEntering(DependencyObject d) => (bool)d.GetValue(IsEnteringProperty);
    public static void SetIsEntering(DependencyObject d, bool value) => d.SetValue(IsEnteringProperty, value);

    // kept on the ItemsControl that hosts the cards
    private static readonly DependencyProperty FillStateProperty = DependencyProperty.RegisterAttached(
        "FillState", typeof(FillState), typeof(CardEntrance), new PropertyMetadata(FillState.Empty));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnLoaded;
        if ((bool)e.NewValue) fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var card = (FrameworkElement)sender;
        // a card enters once: coming back to a cached page must not replay it either
        card.Loaded -= OnLoaded;

        var list = FindHost(card);
        var state = list == null ? FillState.Empty : (FillState)list.GetValue(FillStateProperty);
        if (state == FillState.Settled) return;

        card.SetValue(IsEnteringProperty, true);
        if (list == null || state == FillState.Filling) return;

        // Every card of the first fill is generated in one layout pass and gets Loaded in the same batch, before
        // this callback runs; whatever loads later is a re-insert.
        list.SetValue(FillStateProperty, FillState.Filling);
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => list.SetValue(FillStateProperty, FillState.Settled)));
    }

    private static ItemsControl? FindHost(DependencyObject card)
    {
        for (var node = VisualTreeHelper.GetParent(card); node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ItemsControl list) return list;
        return null;
    }
}
