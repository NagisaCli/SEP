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
    private readonly ProjectUsageService _usage;

    /// <summary>True while a manual "who is in this project" check runs; its buttons are disabled meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUsageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllUsageCommand))]
    private bool _isCheckingUsage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchAllMineCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchGroupCommand))]
    private bool _isLaunching;

    /// <summary>Raised when a page should open (e.g. the Users page for a project).</summary>
    public event Action<Type, object?>? NavigateRequested;

    public ProjectActions(IProjectCatalog catalog, IE3dLauncherService launcher, IE3dProjectService projectService, IDialogService dialogs, ToastService toasts,
        ProjectUsageService usage)
    {
        _usage = usage;
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

    // ── manual usage checks (never automatic: each one touches the file server) ────

    public bool CanCheckUsage() => !IsCheckingUsage;

    /// <summary>Card menu: who is in this one project.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUsage))]
    public async Task CheckUsageAsync(ProjectItem? item)
    {
        if (item == null) return;
        await CheckUsageForAsync(new[] { item });
        if (item.Usage == UsageState.InUse) _toasts.Info(string.Format(Strings.Usage_OneInUse, item.Title, item.OnlineSummary));
        else if (item.Usage == UsageState.Free) _toasts.Success(string.Format(Strings.Usage_OneFree, item.Title));
        else _toasts.Warning(string.Format(Strings.Usage_OneUnknown, item.Title));
    }

    /// <summary>Projects page header: every project of every library.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUsage))]
    public Task CheckAllUsageAsync() => CheckUsageForAsync(_catalog.Projects, report: true);

    /// <summary>Checks a set of projects (a library, a group, my projects) and reports the totals.</summary>
    public async Task CheckUsageForAsync(IEnumerable<ProjectItem> projects, bool report = false)
    {
        var list = projects.ToList();
        if (list.Count == 0 || IsCheckingUsage) return;
        IsCheckingUsage = true;
        try
        {
            if (report) _toasts.Info(string.Format(Strings.Usage_Checking, list.Count));
            var s = await _usage.CheckAsync(list);
            if (report) _toasts.Result(true, string.Format(Strings.Usage_Summary, s.Checked, s.InUse, s.Free, s.Unreachable));
        }
        catch (Exception ex)
        {
            _toasts.Error(ex.Message);
        }
        finally { IsCheckingUsage = false; }
    }

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
