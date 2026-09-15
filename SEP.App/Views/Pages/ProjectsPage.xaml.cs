using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Models;
using SEP.App.ViewModels;
using SEP.App.Views.Dialogs;

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
    private void OnFilterFavoritesClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "Favorites";
    private void OnFilterLocalClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "Local";
    private void OnFilterUncClick(object sender, RoutedEventArgs e) => _viewModel.FilterTab = "Unc";

    private void OnCreateProjectClick(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateProjectDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            _viewModel.LoadProjectsCommand.Execute(null);
        }
    }

    private void OnProjectMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ProjectItem item)
        {
            var dlg = new DecommissionDialog(item) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                _viewModel.LoadProjectsCommand.Execute(null);
            }
        }
    }
}
