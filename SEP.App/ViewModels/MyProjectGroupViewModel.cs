using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>Represents a custom or default group of projects in "My Projects".</summary>
public partial class MyProjectGroupViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ProjectActions _actions;

    public string? Id { get; }

    [ObservableProperty]
    private string _name;

    public bool IsDefault => string.IsNullOrEmpty(Id);

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<ProjectItem> Items { get; } = new();

    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
    public string CountText => string.Format(Strings.My_CountFormat, Count);

    public bool CanLaunchGroup => HasItems && _actions.CanLaunch();

    public MyProjectGroupViewModel(
        string? id,
        string name,
        bool isExpanded,
        IProjectCatalog catalog,
        IDialogService dialogs,
        ToastService toasts,
        ProjectActions actions)
    {
        Id = id;
        _name = name;
        _isExpanded = isExpanded;
        _catalog = catalog;
        _dialogs = dialogs;
        _toasts = toasts;
        _actions = actions;

        Items.CollectionChanged += OnItemsCollectionChanged;
        _actions.PropertyChanged += OnActionsPropertyChanged;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshCounts();
    }

    private void OnActionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectActions.IsLaunching))
        {
            LaunchGroupCommand.NotifyCanExecuteChanged();
        }
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
        LaunchGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!IsDefault && Id != null)
        {
            _catalog.SetMyProjectGroupExpanded(Id, value);
        }
    }

    [RelayCommand]
    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(CanLaunchGroup))]
    public async Task LaunchGroupAsync()
    {
        if (Items.Count == 0) return;
        await _actions.LaunchGroupAsync(Items);
    }

    [RelayCommand]
    public async Task RenameAsync()
    {
        if (IsDefault || Id == null) return;
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = Strings.My_RenameGroupTitle,
            TextLabel = Strings.My_RenameGroupPrompt,
            TextInitial = Name,
            TextMaxLength = 50,
            OkLabel = Strings.Common_Save
        });
        if (res == null) return;

        string newName = res.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(newName) && newName != Name)
        {
            if (_catalog.RenameMyProjectGroup(Id, newName))
            {
                Name = newName;
                _toasts.Success(Strings.Common_Saved);
            }
        }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (IsDefault || Id == null) return;
        bool ok = await _dialogs.ConfirmAsync(
            Strings.My_DeleteGroupTitle,
            string.Format(Strings.My_DeleteGroupBody, Name),
            Strings.My_DeleteGroupConfirm,
            danger: true);
        if (!ok) return;

        _catalog.RemoveMyProjectGroup(Id);
    }
}
