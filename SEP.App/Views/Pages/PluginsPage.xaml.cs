using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class PluginsPage : Page
{
    private readonly PluginsViewModel _viewModel;

    public PluginsPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<PluginsViewModel>();
        DataContext = _viewModel;
    }

    /// <summary>
    /// Header and folder card: the buttons stay beside the text while the text keeps a readable width, otherwise
    /// they move to a row of their own under it (a squeezed title broke into one word per line).
    /// </summary>
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Arrange(HeaderGrid, HeaderTools, textColumn: 0, minText: 340);
        Arrange(DirGrid, DirTools, textColumn: 1, minText: 380);
    }

    private static void Arrange(Grid grid, FrameworkElement tools, int textColumn, double minText)
    {
        if (grid.ActualWidth <= 0) return;
        tools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fixedWidth = 0;
        for (int i = 0; i < textColumn; i++)
            foreach (UIElement child in grid.Children)
                if (Grid.GetColumn(child) == i && Grid.GetRow(child) == 0 && child is FrameworkElement fe) fixedWidth = Math.Max(fixedWidth, fe.ActualWidth + fe.Margin.Left + fe.Margin.Right);
        bool stack = grid.ActualWidth - fixedWidth - tools.DesiredSize.Width < minText;
        int row = stack ? 1 : 0, column = stack ? textColumn : grid.ColumnDefinitions.Count - 1;
        if (Grid.GetRow(tools) == row && Grid.GetColumn(tools) == column) return;
        Grid.SetRow(tools, row);
        Grid.SetColumn(tools, column);
        Grid.SetColumnSpan(tools, stack ? grid.ColumnDefinitions.Count - textColumn : 1);
        tools.HorizontalAlignment = stack ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        tools.Margin = stack ? new Thickness(textColumn == 0 ? 0 : 12, 10, 0, 0) : new Thickness(0);
    }

    private void OnListTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "list";
    private void OnConflictsTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "conflicts";
    private void OnChainTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "chain";
    private void OnMacroTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "macro";
    private void OnDiscoveryTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "discovery";
}
