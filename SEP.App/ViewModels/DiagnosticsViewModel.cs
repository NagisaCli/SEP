using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IE3dDiagService _diagService;
    private readonly IE3dProjectService _projectService;

    [ObservableProperty]
    private ObservableCollection<SystemDiagItem> _diagItems = new();

    [ObservableProperty]
    private ObservableCollection<SessionLockItem> _lockItems = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    public DiagnosticsViewModel(IE3dDiagService diagService, IE3dProjectService projectService)
    {
        _diagService = diagService;
        _projectService = projectService;

        RunDiagnosticsCommand.Execute(null);
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        IsLoading = true;
        StatusMessage = "正在执行环境体检与锁文件扫描...";

        try
        {
            var items = await _diagService.RunSystemDiagnosticsAsync();
            DiagItems = new ObservableCollection<SystemDiagItem>(items);

            var projs = await _projectService.LoadAllProjectsAsync();
            var locks = await _diagService.ScanAllLocksAsync(projs);
            LockItems = new ObservableCollection<SessionLockItem>(locks);

            StatusMessage = $"体检完成：共发现 {LockItems.Count} 个活跃/残留数据库锁。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"诊断出错: {ex.Message}";
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
            StatusMessage = "当前没有需要清理的锁文件。";
            return;
        }

        IsLoading = true;
        StatusMessage = "正在一键清理所有残留锁...";

        var (ok, fail) = await _diagService.UnlockAllSessionsAsync(LockItems);
        StatusMessage = $"一键清理完成：成功解锁 {ok} 个，失败 {fail} 个。";

        await RunDiagnosticsAsync();
    }
}
