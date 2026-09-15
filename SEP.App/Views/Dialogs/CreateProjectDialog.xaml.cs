using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Dialogs;

public partial class CreateProjectDialog : Window
{
    private readonly CreateProjectDialogViewModel _viewModel;

    public CreateProjectDialog()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<CreateProjectDialogViewModel>();
        DataContext = _viewModel;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.CreateAsync();
        if (_viewModel.IsSuccess)
        {
            DialogResult = true;
            Close();
        }
    }
}
