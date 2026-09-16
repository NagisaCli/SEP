using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IE3dProjectService _projectService;
    private readonly IE3dLauncherService _launcherService;

    [ObservableProperty]
    private string _activeProjectCode = Strings.Main_NoActiveProject;

    [ObservableProperty]
    private string _activeProjectStatus = Strings.Main_StatusPending;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchActiveProjectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    public MainWindowViewModel(IE3dProjectService projectService, IE3dLauncherService launcherService)
    {
        _projectService = projectService;
        _launcherService = launcherService;

        RefreshActiveProject();

        // Singleton view-model: re-render the cached status texts when the UI language changes.
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            RefreshActiveProject();
            if (!IsBusy) StatusMessage = Strings.Common_Ready;
        };
    }

    public void RefreshActiveProject()
    {
        string? active = _projectService.ProjectsConfig.LastActiveProject;
        if (!string.IsNullOrEmpty(active))
        {
            ActiveProjectCode = active;
            ActiveProjectStatus = Strings.Common_Ready;
        }
        else
        {
            ActiveProjectCode = Strings.Main_NoActiveProjectSet;
            ActiveProjectStatus = Strings.Main_StatusPending;
        }
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LaunchActiveProjectAsync()
    {
        if (string.IsNullOrEmpty(_projectService.ProjectsConfig.LastActiveProject))
        {
            StatusMessage = Strings.Main_SelectProjectFirst;
            return;
        }

        IsBusy = true;
        try
        {
            string code = _projectService.ProjectsConfig.LastActiveProject;
            var projects = await _projectService.LoadAllProjectsAsync();   // cached snapshot when fresh
            var proj = projects.Find(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

            if (proj == null)
            {
                StatusMessage = string.Format(Strings.Main_ActiveProjectPathMissing, code);
                return;
            }

            StatusMessage = string.Format(Strings.Main_SwitchingAndLaunching, code);
            var res = await _launcherService.SwitchAndLaunchAsync(proj);
            StatusMessage = res.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
