using System;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace SEP.App.Controls;

/// <summary>
/// One entry of the shell's sidebar: icon, label and an optional count, checked while its page is shown. The shell
/// (MainWindow) navigates to <see cref="PageType"/> on click and checks the item that matches the page on screen.
/// Template: Themes/Generic.xaml.
/// </summary>
public class NavItem : RadioButton
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(NavItem), new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty CountProperty =
        DependencyProperty.Register(nameof(Count), typeof(int), typeof(NavItem), new PropertyMetadata(0));

    public static readonly DependencyProperty PageTypeProperty =
        DependencyProperty.Register(nameof(PageType), typeof(Type), typeof(NavItem), new PropertyMetadata(null));

    static NavItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NavItem), new FrameworkPropertyMetadata(typeof(NavItem)));
    }

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>Small figure at the right edge (projects in the workbench, starred projects); hidden while 0.</summary>
    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public Type? PageType
    {
        get => (Type?)GetValue(PageTypeProperty);
        set => SetValue(PageTypeProperty, value);
    }

    // Checking is the shell's job (it follows the page actually shown); a click only asks for the navigation.
    protected override void OnToggle() { }
}
