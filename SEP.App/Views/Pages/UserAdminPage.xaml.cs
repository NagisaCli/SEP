using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class UserAdminPage : Page
{
    public UserAdminPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<UserAdminViewModel>();
    }
}
