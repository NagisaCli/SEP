using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>An entry of the language picker; Display is observable so the "Auto" caption can follow the language.</summary>
public partial class LanguageOption : ObservableObject
{
    public string Code { get; }

    [ObservableProperty]
    private string _display;

    public LanguageOption(string code, string display)
    {
        Code = code;
        _display = display;
    }

    /// <summary>Accessibility name of the item (UI Automation reads ToString() for unrealized items).</summary>
    public override string ToString() => Display;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IE3dProjectService _projectService;
    private readonly LanguageOption _autoOption = new(Loc.Auto, Strings.Settings_LanguageAuto);
    private readonly bool _initialized;

    [ObservableProperty]
    private string _installDir = string.Empty;

    [ObservableProperty]
    private string _projectsDir = string.Empty;

    [ObservableProperty]
    private string _evarsBat = string.Empty;

    [ObservableProperty]
    private string _e3dVersion = string.Empty;

    [ObservableProperty]
    private bool _autoStart;

    /// <summary>"auto", "en" or "zh-CN"; applied and persisted immediately when changed.</summary>
    [ObservableProperty]
    private string _language = Loc.Auto;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    public ObservableCollection<LanguageOption> LanguageOptions { get; }

    public SettingsViewModel(IE3dProjectService projectService)
    {
        _projectService = projectService;

        var paths = _projectService.PathsConfig;
        InstallDir = paths.InstallDir ?? string.Empty;
        ProjectsDir = paths.ProjectsDir ?? string.Empty;
        EvarsBat = paths.EvarsBat ?? string.Empty;
        E3dVersion = paths.E3dVersion ?? "AVEVA Everything3D 3.1";

        AutoStart = _projectService.ProjectsConfig.Settings.AutoStart;

        // Native names on purpose: a user who cannot read the current language must still find their own.
        LanguageOptions = new ObservableCollection<LanguageOption>
        {
            _autoOption,
            new(Loc.English, "English"),
            new(Loc.ChineseSimplified, "中文（简体）"),
        };
        Language = Loc.Normalize(_projectService.ProjectsConfig.Settings.Language);
        _initialized = true;
    }

    partial void OnLanguageChanged(string value)
    {
        if (!_initialized || string.IsNullOrEmpty(value)) return;

        Loc.Instance.Apply(value);
        _autoOption.Display = Strings.Settings_LanguageAuto;
        StatusMessage = Strings.Common_Ready;

        _projectService.ProjectsConfig.Settings.Language = value;
        _ = _projectService.SaveConfigAsync();
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _projectService.PathsConfig.InstallDir = InstallDir.Trim();
        _projectService.PathsConfig.ProjectsDir = ProjectsDir.Trim();
        _projectService.PathsConfig.EvarsBat = EvarsBat.Trim();
        _projectService.PathsConfig.E3dVersion = E3dVersion.Trim();

        _projectService.ProjectsConfig.Settings.AutoStart = AutoStart;
        _projectService.ProjectsConfig.Settings.Language = Language;

        await _projectService.SaveConfigAsync();
        StatusMessage = Strings.Settings_Saved;
    }
}
