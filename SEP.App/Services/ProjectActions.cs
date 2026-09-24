using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

/// <summary>
/// The actions a project card offers, shared by the Overview, Projects and My Projects pages so every card
/// behaves the same: launch, favourite, edit details, open folder, users, decommission.
/// </summary>
public partial class ProjectActions : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly IE3dLauncherService _launcher;
    private readonly IE3dProjectService _projectService;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchAllMineCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchGroupCommand))]
    private bool _isLaunching;

    /// <summary>Raised when a page should open (e.g. the Users page for a project).</summary>
    public event Action<Type, object?>? NavigateRequested;

    public ProjectActions(IProjectCatalog catalog, IE3dLauncherService launcher, IE3dProjectService projectService, IDialogService dialogs, ToastService toasts)
    {
        _catalog = catalog;
        _launcher = launcher;
        _projectService = projectService;
        _dialogs = dialogs;
        _toasts = toasts;
    }

    public bool CanLaunch() => !IsLaunching;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    public async Task LaunchAsync(ProjectItem? item)
    {
        if (item == null) return;
        IsLaunching = true;
        try
        {
            _toasts.Info(string.Format(Strings.Projects_SwitchingTo, item.Name));
            var res = await _launcher.SwitchAndLaunchAsync(item);
            _toasts.Result(res.Success, res.Message);
        }
        finally { IsLaunching = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    public async Task LaunchAllMineAsync()
    {
        IsLaunching = true;
        try
        {
            _toasts.Info(Strings.Launch_AllStarting);
            var res = await _launcher.LoadMyProjectsAndLaunchAsync();
            _toasts.Result(res.Success, res.Message);
        }
        finally { IsLaunching = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    public async Task LaunchGroupAsync(IEnumerable<ProjectItem>? projects)
    {
        if (projects == null) return;
        var list = projects.ToList();
        if (list.Count == 0) return;
        IsLaunching = true;
        try
        {
            _toasts.Info(Strings.Launch_AllStarting);
            var res = await _launcher.SwitchAndLaunchMultipleAsync(list);
            _toasts.Result(res.Success, res.Message);
        }
        finally { IsLaunching = false; }
    }

    [RelayCommand]
    public void ToggleFavorite(ProjectItem? item)
    {
        if (item == null) return;
        bool mine = _catalog.ToggleMyProject(item);
        _toasts.Success(string.Format(mine ? Strings.Project_AddedToMine : Strings.Project_RemovedFromMine, item.Title));
    }

    [RelayCommand]
    public Task EditAsync(ProjectItem? item) => item == null ? Task.CompletedTask : _dialogs.EditProjectAsync(item);

    [RelayCommand]
    public void OpenFolder(ProjectItem? item)
    {
        if (item != null) _projectService.OpenFolder(item.ProjectDir);
    }

    [RelayCommand]
    public void ShowUsers(ProjectItem? item)
    {
        if (item != null) NavigateRequested?.Invoke(typeof(Views.Pages.UserAdminPage), item);
    }

    [RelayCommand]
    public Task DecommissionAsync(ProjectItem? item) => item == null ? Task.CompletedTask : _dialogs.DecommissionProjectAsync(item);

    [RelayCommand]
    public Task CreateProjectAsync() => _dialogs.CreateProjectAsync();

    [RelayCommand]
    public Task ManageCategoriesAsync() => _dialogs.ManageCategoriesAsync();

    public void Navigate(Type page) => NavigateRequested?.Invoke(page, null);

    /// <summary>Copies a project's bat path to the clipboard.</summary>
    [RelayCommand]
    public void CopyPath(ProjectItem? item)
    {
        if (item == null) return;
        try
        {
            System.Windows.Clipboard.SetText(item.BatPath);
            _toasts.Info(string.Format(Strings.Project_PathCopied, item.BatPath));
        }
        catch (Exception ex) { _toasts.Error(ex.Message); }
    }
}
