using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Models;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public sealed record LiveProjectChoice(string Code, string Label, ProjectItem? SepProject);

public sealed record LiveSupportProjectChoice(string Label, ProjectItem? Project);

/// <summary>Launch parameters only; all Live scope/runtime rules come from e3d_live.</summary>
public partial class LiveViewModel : ObservableObject
{
    private readonly LiveEntryAdapter _entry;
    private readonly IE3dAdminBridge _admin;
    private readonly IProjectCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private List<LiveEntryTarget> _targets = new();
    private List<LiveModuleProfile> _profiles = new();
    private bool _dynamicProjects;
    private int _mdbQueryGeneration;
    private readonly Dictionary<string, string> _mountSelections = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex ScopeIdentifier = new("^[A-Z0-9_.-]{1,64}$", RegexOptions.Compiled);

    public ObservableCollection<LiveProjectChoice> Projects { get; } = new();
    public ObservableCollection<LiveSupportProjectChoice> SupportProjects { get; } = new();
    public ObservableCollection<string> Mdbs { get; } = new();
    public ObservableCollection<string> Modules { get; } = new();
    public ObservableCollection<string> AccessModes { get; } = new();
    public ObservableCollection<LiveEntrySession> RunningSessions { get; } = new();

    [ObservableProperty] private string _liveRoot = string.Empty;
    [ObservableProperty] private string _pythonExecutable = "python";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _runtimeDescription = string.Empty;
    [ObservableProperty] private string _mdbLookupMessage = string.Empty;
    [ObservableProperty] private string _sessionSummary = string.Empty;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSessionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSessionCommand))]
    private bool _isBusy;
    [ObservableProperty] private LiveProjectChoice? _selectedProject;
    [ObservableProperty] private LiveSupportProjectChoice? _selectedSupportProject;
    [ObservableProperty] private string? _selectedMdb;
    [ObservableProperty] private string? _selectedModule;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartLiveCommand))]
    private string? _selectedAccessMode;

    public LiveViewModel(LiveEntryAdapter entry, IE3dAdminBridge admin, IProjectCatalog catalog,
        IDialogService dialogs, ToastService toasts)
    {
        _entry = entry;
        _admin = admin;
        _catalog = catalog;
        _dialogs = dialogs;
        _toasts = toasts;
        try
        {
            var settings = entry.LoadSettings();
            LiveRoot = settings.LiveRoot;
            PythonExecutable = settings.PythonExecutable;
            foreach (var profile in settings.MountProfiles)
            {
                if (profile.Value != null && profile.Value.Count > 0 && !string.IsNullOrWhiteSpace(profile.Value[0]))
                    _mountSelections[profile.Key] = profile.Value[0];
            }
            Loc.Instance.LanguageChanged += (_, _) =>
            {
                if (SupportProjects.Count > 0 && SupportProjects[0].Project == null)
                {
                    var previous = SelectedSupportProject;
                    var newNone = new LiveSupportProjectChoice(Loc.Instance["Live_SupportNone"], null);
                    SupportProjects[0] = newNone;
                    if (previous?.Project == null)
                        SelectedSupportProject = newNone;
                }
            };
            StatusMessage = Loc.Instance["Live_SelectScope"];
            if (!string.IsNullOrWhiteSpace(LiveRoot)) _ = RefreshCoreAsync(saveSettings: false);
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
    }

    private LiveEntrySettings CurrentSettings => new()
    {
        LiveRoot = LiveRoot,
        PythonExecutable = PythonExecutable,
        MountProfiles = _mountSelections.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key,
                pair => new List<string> { pair.Value }, StringComparer.OrdinalIgnoreCase),
    };

    private bool CanRefresh() => !IsBusy;
    private bool CanStop(LiveEntrySession? session) => !IsBusy && session != null &&
        session.TargetProcessId > 0 && !string.IsNullOrWhiteSpace(session.BridgeSessionId);
    private bool CanStart() => !IsBusy && SelectedTarget is { RuntimeReady: true }
        && (!_dynamicProjects || SelectedProject?.SepProject != null)
        && SelectedAccessMode != null;

    private LiveEntryTarget? SelectedTarget
    {
        get
        {
            if (SelectedProject == null || SelectedModule == null || SelectedMdb == null ||
                !ScopeIdentifier.IsMatch(SelectedMdb.Trim().ToUpperInvariant())) return null;
            if (!_dynamicProjects) return _targets.FirstOrDefault(t =>
                t.ProjectCode == SelectedProject.Code && t.Mdb == SelectedMdb &&
                t.Module == SelectedModule);
            var profile = _profiles.FirstOrDefault(p => p.Module == SelectedModule);
            return profile == null ? null : new LiveEntryTarget
            {
                ProjectCode = SelectedProject.Code,
                Mdb = SelectedMdb.Trim().ToUpperInvariant(),
                Module = profile.Module,
                AccessModes = profile.AccessModes,
                RuntimeReady = profile.RuntimeReady,
                RuntimeDirectory = profile.RuntimeDirectory,
            };
        }
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        string? folder = _dialogs.PickFolder(Loc.Instance["Live_BrowseTitle"], LiveRoot);
        if (folder != null) LiveRoot = folder;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RefreshCoreAsync(saveSettings: true);

    private async Task RefreshCoreAsync(bool saveSettings)
    {
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = Loc.Instance["Live_LoadingOptions"];
        try
        {
            var settings = CurrentSettings;
            if (saveSettings) _entry.SaveSettings(settings);
            var options = await _entry.GetOptionsAsync(settings);
            _targets = options.Targets;
            _profiles = options.ModuleProfiles;
            _dynamicProjects = options.ProjectPolicy == "any_valid_code";
            RebuildProjects();
            await LoadSessionsAsync();
            int skipped = Math.Max(0, _catalog.Projects.Count - Projects.Count);
            StatusMessage = _dynamicProjects
                ? string.Format(Loc.Instance["Live_ProfilesLoaded"], _profiles.Count, Projects.Count) +
                  (skipped > 0 ? " " + string.Format(Loc.Instance["Live_ProjectsWithoutCode"], skipped) : "")
                : _targets.Count == 0 ? Loc.Instance["Live_NoTargets"]
                : string.Format(Loc.Instance["Live_OptionsLoaded"], _targets.Count);
        }
        catch (Exception ex)
        {
            _targets.Clear();
            _profiles.Clear();
            Projects.Clear(); SupportProjects.Clear(); Mdbs.Clear(); Modules.Clear(); AccessModes.Clear();
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            StartLiveCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshSessionsAsync()
    {
        IsBusy = true;
        try
        {
            await LoadSessionsAsync();
            StatusIsError = false;
            StatusMessage = string.Format(Loc.Instance["Live_SessionsCount"], RunningSessions.Count);
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadSessionsAsync()
    {
        var inventory = await _entry.GetSessionsAsync(CurrentSettings);
        RunningSessions.Clear();
        foreach (var session in inventory.Sessions.OrderBy(s => s.Scope.ProjectCode)
                     .ThenBy(s => s.Scope.Module).ThenBy(s => s.TargetProcessId))
            RunningSessions.Add(session);
        SessionSummary = string.Format(Loc.Instance["Live_SessionsCount"], RunningSessions.Count);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopSessionAsync(LiveEntrySession? session)
    {
        if (session == null) return;
        if (!await _dialogs.ConfirmAsync(Loc.Instance["Live_StopTitle"],
            string.Format(Loc.Instance["Live_StopBody"], session.Scope.ProjectCode,
                session.Scope.Mdb, session.Scope.Module, session.TargetProcessId),
            Loc.Instance["Live_StopConfirm"], danger: true)) return;
        IsBusy = true;
        try
        {
            await _entry.StopAsync(CurrentSettings, session);
            await LoadSessionsAsync();
            StatusIsError = false;
            StatusMessage = Loc.Instance["Live_Stopped"];
            _toasts.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
            _toasts.Error(StatusMessage);
        }
        finally { IsBusy = false; }
    }

    private void RebuildProjects()
    {
        string? previous = SelectedProject?.SepProject?.Id ?? SelectedProject?.Code;
        Projects.Clear();
        var codes = _dynamicProjects
            ? _catalog.Projects.Select(p => (p.Code ?? string.Empty).Trim().ToUpperInvariant())
                .Where(c => ScopeIdentifier.IsMatch(c))
            : _targets.Select(t => t.ProjectCode);
        foreach (string code in codes.Distinct().OrderBy(s => s))
        {
            var known = _catalog.Projects.Where(p =>
                string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (known.Count == 0 && !_dynamicProjects)
                Projects.Add(new LiveProjectChoice(code, code, null));
            else foreach (var project in known)
                Projects.Add(new LiveProjectChoice(code,
                    $"{code} — {project.Title} ({project.LibraryName})", project));
        }
        // Never silently choose the first unrelated plant project on first open.
        SelectedProject = previous == null ? null
            : Projects.FirstOrDefault(p => (p.SepProject?.Id ?? p.Code) == previous);
        RebuildMdbs();
    }

    partial void OnSelectedProjectChanged(LiveProjectChoice? value)
    {
        ++_mdbQueryGeneration;
        RebuildSupportProjects();
        RebuildMdbs();
        if (value?.SepProject is { } project)
            _ = LoadMdbsAsync(project, _mdbQueryGeneration);
        else MdbLookupMessage = string.Empty;
    }

    private async Task LoadMdbsAsync(ProjectItem project, int generation)
    {
        MdbLookupMessage = Loc.Instance["Live_MdbLoading"];
        string? initialSelection = SelectedMdb;
        try
        {
            var names = await _admin.GetMdbsAsync(project);
            if (generation != _mdbQueryGeneration || SelectedProject?.SepProject?.Id != project.Id)
                return;
            if (names.Count == 0)
            {
                MdbLookupMessage = Loc.Instance["Live_MdbEmpty"];
                return;
            }
            string? previous = SelectedMdb;
            Mdbs.Clear();
            foreach (string name in names) Mdbs.Add(name);
            bool operatorChangedSelection = !string.Equals(previous, initialSelection,
                StringComparison.OrdinalIgnoreCase);
            SelectedMdb = previous != null && (Mdbs.Contains(previous) || operatorChangedSelection)
                ? previous : Mdbs.Contains("ALL") ? "ALL" : Mdbs[0];
            MdbLookupMessage = string.Format(Loc.Instance["Live_MdbLoaded"], names.Count);
        }
        catch (Exception ex)
        {
            if (generation != _mdbQueryGeneration || SelectedProject?.SepProject?.Id != project.Id)
                return;
            MdbLookupMessage = string.Format(Loc.Instance["Live_MdbUnavailable"], ex.Message);
        }
    }

    [RelayCommand]
    private void RefreshMdbs()
    {
        if (SelectedProject?.SepProject is { } project)
            _ = LoadMdbsAsync(project, ++_mdbQueryGeneration);
    }

    private void RebuildSupportProjects()
    {
        SupportProjects.Clear();
        var primary = SelectedProject?.SepProject;
        if (primary == null)
        {
            SelectedSupportProject = null;
            return;
        }

        string noneLabel = Loc.Instance["Live_SupportNone"];
        var noneChoice = new LiveSupportProjectChoice(noneLabel, null);
        SupportProjects.Add(noneChoice);

        _mountSelections.TryGetValue(primary.Id, out var rememberedId);

        LiveSupportProjectChoice? matchedChoice = null;
        foreach (var project in _catalog.Projects.Where(p =>
                     p.Id != primary.Id &&
                     !string.Equals(p.Code, primary.Code, StringComparison.OrdinalIgnoreCase) &&
                     ScopeIdentifier.IsMatch((p.Code ?? string.Empty).Trim().ToUpperInvariant()))
                     .OrderBy(p => p.Code).ThenBy(p => p.Title))
        {
            string label = $"{project.Code} — {project.Title} ({project.LibraryName})";
            var choice = new LiveSupportProjectChoice(label, project);
            SupportProjects.Add(choice);
            if (rememberedId != null && (string.Equals(project.Id, rememberedId, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(project.Code, rememberedId, StringComparison.OrdinalIgnoreCase)))
            {
                matchedChoice = choice;
            }
        }

        SelectedSupportProject = matchedChoice ?? noneChoice;
    }

    partial void OnSelectedSupportProjectChanged(LiveSupportProjectChoice? value)
    {
        if (SelectedProject?.SepProject is not { } primary) return;
        if (value?.Project is { } supportProject)
        {
            _mountSelections[primary.Id] = supportProject.Id;
        }
        else
        {
            _mountSelections.Remove(primary.Id);
        }
        try { _entry.SaveSettings(CurrentSettings); }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
    }
    partial void OnSelectedMdbChanged(string? value)
    {
        if (_dynamicProjects) StartLiveCommand.NotifyCanExecuteChanged();
        else RebuildModules();
    }
    partial void OnSelectedModuleChanged(string? value) => RebuildAccessModes();
    partial void OnSelectedAccessModeChanged(string? value) => StartLiveCommand.NotifyCanExecuteChanged();

    private void RebuildMdbs()
    {
        string? previous = SelectedMdb;
        Mdbs.Clear();
        if (_dynamicProjects) Mdbs.Add("ALL");
        foreach (string mdb in _targets.Where(t => t.ProjectCode == SelectedProject?.Code)
                     .Select(t => t.Mdb).Distinct().OrderBy(s => s))
            if (!Mdbs.Contains(mdb)) Mdbs.Add(mdb);
        SelectedMdb = previous != null && Mdbs.Contains(previous) ? previous : Mdbs.FirstOrDefault();
        RebuildModules();
    }

    private void RebuildModules()
    {
        string? previous = SelectedModule;
        Modules.Clear();
        var available = _dynamicProjects ? _profiles.Select(p => p.Module)
            : _targets.Where(t => t.ProjectCode == SelectedProject?.Code && t.Mdb == SelectedMdb)
                .Select(t => t.Module);
        foreach (string module in available.Distinct().OrderBy(s => s))
            Modules.Add(module);
        SelectedModule = previous != null && Modules.Contains(previous) ? previous
            : Modules.FirstOrDefault(module => _dynamicProjects
                ? _profiles.Any(p => p.Module == module && p.AccessModes.Contains("read_only"))
                : _targets.Any(t => t.ProjectCode == SelectedProject?.Code &&
                    t.Mdb == SelectedMdb && t.Module == module && t.AccessModes.Contains("read_only")))
              ?? Modules.FirstOrDefault();
        RebuildAccessModes();
    }

    private void RebuildAccessModes()
    {
        string? previous = SelectedAccessMode;
        var target = SelectedTarget;
        AccessModes.Clear();
        if (target != null) foreach (string mode in target.AccessModes) AccessModes.Add(mode);
        SelectedAccessMode = previous != null && AccessModes.Contains(previous) ? previous
            : AccessModes.Contains("read_only") ? "read_only" : AccessModes.FirstOrDefault();
        RuntimeDescription = target == null ? string.Empty
            : $"{target.RuntimeDirectory} · " +
              (target.RuntimeReady ? Loc.Instance["Live_RuntimeReady"] : Loc.Instance["Live_RuntimeMissing"]);
        StartLiveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartLiveAsync()
    {
        var target = SelectedTarget;
        var mode = SelectedAccessMode;
        if (target == null || mode == null) return;
        if (mode == "controlled_write" && !await _dialogs.ConfirmAsync(
            Loc.Instance["Live_WriteTitle"],
            string.Format(Loc.Instance["Live_WriteBody"],
                target.ProjectCode, target.Mdb, target.Module),
            Loc.Instance["Live_WriteConfirm"], danger: true)) return;
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = Loc.Instance["Live_WaitForLogin"];
        try
        {
            if (SelectedProject?.SepProject is { } sepProject)
            {
                target.ProjectEvarsPath = sepProject.BatPath;
                // Explicit database mounts only. Live makes per-launch startup files;
                // SEP's ordinary custom_evars.bat and project switch stay untouched.
                target.SupportEvarsPaths = SelectedSupportProject?.Project?.BatPath != null
                    ? new List<string> { SelectedSupportProject.Project.BatPath }
                    : new List<string>();
            }
            var job = await _entry.StartAsync(CurrentSettings, target, mode);
            await LoadSessionsAsync();
            StatusMessage = string.Format(Loc.Instance["Live_Ready"],
                target.ProjectCode, target.Mdb, target.Module, mode, job.Session!.TargetProcessId);
            _toasts.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
            _toasts.Error(StatusMessage);
        }
        finally { IsBusy = false; }
    }
}
