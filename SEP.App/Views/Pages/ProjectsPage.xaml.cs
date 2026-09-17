using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class ProjectsPage : Page
{
    private readonly ProjectsViewModel _viewModel;

    public ProjectsPage()
    {
        App.Log("ProjectsPage Constructor enter");
        InitializeComponent();
        App.Log("ProjectsPage InitializeComponent finished");

        _viewModel = App.Services.GetRequiredService<ProjectsViewModel>();
        DataContext = _viewModel;
        App.Log("ProjectsPage DataContext set");
    }

    private void OnFilterAllClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "All";
    private void OnFilterMineClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "Mine";
    private void OnFilterLocalClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "Local";
    private void OnFilterUncClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "Unc";
}
