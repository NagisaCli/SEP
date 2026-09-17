using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;
using Wpf.Ui.Controls;

namespace SEP.App.Views.Dialogs;

public partial class CreateProjectDialog : FluentWindow
{
    private readonly CreateProjectDialogViewModel _viewModel;

    public CreateProjectDialog()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<CreateProjectDialogViewModel>();
        DataContext = _viewModel;
        Loaded += (_, _) => CodeField.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        try
        {
            await _viewModel.CreateAsync();
            if (_viewModel.IsSuccess)
            {
                DialogResult = true;
                Close();
            }
        }
        finally { CreateButton.IsEnabled = true; }
    }
}
