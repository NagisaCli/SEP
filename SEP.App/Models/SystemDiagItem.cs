using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

public enum DiagSeverity
{
    Success,
    Warning,
    Error,
    Info
}

public partial class SystemDiagItem : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private DiagSeverity _severity = DiagSeverity.Success;

    [ObservableProperty]
    private string _actionLabel = string.Empty;

    [ObservableProperty]
    private bool _canAutoFix;
}
