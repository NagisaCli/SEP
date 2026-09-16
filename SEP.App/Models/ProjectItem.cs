using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

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

    /// <summary>Set from the Python UI's project_meta when present.</summary>
    public string? DisplayName { get; init; }
    public string? Owner { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

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

    public string Title => string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{Name} · {DisplayName}";

    /// <summary>Badge text: the project code when known, otherwise the first letters of the name.</summary>
    public string Badge => !string.IsNullOrEmpty(Code) ? Code : (Name.Length <= 5 ? Name.ToUpperInvariant() : Name[..3].ToUpperInvariant());
}
