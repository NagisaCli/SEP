using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IE3dDiagService _diagService;
    private readonly IProjectCatalog _catalog;

    [ObservableProperty]
    private ObservableCollection<SystemDiagItem> _diagItems = new();

    [ObservableProperty]
    private ObservableCollection<SessionLockItem> _lockItems = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    public DiagnosticsViewModel(IE3dDiagService diagService, IProjectCatalog catalog)
    {
        _diagService = diagService;
        _catalog = catalog;

        RunDiagnosticsCommand.Execute(null);
    }

    /// <summary>Initial check: reuses the last library scan when it is fresh, so opening the page is instant.</summary>
    [RelayCommand]
    public Task RunDiagnosticsAsync() => RunAsync(forceRescan: false);

    /// <summary>"Re-run check" button: probes every library again.</summary>
    [RelayCommand]
    private Task RerunAsync() => RunAsync(forceRescan: true);

    private async Task RunAsync(bool forceRescan)
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = Strings.Diag_Running;

        try
        {
            var items = await _diagService.RunSystemDiagnosticsAsync();
            DiagItems = new ObservableCollection<SystemDiagItem>(items);

            await _catalog.RescanAllAsync(forceRescan);
            var locks = await _diagService.ScanAllLocksAsync(_catalog.Projects);
            LockItems = new ObservableCollection<SessionLockItem>(locks);

            StatusMessage = string.Format(Strings.Diag_Complete, LockItems.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Strings.Diag_Error, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UnlockSingleAsync(SessionLockItem? item)
    {
        if (item == null) return;
        var res = await _diagService.UnlockSessionAsync(item);
        StatusMessage = res.Message;
        if (res.Success)
        {
            LockItems.Remove(item);
        }
    }

    [RelayCommand]
    private async Task UnlockAllAsync()
    {
        if (LockItems.Count == 0)
        {
            StatusMessage = Strings.Diag_NoLocks;
            return;
        }

        IsLoading = true;
        StatusMessage = Strings.Diag_ClearingAll;

        var (ok, fail) = await _diagService.UnlockAllSessionsAsync(LockItems);
        StatusMessage = string.Format(Strings.Diag_ClearAllDone, ok, fail);
        IsLoading = false;

        await RunDiagnosticsAsync();
    }
}
