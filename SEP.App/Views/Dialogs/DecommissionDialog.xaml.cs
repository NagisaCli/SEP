using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Converters;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;
using Wpf.Ui.Controls;

namespace SEP.App.Views.Dialogs;

public partial class DecommissionDialog : FluentWindow
{
    private readonly ProjectItem _project;
    private readonly IE3dProjectService _projectService;
    private readonly IDialogService _dialogs;

    public DecommissionDialog(ProjectItem project)
    {
        InitializeComponent();
        _project = project;
        _projectService = App.Services.GetRequiredService<IE3dProjectService>();
        _dialogs = App.Services.GetRequiredService<IDialogService>();

        Avatar.Background = PaletteConverter.ForName(project.Badge);
        TxtCode.Text = project.Badge;
        TxtName.Text = project.Title;
        TxtPath.Text = project.ProjectDir;
        TxtPath.ToolTip = project.ProjectDir;

        // archive next to the library by default (…\Projects\Archive)
        string parent = Path.GetDirectoryName(project.ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? project.ProjectDir;
        TxtArchiveDir.Text = Path.Combine(parent, "Archive");
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        string? folder = _dialogs.PickFolder(Strings.Decommission_ArchiveDirLabel, TxtArchiveDir.Text);
        if (folder != null) TxtArchiveDir.Text = folder;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnConfirmDecommissionClick(object sender, RoutedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
        try
        {
            bool doArchive = ChkArchive.IsChecked == true;
            bool doDelete = ChkDelete.IsChecked == true;
            string archiveDir = TxtArchiveDir.Text.Trim();

            var (ok, msg) = await _projectService.DecommissionProjectAsync(_project, archiveDir, doArchive, doDelete);
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
        finally { ConfirmButton.IsEnabled = true; }
    }
}
