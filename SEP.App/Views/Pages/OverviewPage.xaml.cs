using System.Windows.Controls;
using System.Windows.Input;
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
        // six tiles in one row need about 1000 px; below that two even rows read better than one orphan tile
        SizeChanged += (_, e) => KpiGrid.Rows = e.NewSize.Width >= 1000 ? 1 : 2;
    }

    // KPI tiles are plain borders (no button chrome); a click opens the matching page.
    private void OnProjectsTile(object sender, MouseButtonEventArgs e) => _viewModel.GoProjectsCommand.Execute(null);
    private void OnMineTile(object sender, MouseButtonEventArgs e) => _viewModel.GoMyProjectsCommand.Execute(null);
    private void OnSettingsTile(object sender, MouseButtonEventArgs e) => _viewModel.GoSettingsCommand.Execute(null);
    private void OnToolsTile(object sender, MouseButtonEventArgs e) => _viewModel.GoToolsCommand.Execute(null);
}
