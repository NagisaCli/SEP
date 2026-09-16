using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

/// <summary>Reachability of a project folder; the order matters (it indexes localized texts in XAML).</summary>
public enum ProjectAvailability
{
    Online = 0,
    HostOffline = 1,
    NotMounted = 2,
    MetricsUnavailable = 3
}

public partial class ProjectItem : ObservableObject
{
    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private int _lockCount;

    [ObservableProperty]
    private List<string> _lockFiles = new();

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private string _sizeHuman = "0 MB";

    [ObservableProperty]
    private ProjectAvailability _availability = ProjectAvailability.Online;

    [ObservableProperty]
    private string _category = Resources.Strings.Project_DefaultCategory;

    /// <summary>True for \\server\share (UNC) paths; the display text is localized in XAML.</summary>
    [ObservableProperty]
    private bool _isUnc;

    [ObservableProperty]
    private bool _exists = true;

    [ObservableProperty]
    private DateTime? _lastAccessed;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) || Name.Equals(Code, StringComparison.OrdinalIgnoreCase) 
        ? Code 
        : $"{Code} · {Name}";

    public string BadgeColor => Code.Length switch
    {
        2 => "#38bdf8",
        3 => "#00e5ff",
        4 => "#818cf8",
        _ => "#34d399"
    };
}
