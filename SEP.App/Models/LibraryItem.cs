using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

/// <summary>A path library (a folder of project folders, a single project folder, or an evarsXXX.bat) as shown in the UI.</summary>
public partial class LibraryItem : ObservableObject
{
    /// <summary>Stable id shared with the Python tool: "lib_" + sha1(lower(path))[:12].</summary>
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsUnc { get; init; }

    /// <summary>True for a collection (many projects), false for a single project.</summary>
    [ObservableProperty]
    private bool _isCollection = true;

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>Localized reason when the last scan failed; null when the library is reachable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReachable))]
    private string? _lastError;

    [ObservableProperty]
    private DateTime? _lastScan;

    [ObservableProperty]
    private int _projectCount;

    [ObservableProperty]
    private bool _isExpanded = true;

    public bool IsReachable => LastError == null;
}
