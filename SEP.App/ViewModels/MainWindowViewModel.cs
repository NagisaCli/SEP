using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>Title-bar state: what E3D is currently set up to load, and the launch actions.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly IE3dLauncherService _launcherService;
    private readonly SessionService _sessions;
    private readonly ToastService _toasts;

    [ObservableProperty]
    private string _activeProjectCode = Strings.Main_NoActiveProject;

    [ObservableProperty]
    private string _activeProjectStatus = Strings.Main_StatusPending;

    /// <summary>"single" / "all" / "library" / "" — drives the icon next to the active label.</summary>
    [ObservableProperty]
    private string _activeMode = string.Empty;

    [ObservableProperty]
    private bool _isE3dRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchActiveProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchAllMineCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchE3dOnlyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    public MainWindowViewModel(IProjectCatalog catalog, IE3dLauncherService launcherService, SessionService sessions, ToastService toasts)
    {
        _catalog = catalog;
        _launcherService = launcherService;
        _sessions = sessions;
        _toasts = toasts;

        RefreshActiveProject();
        _catalog.Changed += (_, _) => OnUiThread(RefreshActiveProject);

        // Singleton view-model: re-render the cached status texts when the UI language changes.
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            RefreshActiveProject();
            if (!IsBusy) StatusMessage = Strings.Common_Ready;
        };

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => IsE3dRunning = _sessions.IsE3dRunning();
        timer.Start();
        IsE3dRunning = _sessions.IsE3dRunning();
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
        string mode = _catalog.Data.Settings.LastMode ?? string.Empty;
        ActiveMode = mode;
        if (string.IsNullOrEmpty(last))
        {
            ActiveProjectCode = Strings.Main_NoActiveProjectSet;
            ActiveProjectStatus = Strings.Main_StatusPending;
            return;
        }
        ActiveProjectCode = mode == "all" ? Strings.Launch_AllName : last;
        ActiveProjectStatus = mode switch
        {
            "all" => string.Format(Strings.My_CountFormat, _catalog.MyProjects.Count),
            "library" => Strings.Main_ModeLibrary,
            _ => Strings.Common_Ready,
        };
    }

    private bool NotBusy() => !IsBusy;

    /// <summary>Title-bar button: re-applies the last environment and starts E3D.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LaunchActiveProjectAsync()
    {
        string last = _catalog.Data.Settings.LastLaunched;
        if (string.IsNullOrEmpty(last))
        {
            StatusMessage = Strings.Main_SelectProjectFirst;
            _toasts.Warning(Strings.Main_SelectProjectFirst);
            return;
        }

        IsBusy = true;
        try
        {
            string mode = _catalog.Data.Settings.LastMode ?? string.Empty;
            (bool Success, string Message) res;
            if (mode == "all")
            {
                res = await _launcherService.LoadMyProjectsAndLaunchAsync();
            }
            else
            {
                var proj = _catalog.ActiveProject;
                if (proj == null)
                {
                    // A whole library was loaded last time (possibly by the Python UI): the environment is already
                    // set up, so just start E3D.
                    var launch = await _launcherService.LaunchE3dProcessAsync();
                    res = (launch.Success, $"[{last}] {launch.Message}");
                }
                else
                {
                    StatusMessage = string.Format(Strings.Main_SwitchingAndLaunching, proj.Name);
                    res = await _launcherService.SwitchAndLaunchAsync(proj);
                }
            }
            StatusMessage = res.Message;
            _toasts.Result(res.Success, res.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LaunchAllMineAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = Strings.Launch_AllStarting;
            var res = await _launcherService.LoadMyProjectsAndLaunchAsync();
            StatusMessage = res.Message;
            _toasts.Result(res.Success, res.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LaunchE3dOnlyAsync()
    {
        IsBusy = true;
        try
        {
            var res = await _launcherService.LaunchE3dProcessAsync();
            StatusMessage = res.Message;
            _toasts.Result(res.Success, res.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
