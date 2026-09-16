using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

public partial class SessionLockItem : ObservableObject
{
    [ObservableProperty]
    private string _projectCode = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private DateTime _lockTime;

    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    private bool _isOrphan = true;

    [ObservableProperty]
    private string _statusMessage = Resources.Strings.Lock_StatusOrphan;
}
