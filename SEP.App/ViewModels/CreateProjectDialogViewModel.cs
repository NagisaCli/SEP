using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class CreateProjectDialogViewModel : ObservableObject
{
    private readonly IE3dProjectService _projectService;

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _rootDir = string.Empty;

    [ObservableProperty]
    private bool _isCloneMode;

    [ObservableProperty]
    private ObservableCollection<string> _availableTemplates = new();

    [ObservableProperty]
    private string _selectedTemplate = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    public CreateProjectDialogViewModel(IE3dProjectService projectService)
    {
        _projectService = projectService;

        RootDir = _projectService.PathsConfig.ProjectsDir ?? @"D:\AVEVA\Projects\E3D3.1";

        // Collect available template directories
        foreach (var kv in _projectService.ProjectsConfig.Projects)
        {
            if (Directory.Exists(kv.Value))
            {
                AvailableTemplates.Add(kv.Value);
            }
        }
        if (AvailableTemplates.Count > 0)
        {
            SelectedTemplate = AvailableTemplates[0];
        }
    }

    partial void OnCodeChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        string upper = Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (upper.Length > 5) upper = upper[..5];
        if (upper != value)
        {
            Code = upper;
        }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        ErrorMessage = string.Empty;

        if (Code.Length < 2 || Code.Length > 5 || !Regex.IsMatch(Code, "^[A-Z0-9]+$"))
        {
            ErrorMessage = "项目代号必须为 2~5 位英文字母或数字（例如 PRJ, APS, M01）。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "请输入项目全称或描述。";
            return;
        }

        string? template = IsCloneMode ? SelectedTemplate : null;
        var (ok, msg) = await _projectService.CreateProjectAsync(Code, Name, RootDir, template);

        if (!ok)
        {
            ErrorMessage = msg;
        }
        else
        {
            IsSuccess = true;
        }
    }
}
