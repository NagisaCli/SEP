using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

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
    private string _category = "通用";

    [ObservableProperty]
    private string _source = "本地";

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
