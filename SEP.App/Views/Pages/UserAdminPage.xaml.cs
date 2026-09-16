using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class UserAdminPage : Page
{
    private readonly UserAdminViewModel _viewModel;

    public UserAdminPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<UserAdminViewModel>();
        DataContext = _viewModel;
    }

    private void OnTeamsTabClick(object sender, RoutedEventArgs e) => _viewModel.IsUsersTab = false;
}
