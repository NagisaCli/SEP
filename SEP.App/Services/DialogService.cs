using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Views.Dialogs;
using Wpf.Ui.Controls;

namespace SEP.App.Services;

/// <summary>WPF implementation of <see cref="IDialogService"/>: WPF-UI message boxes and the small prompt windows.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                    ?? Application.Current?.MainWindow;

    public async Task<bool> ConfirmAsync(string title, string message, string okLabel, bool danger = false)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = okLabel,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = Strings.Common_Cancel,
            Owner = Owner,
        };
        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            CloseButtonText = Strings.Common_Ok,
            Owner = Owner,
        };
        await box.ShowDialogAsync();
    }

    public Task<PromptResult?> PromptAsync(PromptRequest request)
    {
        var dlg = new PromptDialog(request) { Owner = Owner };
        bool ok = dlg.ShowDialog() == true;
        return Task.FromResult(ok ? dlg.Result : null);
    }

    public Task<UserDraft?> NewUserAsync(IReadOnlyList<string> teams, string projectCode)
    {
        var dlg = new NewUserDialog(teams, projectCode) { Owner = Owner };
        bool ok = dlg.ShowDialog() == true;
        return Task.FromResult(ok ? dlg.Result : null);
    }

    private static IProjectCatalog Catalog => (IProjectCatalog)App.Services.GetService(typeof(IProjectCatalog))!;

    public Task<bool> EditProjectAsync(ProjectItem project)
    {
        var dlg = new ProjectEditDialog(Catalog, new[] { project }) { Owner = Owner };
        dlg.ShowDialog();
        return Task.FromResult(dlg.Saved);
    }

    public Task<bool> EditProjectsAsync(IReadOnlyList<ProjectItem> projects)
    {
        if (projects.Count == 0) return Task.FromResult(false);
        var dlg = new ProjectEditDialog(Catalog, projects) { Owner = Owner };
        dlg.ShowDialog();
        return Task.FromResult(dlg.Saved);
    }

    public Task ManageCategoriesAsync()
    {
        new CategoriesDialog(Catalog) { Owner = Owner }.ShowDialog();
        return Task.CompletedTask;
    }

    public Task CreateProjectAsync()
    {
        new CreateProjectDialog { Owner = Owner }.ShowDialog();   // the catalog rescans the library itself after a successful creation
        return Task.CompletedTask;
    }

    public Task DecommissionProjectAsync(ProjectItem project)
    {
        new DecommissionDialog(project) { Owner = Owner }.ShowDialog();
        return Task.CompletedTask;
    }

    public string? PickFolder(string title, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public string? PickFile(string title, string filter, string? initial = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string suggestedName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = suggestedName, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }
}
