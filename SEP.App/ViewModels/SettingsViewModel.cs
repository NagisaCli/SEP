using System;
using System.Collections.ObjectModel;
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
    private readonly IProjectCatalog _catalog;
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

    /// <summary>settings.local_projects_dir — the library whose custom_evars.bat receives single-project launches.</summary>
    [ObservableProperty]
    private string _localProjectsDir = string.Empty;

    /// <summary>settings.e3d_lnk — explicit shortcut; empty = find automatically.</summary>
    [ObservableProperty]
    private string _e3dLnk = string.Empty;

    [ObservableProperty]
    private bool _autoStart;

    /// <summary>"auto", "en" or "zh-CN"; applied and persisted immediately when changed.</summary>
    [ObservableProperty]
    private string _language = Loc.Auto;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    public ObservableCollection<LanguageOption> LanguageOptions { get; }

    public SettingsViewModel(IProjectCatalog catalog)
    {
        _catalog = catalog;

        var paths = _catalog.Paths;
        InstallDir = paths.InstallDir ?? string.Empty;
        ProjectsDir = paths.ProjectsDir ?? string.Empty;
        EvarsBat = paths.EvarsBat ?? string.Empty;
        E3dVersion = paths.E3dVersion ?? "AVEVA Everything3D 3.1";

        var settings = _catalog.Data.Settings;
        LocalProjectsDir = settings.LocalProjectsDir;
        E3dLnk = settings.E3dLnk;
        AutoStart = settings.AutoStart;

        // Native names on purpose: a user who cannot read the current language must still find their own.
        LanguageOptions = new ObservableCollection<LanguageOption>
        {
            _autoOption,
            new(Loc.English, "English"),
            new(Loc.ChineseSimplified, "中文（简体）"),
        };
        Language = Loc.Normalize(settings.Language);
        _initialized = true;
    }

    partial void OnLanguageChanged(string value)
    {
        if (!_initialized || string.IsNullOrEmpty(value)) return;

        Loc.Instance.Apply(value);
        _autoOption.Display = Strings.Settings_LanguageAuto;
        StatusMessage = Strings.Common_Ready;

        _catalog.Data.Settings.Language = value;
        _catalog.Save();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var paths = _catalog.Paths;
        paths.InstallDir = InstallDir.Trim();
        paths.ProjectsDir = ProjectsDir.Trim();
        paths.EvarsBat = EvarsBat.Trim();
        paths.E3dVersion = E3dVersion.Trim();
        // evars.init lives next to evars.bat; keep it in step when the user changes the bat path
        if (!string.IsNullOrEmpty(paths.EvarsBat))
        {
            string init = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(paths.EvarsBat) ?? string.Empty, "evars.init");
            if (string.IsNullOrEmpty(paths.EvarsInit) || !System.IO.File.Exists(paths.EvarsInit)) paths.EvarsInit = init;
        }

        var settings = _catalog.Data.Settings;
        settings.LocalProjectsDir = SepPaths.Normalize(LocalProjectsDir);
        settings.E3dLnk = SepPaths.Normalize(E3dLnk);
        settings.AutoStart = AutoStart;
        settings.Language = Language;

        _catalog.Save();
        StatusMessage = Strings.Settings_Saved;
    }
}
