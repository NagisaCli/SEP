using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>A status option of the project editor: stored token + localized label.</summary>
public sealed record StatusOption(string Token, string Label)
{
    public override string ToString() => Label;

    public static string LabelOf(string token) => token switch
    {
        "进行中" => Strings.Status_InProgress,
        "已完成" => Strings.Status_Completed,
        "暂停" => Strings.Status_OnHold,
        "归档" => Strings.Status_Archived,
        _ => Strings.Status_None,
    };

    public static List<StatusOption> All() => new List<string> { "" }.Concat(IProjectCatalog.StatusOptions).Select(t => new StatusOption(t, LabelOf(t))).ToList();
}

/// <summary>A category option of the project editor ("(none)" first).</summary>
public sealed record CategoryOption(string Id, string Name, string Color)
{
    public override string ToString() => Name;
}

/// <summary>Editor state for one project, or for several at once (batch: only the fields the user touched apply).</summary>
public partial class ProjectEditViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;

    public IReadOnlyList<ProjectItem> Projects { get; }
    public bool IsBatch => Projects.Count > 1;
    public string Title => IsBatch ? string.Format(Strings.Edit_BatchTitle, Projects.Count) : string.Format(Strings.Edit_Title, Projects[0].Name);

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private CategoryOption? _category;
    [ObservableProperty] private StatusOption? _status;
    [ObservableProperty] private string _owner = string.Empty;
    [ObservableProperty] private string _tagsText = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    // batch: which fields to apply
    [ObservableProperty] private bool _applyCategory;
    [ObservableProperty] private bool _applyStatus;
    [ObservableProperty] private bool _applyOwner;
    [ObservableProperty] private bool _applyTags;
    /// <summary>Batch tags: add to existing (true) or replace (false).</summary>
    [ObservableProperty] private bool _appendTags = true;

    public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();
    public List<StatusOption> StatusOptions { get; } = StatusOption.All();
    public IReadOnlyList<string> KnownTags => _catalog.AllTags;

    public ProjectEditViewModel(IProjectCatalog catalog, IReadOnlyList<ProjectItem> projects)
    {
        _catalog = catalog;
        Projects = projects;
        RefreshCategories();
        if (!IsBatch)
        {
            var p = projects[0];
            DisplayName = p.DisplayName ?? string.Empty;
            Category = CategoryOptions.FirstOrDefault(c => c.Id == (p.Category?.Id ?? string.Empty)) ?? CategoryOptions[0];
            Status = StatusOptions.FirstOrDefault(s => s.Token == p.Status) ?? StatusOptions[0];
            Owner = p.Owner ?? string.Empty;
            TagsText = string.Join(", ", p.Tags);
            Description = p.Description ?? string.Empty;
            Notes = p.Notes ?? string.Empty;
        }
        else
        {
            Category = CategoryOptions[0];
            Status = StatusOptions[0];
        }
        _initialized = true;
    }

    // batch: touching a field is the natural way to say "apply this one"
    private bool _initialized;
    partial void OnCategoryChanged(CategoryOption? value) { if (_initialized && IsBatch) ApplyCategory = true; }
    partial void OnStatusChanged(StatusOption? value) { if (_initialized && IsBatch) ApplyStatus = true; }
    partial void OnOwnerChanged(string value) { if (_initialized && IsBatch) ApplyOwner = true; }
    partial void OnTagsTextChanged(string value) { if (_initialized && IsBatch) ApplyTags = true; }

    public void RefreshCategories()
    {
        var current = Category?.Id;
        CategoryOptions.Clear();
        CategoryOptions.Add(new CategoryOption(string.Empty, Strings.Category_None, "#6B7280"));
        foreach (var c in _catalog.Categories) CategoryOptions.Add(new CategoryOption(c.Id, c.Name, c.Color));
        Category = CategoryOptions.FirstOrDefault(c => c.Id == (current ?? string.Empty)) ?? CategoryOptions[0];
    }

    [RelayCommand]
    private void AddTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var tags = ParseTags(TagsText);
        if (!tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase)) tags.Add(tag.Trim());
        TagsText = string.Join(", ", tags);
    }

    public static List<string> ParseTags(string text) =>
        text.Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Writes the edits to the catalog; returns false when nothing applies.</summary>
    public bool Save()
    {
        if (!IsBatch)
        {
            _catalog.UpdateProjectMeta(Projects[0], new ProjectMetaUpdate
            {
                DisplayName = DisplayName,
                CategoryId = Category?.Id ?? string.Empty,
                Status = Status?.Token ?? string.Empty,
                Owner = Owner,
                Tags = ParseTags(TagsText),
                Description = Description,
                Notes = Notes,
            });
            return true;
        }

        if (!ApplyCategory && !ApplyStatus && !ApplyOwner && !ApplyTags) return false;
        var newTags = ParseTags(TagsText);
        foreach (var p in Projects)
        {
            IReadOnlyList<string>? tags = null;
            if (ApplyTags) tags = AppendTags ? p.Tags.Concat(newTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : newTags;
            _catalog.UpdateProjectMeta(p, new ProjectMetaUpdate
            {
                CategoryId = ApplyCategory ? Category?.Id ?? string.Empty : null,
                Status = ApplyStatus ? Status?.Token ?? string.Empty : null,
                Owner = ApplyOwner ? Owner : null,
                Tags = tags,
            });
        }
        return true;
    }
}
