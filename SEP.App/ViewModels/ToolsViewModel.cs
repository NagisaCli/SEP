using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>A library the network diagnosis can be pointed at.</summary>
public sealed record LibraryChoice(LibraryItem Library)
{
    public string Display => Library.IsUnc ? $"🌐 {Library.Name}  —  {Library.Path}" : $"💻 {Library.Name}  —  {Library.Path}";
    public override string ToString() => Display;
}

/// <summary>Tools page: health checks, one-click repairs, USERDATA / CAD font tools, network diagnosis and lock clean-up.</summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly E3dToolsService _tools;
    private readonly IE3dDiagService _diag;
    private readonly IProjectCatalog _catalog;
    private readonly PluginService _plugins;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;

    public ProjectActions Actions { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckConfigCommand))] [NotifyCanExecuteChangedFor(nameof(FixConfigCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanUserDataCommand))] [NotifyCanExecuteChangedFor(nameof(FixCadFontsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiagnoseLibraryCommand))] [NotifyCanExecuteChangedFor(nameof(DiagnoseAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexesCommand))] [NotifyCanExecuteChangedFor(nameof(ScanLocksCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyFixCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = Strings.Common_Ready;
    [ObservableProperty] private bool _statusIsError;

    // report panel
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _reportTitle = string.Empty;
    [ObservableProperty] private string _reportSubtitle = string.Empty;
    [ObservableProperty] private bool _reportOk;
    public ObservableCollection<DiagCheck> Checks { get; } = new();
    public ObservableCollection<DiagFix> Fixes { get; } = new();
    public ObservableCollection<string> Output { get; } = new();
    [ObservableProperty] private bool _hasOutput;

    // network diagnosis
    public ObservableCollection<LibraryChoice> Libraries { get; } = new();
    [ObservableProperty] private LibraryChoice? _selectedLibrary;

    // locks
    public ObservableCollection<SessionLockItem> Locks { get; } = new();
    [ObservableProperty] private bool _locksScanned;
    [ObservableProperty] private string _userDataDir = string.Empty;
    [ObservableProperty] private string _installDir = string.Empty;
    [ObservableProperty] private string _customEvarsPath = string.Empty;

    public ToolsViewModel(E3dToolsService tools, IE3dDiagService diag, IProjectCatalog catalog, PluginService plugins,
        IDialogService dialogs, ToastService toasts, ProjectActions actions)
    {
        _tools = tools;
        _diag = diag;
        _catalog = catalog;
        _plugins = plugins;
        _dialogs = dialogs;
        _toasts = toasts;
        Actions = actions;
        UserDataDir = tools.UserDataDir;
        InstallDir = tools.InstallDir;
        CustomEvarsPath = tools.CustomEvarsPath;
        _catalog.Changed += (_, _) => OnUiThread(RefreshLibraries);
        RefreshLibraries();
    }

    private static void OnUiThread(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action(); else d.BeginInvoke(action);
    }

    private void RefreshLibraries()
    {
        var current = SelectedLibrary?.Library.Id;
        Libraries.Clear();
        foreach (var l in _catalog.Libraries) Libraries.Add(new LibraryChoice(l));
        SelectedLibrary = Libraries.FirstOrDefault(c => c.Library.Id == current) ?? Libraries.FirstOrDefault();
    }

    private bool NotBusy() => !IsBusy;

    private void SetStatus(string text, bool error) { StatusMessage = text; StatusIsError = error; }

    private void ShowReport(string title, string subtitle, DiagReport report)
    {
        ReportTitle = title;
        ReportSubtitle = subtitle;
        ReportOk = report.Ok;
        Checks.Clear(); foreach (var c in report.Checks) Checks.Add(c);
        Fixes.Clear(); foreach (var f in report.Fixes) Fixes.Add(f);
        Output.Clear(); HasOutput = false;
        HasReport = true;
    }

    private void ShowResult(string title, ToolResult result)
    {
        ReportTitle = title;
        ReportSubtitle = result.Message;
        ReportOk = result.Ok;
        Checks.Clear(); Fixes.Clear();
        Output.Clear(); foreach (var line in result.Output) Output.Add(line);
        HasOutput = Output.Count > 0;
        HasReport = true;
        _toasts.Result(result.Ok, result.Message);
    }

    private async Task RunAsync(string progress, Func<Task> work)
    {
        IsBusy = true;
        SetStatus(progress, false);
        try { await work(); SetStatus(Strings.Common_Ready, false); }
        catch (Exception ex) { SetStatus(string.Format(Strings.Diag_Error, ex.Message), true); _toasts.Error(ex.Message); }
        finally { IsBusy = false; }
    }

    // ── E3D configuration ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task CheckConfigAsync() => RunAsync(Strings.Tools_Checking, async () =>
    {
        var report = await _tools.DiagnoseConfigAsync();
        ShowReport(Strings.Tools_ConfigReportTitle, report.Ok ? Strings.Tools_ConfigReportOk : string.Format(Strings.Tools_ConfigReportProblems, report.Problems), report);
    });

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task FixConfigAsync()
    {
        bool ok = await _dialogs.ConfirmAsync(Strings.Tools_FixConfigTitle, Strings.Tools_FixConfigConfirm, Strings.Tools_FixConfigButton);
        if (!ok) return;
        await RunAsync(Strings.Tools_Fixing, async () =>
        {
            var res = await _tools.FixConfigAsync();
            ShowResult(Strings.Tools_FixConfigTitle, res);
        });
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ApplyFixAsync(DiagFix? fix)
    {
        if (fix == null) return;
        if (fix.Id == "e3d_config_clean") { await FixConfigAsync(); return; }
        if (fix.RequiresAdmin)
        {
            bool ok = await _dialogs.ConfirmAsync(fix.Title, string.Join("\n", fix.Steps) + "\n\n" + Strings.Tools_FixNeedsAdmin, Strings.Tools_FixRun);
            if (!ok) return;
        }
        await RunAsync(fix.Title, async () =>
        {
            var res = await _tools.ApplyFixAsync(fix);
            ShowResult(fix.Title, res);
        });
    }

    [RelayCommand]
    private void CopyCommands(DiagFix? fix)
    {
        if (fix == null || fix.Commands.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, fix.Commands)); _toasts.Success(Strings.Tools_CommandsCopied); }
        catch (Exception ex) { _toasts.Error(ex.Message); }
    }

    // ── other tools ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CleanUserDataAsync()
    {
        bool ok = await _dialogs.ConfirmAsync(Strings.Tools_UserDataTitle, string.Format(Strings.Tools_UserDataConfirm, UserDataDir), Strings.Tools_UserDataButton);
        if (!ok) return;
        await RunAsync(Strings.Tools_Cleaning, async () => ShowResult(Strings.Tools_UserDataTitle, await _tools.CleanUserDataAsync()));
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task FixCadFontsAsync()
    {
        bool ok = await _dialogs.ConfirmAsync(Strings.Tools_CadTitle, Strings.Tools_CadConfirm, Strings.Tools_CadButton);
        if (!ok) return;
        await RunAsync(Strings.Tools_Fixing, async () => ShowResult(Strings.Tools_CadTitle, await _tools.FixCadFontsAsync()));
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RebuildIndexesAsync() => RunAsync(Strings.Tools_Reindexing, async () =>
    {
        var plugins = await _plugins.ScanAsync(autoHeal: false);
        ShowResult(Strings.Tools_ReindexTitle, await _plugins.RebuildAllIndexesAsync(plugins));
    });

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task DiagnoseLibraryAsync() => RunAsync(Strings.Tools_Diagnosing, async () =>
    {
        var lib = SelectedLibrary?.Library;
        if (lib == null) return;
        var report = await _tools.DiagnoseLibraryAsync(lib.Path);
        ShowReport(string.Format(Strings.Tools_NetReportTitle, lib.Name), lib.Path, report);
    });

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task DiagnoseAllAsync() => RunAsync(Strings.Tools_Diagnosing, async () =>
    {
        var checks = new List<DiagCheck>();
        var fixes = new List<DiagFix>();
        int problems = 0;
        foreach (var lib in _catalog.Libraries)
        {
            var report = await _tools.DiagnoseLibraryAsync(lib.Path);
            problems += report.Problems;
            checks.Add(new DiagCheck
            {
                Id = "lib:" + lib.Id,
                Name = lib.Name,
                Status = report.Ok ? CheckStatus.Ok : report.Checks.Any(c => c.Status == CheckStatus.Fail) ? CheckStatus.Fail : CheckStatus.Warn,
                Detail = report.Ok ? lib.Path : $"{lib.Path} — " + string.Join("; ", report.Checks.Where(c => c.Status is CheckStatus.Fail or CheckStatus.Warn).Select(c => $"{c.Name}: {c.Detail}")),
                Fix = report.Fixes.FirstOrDefault(),
            });
            foreach (var f in report.Fixes) if (fixes.All(x => x.Id != f.Id)) fixes.Add(f);
        }
        var combined = new DiagReport { Ok = problems == 0, Checks = checks, Fixes = fixes, Problems = problems };
        ShowReport(Strings.Tools_NetAllTitle, problems == 0 ? Strings.Tools_NetAllOk : string.Format(Strings.Tools_NetAllProblems, problems), combined);
    });

    // ── locks ────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task ScanLocksAsync() => RunAsync(Strings.Diag_Running, async () =>
    {
        await _catalog.RescanAllAsync(force: false);
        var locks = await _diag.ScanAllLocksAsync(_catalog.Projects);
        Locks.Clear();
        foreach (var l in locks) Locks.Add(l);
        LocksScanned = true;
        SetStatus(string.Format(Strings.Diag_Complete, Locks.Count), false);
    });

    [RelayCommand]
    private async Task UnlockAsync(SessionLockItem? item)
    {
        if (item == null) return;
        bool ok = await _dialogs.ConfirmAsync(Strings.Diag_UnlockButton, string.Format(Strings.Lock_ConfirmBody, item.FileName, item.ProjectCode), Strings.Diag_UnlockButton, danger: true);
        if (!ok) return;
        var res = await _diag.UnlockSessionAsync(item);
        _toasts.Result(res.Success, res.Message);
        if (res.Success) Locks.Remove(item);
    }

    [RelayCommand]
    private async Task UnlockAllAsync()
    {
        if (Locks.Count == 0) { _toasts.Info(Strings.Diag_NoLocks); return; }
        bool ok = await _dialogs.ConfirmAsync(Strings.Diag_UnlockAll, string.Format(Strings.Lock_ConfirmAllBody, Locks.Count), Strings.Diag_UnlockAll, danger: true);
        if (!ok) return;
        var (done, fail) = await _diag.UnlockAllSessionsAsync(Locks.ToList());
        _toasts.Result(fail == 0, string.Format(Strings.Diag_ClearAllDone, done, fail));
        await ScanLocksAsync();
    }

    [RelayCommand]
    private void OpenUserData() => Actions.OpenFolder(new ProjectItem { ProjectDir = UserDataDir });

    [RelayCommand]
    private void CloseReport() => HasReport = false;

    [RelayCommand]
    private void GoSettings() => Actions.Navigate(typeof(Views.Pages.SettingsPage));
}
