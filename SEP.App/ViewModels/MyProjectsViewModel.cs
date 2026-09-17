using System;
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

/// <summary>"My projects": the launch queue that mode "all" loads into E3D in one go.</summary>
public partial class MyProjectsViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly SessionService _sessions;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;

    public ProjectActions Actions { get; }
    public ObservableCollection<ProjectItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _count;

    [ObservableProperty]
    private int _offlineCount;

    public bool HasItems => Count > 0;
    public string CountText => string.Format(Strings.My_CountFormat, Count);

    public MyProjectsViewModel(IProjectCatalog catalog, SessionService sessions, IDialogService dialogs, ToastService toasts, ProjectActions actions)
    {
        _catalog = catalog;
        _sessions = sessions;
        _dialogs = dialogs;
        _toasts = toasts;
        Actions = actions;
        _catalog.Changed += (_, _) => OnUiThread(Rebuild);
        Rebuild();
    }

    private static void OnUiThread(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action(); else d.BeginInvoke(action);
    }

    private void Rebuild()
    {
        var wanted = _catalog.MyProjects.ToList();
        if (!wanted.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var p in wanted) Items.Add(p);
        }
        Count = Items.Count;
        OfflineCount = Items.Count(p => p.IsCached);
    }

    [RelayCommand]
    private void Remove(ProjectItem? item)
    {
        if (item == null) return;
        _catalog.SetMyProjects(new[] { item }, false);
        _toasts.Info(string.Format(Strings.Project_RemovedFromMine, item.Title));
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Count == 0) return;
        bool ok = await _dialogs.ConfirmAsync(Strings.My_ClearTitle, string.Format(Strings.My_ClearBody, Count), Strings.My_ClearConfirm, danger: true);
        if (!ok) return;
        _catalog.ClearMyProjects();
        _toasts.Info(Strings.My_Cleared);
    }

    [RelayCommand]
    private void MoveUp(ProjectItem? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(ProjectItem? item) => Move(item, +1);

    /// <summary>Order matters: it is the order of the managed block, i.e. the order E3D lists the projects.</summary>
    private void Move(ProjectItem? item, int delta)
    {
        if (item == null) return;
        var list = _catalog.Data.MyProjects;
        int idx = list.FindIndex(m => m.Id == item.Id);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= list.Count) return;
        (list[idx], list[target]) = (list[target], list[idx]);
        _catalog.SetMyProjects(Array.Empty<ProjectItem>(), false);   // persists + rebuilds the ordered list
    }

    [RelayCommand]
    private Task ProbeSessionsAsync() => _sessions.ProbeAsync(Items.ToList());

    [RelayCommand]
    private void GoProjects() => Actions.Navigate(typeof(Views.Pages.ProjectsPage));
}
