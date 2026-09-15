using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Models;
using SEP.App.Services;

namespace SEP.App.Views.Dialogs;

public partial class DecommissionDialog : Window
{
    private readonly ProjectItem _project;
    private readonly IE3dProjectService _projectService;

    public DecommissionDialog(ProjectItem project)
    {
        InitializeComponent();
        _project = project;
        _projectService = App.Services.GetRequiredService<IE3dProjectService>();

        TxtCode.Text = project.Code;
        TxtName.Text = project.DisplayTitle;
        TxtPath.Text = project.Path;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnConfirmDecommissionClick(object sender, RoutedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;

        bool doArchive = ChkArchive.IsChecked == true;
        bool doDelete = ChkDelete.IsChecked == true;
        string archiveDir = TxtArchiveDir.Text.Trim();

        var (ok, msg) = await _projectService.DecommissionProjectAsync(_project.Path, archiveDir, doArchive, doDelete);

        if (!ok)
        {
            ErrorText.Text = msg;
            ErrorBorder.Visibility = Visibility.Visible;
        }
        else
        {
            DialogResult = true;
            Close();
        }
    }
}
