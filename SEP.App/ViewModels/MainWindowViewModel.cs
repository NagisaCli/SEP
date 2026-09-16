using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly IE3dLauncherService _launcherService;

    [ObservableProperty]
    private string _activeProjectCode = Strings.Main_NoActiveProject;

    [ObservableProperty]
    private string _activeProjectStatus = Strings.Main_StatusPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchActiveProjectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    public MainWindowViewModel(IProjectCatalog catalog, IE3dLauncherService launcherService)
    {
        _catalog = catalog;
        _launcherService = launcherService;

        RefreshActiveProject();
        _catalog.Changed += (_, _) => OnUiThread(RefreshActiveProject);

        // Singleton view-model: re-render the cached status texts when the UI language changes.
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            RefreshActiveProject();
            if (!IsBusy) StatusMessage = Strings.Common_Ready;
        };
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void RefreshActiveProject()
    {
        string last = _catalog.Data.Settings.LastLaunched;
        if (!string.IsNullOrEmpty(last))
        {
            ActiveProjectCode = last;
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
        string last = _catalog.Data.Settings.LastLaunched;
        if (string.IsNullOrEmpty(last))
        {
            StatusMessage = Strings.Main_SelectProjectFirst;
            return;
        }

        IsBusy = true;
        try
        {
            var proj = _catalog.ActiveProject;
            if (proj == null)
            {
                // Not a single project we know (a whole library or "all my projects" was loaded last time, possibly by
                // the Python UI): the E3D environment is already set up, so just start E3D.
                var launch = await _launcherService.LaunchE3dProcessAsync();
                StatusMessage = $"[{last}] {launch.Message}";
                return;
            }

            StatusMessage = string.Format(Strings.Main_SwitchingAndLaunching, proj.Name);
            var res = await _launcherService.SwitchAndLaunchAsync(proj);
            StatusMessage = res.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
