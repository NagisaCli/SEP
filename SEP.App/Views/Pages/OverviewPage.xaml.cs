using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class OverviewPage : Page
{
    private readonly OverviewViewModel _viewModel;

    public OverviewPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<OverviewViewModel>();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Refresh();
        // five tiles in one row need about 1000 px of page; below that two even rows read better than one squeezed row
        SizeChanged += (_, e) => KpiGrid.Rows = e.NewSize.Width >= 1000 ? 1 : 2;
    }

    // KPI tiles are buttons (SepTileButton): Click already means press and release on the same tile, so a press on
    // a card or the scroll bar released over a tile does not navigate away, and the tiles work from the keyboard.
    private void OnProjectsTile(object sender, RoutedEventArgs e) => _viewModel.GoProjectsCommand.Execute(null);
    private void OnMineTile(object sender, RoutedEventArgs e) => _viewModel.GoMyProjectsCommand.Execute(null);
    private void OnSettingsTile(object sender, RoutedEventArgs e) => _viewModel.GoSettingsCommand.Execute(null);
    private void OnToolsTile(object sender, RoutedEventArgs e) => _viewModel.GoToolsCommand.Execute(null);
}
