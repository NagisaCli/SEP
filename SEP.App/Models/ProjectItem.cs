using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

/// <summary>A category as shown on a project (name + colour); null when the project has none.</summary>
public sealed record CategoryInfo(string Id, string Name, string Color);

/// <summary>Someone connected to a project: a SEP heartbeat (.sep_sessions) or the owner of a database lock.</summary>
public sealed record ProjectSession(string User, string Computer, string Source, bool IsThisDevice, DateTime? Since);

/// <summary>A discovered E3D project (one evarsXXX.bat) as shown in the UI.</summary>
public partial class ProjectItem : ObservableObject
{
    /// <summary>Stable id shared with the Python tool: "proj_" + sha1(lower(bat_path))[:12].</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The XXX of evarsXXX.bat (e.g. AvevaPlantSample, mdu).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>AVEVA project code from the "set XXX000=" line (e.g. SAM); null when it could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private string? _code;

    public string BatPath { get; init; } = string.Empty;

    /// <summary>Folder holding the project (its sub-folder in a library, or the library folder itself).</summary>
    public string ProjectDir { get; init; } = string.Empty;

    public string LibraryId { get; init; } = string.Empty;
    public string LibraryName { get; init; } = string.Empty;
    public bool IsUnc { get; init; }
    public DateTime? DiscoveredAt { get; init; }

    // ── metadata (project_meta, shared with the Python UI) ───────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasDisplayName))]
    private string? _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwner))]
    private string? _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    private IReadOnlyList<string> _tags = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string? _description;

    [ObservableProperty]
    private string? _notes;

    /// <summary>Stored status token (进行中 / 已完成 / 暂停 / 归档) or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategory))]
    private CategoryInfo? _category;

    [ObservableProperty]
    private DateTime? _metaUpdatedAt;

    // ── state ────────────────────────────────────────────────────────────────────

    /// <summary>Listed in "my projects" (the Python UI's 我的项目).</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>Launched last (settings.last_launched).</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>True while the project comes from the cache of a library that could not be reached this session.</summary>
    [ObservableProperty]
    private bool _isCached;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private int _lockCount;

    [ObservableProperty]
    private List<string> _lockFiles = new();

    /// <summary>True once the lock probe has run for this project.</summary>
    [ObservableProperty]
    private bool _lockStateKnown;

    /// <summary>People connected right now (SEP heartbeats and lock owners); empty until probed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineCount))]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(OnlineSummary))]
    private IReadOnlyList<ProjectSession> _sessions = Array.Empty<ProjectSession>();

    [ObservableProperty]
    private bool _sessionsKnown;

    /// <summary>Selected in batch mode on the Projects page.</summary>
    [ObservableProperty]
    private bool _isSelected;

    // ── derived ──────────────────────────────────────────────────────────────────

    public string Title => string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : DisplayName;

    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase);
    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);
    public bool HasTags => Tags.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasStatus => Status.Length > 0;
    public bool HasCategory => Category != null;
    public int OnlineCount => Sessions.Count;
    public bool IsOnline => Sessions.Count > 0;
    public string OnlineSummary => string.Join(", ", Sessions.Select(s => $"{s.User}@{s.Computer}"));

    /// <summary>Badge text: the project code when known, otherwise the first letters of the name.</summary>
    public string Badge => !string.IsNullOrEmpty(Code) ? Code : (Name.Length <= 5 ? Name.ToUpperInvariant() : Name[..3].ToUpperInvariant());

    /// <summary>Everything a free-text search should match.</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        return Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (Code?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Owner?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || BatPath.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || LibraryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (Category?.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
