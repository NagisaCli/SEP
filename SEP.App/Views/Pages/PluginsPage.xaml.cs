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

    private void OnListTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "list";
    private void OnConflictsTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "conflicts";
    private void OnChainTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "chain";
    private void OnMacroTab(object sender, RoutedEventArgs e) => _viewModel.Panel = "macro";
}
