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

/// <summary>"My projects": the launch queue that mode "all" loads into E3D in one go, with custom grouping.</summary>
public partial class MyProjectsViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly SessionService _sessions;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;

    public ProjectActions Actions { get; }
    public ObservableCollection<ProjectItem> Items { get; } = new();
    public ObservableCollection<MyProjectGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _count;

    [ObservableProperty]
    private int _offlineCount;

    [ObservableProperty]
    private int _groupCount;

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

        var groupRecords = _catalog.MyProjectGroups.ToList();
        GroupCount = groupRecords.Count;

        var projectsByGroup = wanted.GroupBy(p => p.MyGroupId ?? string.Empty).ToDictionary(g => g.Key, g => g.ToList());
        var existingById = Groups.ToDictionary(g => g.Id ?? string.Empty, g => g);
        var newGroupsList = new List<MyProjectGroupViewModel>();

        // 1. Custom groups in catalog order
        foreach (var rec in groupRecords)
        {
            if (!existingById.TryGetValue(rec.Id, out var vm))
            {
                vm = new MyProjectGroupViewModel(rec.Id, rec.Name, rec.IsExpanded, _catalog, _dialogs, _toasts, Actions);
            }
            else
            {
                vm.Name = rec.Name;
                vm.IsExpanded = rec.IsExpanded;
            }

            var groupItems = projectsByGroup.TryGetValue(rec.Id, out var list) ? list : new List<ProjectItem>();
            if (!groupItems.SequenceEqual(vm.Items))
            {
                vm.Items.Clear();
                foreach (var it in groupItems) vm.Items.Add(it);
            }
            vm.RefreshCounts();
            newGroupsList.Add(vm);
        }

        // 2. Ungrouped / default group
        var ungroupedItems = projectsByGroup.TryGetValue(string.Empty, out var ungrp) ? ungrp : new List<ProjectItem>();
        if (ungroupedItems.Count > 0 || groupRecords.Count == 0)
        {
            if (!existingById.TryGetValue(string.Empty, out var defaultVm))
            {
                defaultVm = new MyProjectGroupViewModel(null, Strings.My_Ungrouped, true, _catalog, _dialogs, _toasts, Actions);
            }
            else
            {
                defaultVm.Name = Strings.My_Ungrouped;
            }

            if (!ungroupedItems.SequenceEqual(defaultVm.Items))
            {
                defaultVm.Items.Clear();
                foreach (var it in ungroupedItems) defaultVm.Items.Add(it);
            }
            defaultVm.RefreshCounts();
            newGroupsList.Add(defaultVm);
        }

        // Reconcile Groups collection
        if (!newGroupsList.SequenceEqual(Groups))
        {
            Groups.Clear();
            foreach (var g in newGroupsList) Groups.Add(g);
        }
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = Strings.My_NewGroupTitle,
            TextLabel = Strings.My_NewGroupPrompt,
            TextPlaceholder = "Database_Group",
            TextMaxLength = 50,
            OkLabel = Strings.Common_Save,
        });
        if (res == null) return;

        string name = res.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return;

        _catalog.AddMyProjectGroup(name);
        _toasts.Success(Strings.Common_Saved);
    }

    [RelayCommand]
    private async Task MoveToGroupAsync(ProjectItem? item)
    {
        if (item == null) return;
        var customGroups = _catalog.MyProjectGroups.ToList();

        if (customGroups.Count == 0)
        {
            var res = await _dialogs.PromptAsync(new PromptRequest
            {
                Title = Strings.My_NewGroupTitle,
                Message = item.Title,
                TextLabel = Strings.My_NewGroupPrompt,
                TextPlaceholder = "Database_Group",
                TextMaxLength = 50,
                OkLabel = Strings.Common_Save,
            });
            if (res == null) return;
            string name = res.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return;
            var created = _catalog.AddMyProjectGroup(name);
            _catalog.SetProjectGroup(item, created.Id);
            _toasts.Success(string.Format(Strings.My_MovedToGroup, item.Title, created.Name));
            return;
        }

        string newGroupOption = "+ " + Strings.My_NewGroup;
        var choices = new List<string> { Strings.My_MoveToUngrouped };
        choices.AddRange(customGroups.Select(g => g.Name));
        choices.Add(newGroupOption);

        string currentChoice = string.IsNullOrEmpty(item.MyGroupId)
            ? Strings.My_MoveToUngrouped
            : (item.MyGroupName ?? Strings.My_MoveToUngrouped);

        var pick = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = Strings.My_MoveToGroupTitle,
            Message = item.Title,
            ChoiceLabel = Strings.My_MoveToGroupPrompt,
            Choices = choices,
            ChoiceInitial = currentChoice,
            OkLabel = Strings.Common_Ok,
        });
        if (pick == null) return;

        string selected = (pick.Choice ?? pick.Text).Trim();
        if (string.Equals(selected, Strings.My_MoveToUngrouped, StringComparison.OrdinalIgnoreCase))
        {
            _catalog.SetProjectGroup(item, null);
            _toasts.Success(string.Format(Strings.My_MovedToGroup, item.Title, Strings.My_Ungrouped));
        }
        else if (string.Equals(selected, newGroupOption, StringComparison.OrdinalIgnoreCase))
        {
            var createRes = await _dialogs.PromptAsync(new PromptRequest
            {
                Title = Strings.My_NewGroupTitle,
                TextLabel = Strings.My_NewGroupPrompt,
                TextPlaceholder = "Database_Group",
                TextMaxLength = 50,
                OkLabel = Strings.Common_Save,
            });
            if (createRes == null) return;
            string name = createRes.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return;
            var created = _catalog.AddMyProjectGroup(name);
            _catalog.SetProjectGroup(item, created.Id);
            _toasts.Success(string.Format(Strings.My_MovedToGroup, item.Title, created.Name));
        }
        else
        {
            var targetGrp = customGroups.FirstOrDefault(g => g.Name == selected);
            if (targetGrp != null)
            {
                _catalog.SetProjectGroup(item, targetGrp.Id);
                _toasts.Success(string.Format(Strings.My_MovedToGroup, item.Title, targetGrp.Name));
            }
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var g in Groups) g.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var g in Groups) g.IsExpanded = false;
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

    private void Move(ProjectItem? item, int delta)
    {
        if (item == null) return;
        _catalog.MoveMyProject(item, delta);
    }

    [RelayCommand]
    private Task ProbeSessionsAsync() => _sessions.ProbeAsync(Items.ToList());

    [RelayCommand]
    private void GoProjects() => Actions.Navigate(typeof(Views.Pages.ProjectsPage));
}
