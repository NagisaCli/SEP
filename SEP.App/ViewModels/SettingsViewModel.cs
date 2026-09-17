using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>An entry of the language / theme pickers; Display is observable so captions can follow the language.</summary>
public partial class ChoiceOption : ObservableObject
{
    public string Code { get; }

    [ObservableProperty]
    private string _display;

    public ChoiceOption(string code, string display)
    {
        Code = code;
        _display = display;
    }

    /// <summary>Accessibility name of the item (UI Automation reads ToString() for unrealized items).</summary>
    public override string ToString() => Display;
}

/// <summary>One drive / folder probed by the device-paths panel.</summary>
public sealed record DevicePath(string Label, string Path, bool Exists)
{
    public string StatusText => Exists ? Strings.Settings_PathFound : Strings.Settings_PathMissing;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly SepDataStore _store;
    private readonly ThemeService _themeService;
    private readonly PluginService _plugins;
    private readonly E3dToolsService _tools;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ChoiceOption _autoLanguage = new(Loc.Auto, Strings.Settings_LanguageAuto);
    private readonly ChoiceOption _autoTheme = new(ThemeService.Auto, Strings.Settings_ThemeAuto);
    private readonly ChoiceOption _darkTheme = new(ThemeService.Dark, Strings.Settings_ThemeDark);
    private readonly ChoiceOption _lightTheme = new(ThemeService.Light, Strings.Settings_ThemeLight);
    private readonly bool _initialized;

    [ObservableProperty] private string _installDir = string.Empty;
    [ObservableProperty] private string _projectsDir = string.Empty;
    [ObservableProperty] private string _evarsBat = string.Empty;
    [ObservableProperty] private string _e3dVersion = string.Empty;
    /// <summary>settings.local_projects_dir — the library whose custom_evars.bat receives single-project launches.</summary>
    [ObservableProperty] private string _localProjectsDir = string.Empty;
    /// <summary>settings.e3d_lnk — explicit shortcut; empty = find automatically.</summary>
    [ObservableProperty] private string _e3dLnk = string.Empty;
    [ObservableProperty] private string _pluginsDir = string.Empty;
    [ObservableProperty] private bool _autoStart;
    /// <summary>"auto", "en" or "zh-CN"; applied and persisted immediately when changed.</summary>
    [ObservableProperty] private string _language = Loc.Auto;
    [ObservableProperty] private string _theme = ThemeService.Auto;
    [ObservableProperty] private string _statusMessage = Strings.Common_Ready;
    [ObservableProperty] private string _dataDir = string.Empty;
    [ObservableProperty] private bool _isPortable;
    [ObservableProperty] private string _versionText = string.Empty;

    public ObservableCollection<ChoiceOption> LanguageOptions { get; }
    public ObservableCollection<ChoiceOption> ThemeOptions { get; }
    public ObservableCollection<DevicePath> DevicePaths { get; } = new();

    public SettingsViewModel(IProjectCatalog catalog, SepDataStore store, ThemeService theme, PluginService plugins, E3dToolsService tools, IDialogService dialogs, ToastService toasts)
    {
        _catalog = catalog;
        _store = store;
        _themeService = theme;
        _plugins = plugins;
        _tools = tools;
        _dialogs = dialogs;
        _toasts = toasts;

        var paths = _catalog.Paths;
        InstallDir = paths.InstallDir ?? string.Empty;
        ProjectsDir = paths.ProjectsDir ?? string.Empty;
        EvarsBat = paths.EvarsBat ?? string.Empty;
        E3dVersion = paths.E3dVersion ?? "AVEVA Everything3D 3.1";

        var settings = _catalog.Data.Settings;
        LocalProjectsDir = settings.LocalProjectsDir;
        E3dLnk = settings.E3dLnk;
        PluginsDir = settings.PluginsDir ?? string.Empty;
        AutoStart = settings.AutoStart;
        DataDir = store.DataDir;
        IsPortable = string.Equals(store.DataDir, SepDataStore.ExeDir, StringComparison.OrdinalIgnoreCase);
        VersionText = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0";

        // Native names on purpose: a user who cannot read the current language must still find their own.
        LanguageOptions = new ObservableCollection<ChoiceOption>
        {
            _autoLanguage,
            new(Loc.English, "English"),
            new(Loc.ChineseSimplified, "中文（简体）"),
        };
        ThemeOptions = new ObservableCollection<ChoiceOption> { _autoTheme, _darkTheme, _lightTheme };
        Language = Loc.Normalize(settings.Language);
        Theme = ThemeService.Normalize(settings.Theme);
        RefreshDevicePaths();
        _initialized = true;
    }

    partial void OnLanguageChanged(string value)
    {
        if (!_initialized || string.IsNullOrEmpty(value)) return;
        Loc.Instance.Apply(value);
        _autoLanguage.Display = Strings.Settings_LanguageAuto;
        _autoTheme.Display = Strings.Settings_ThemeAuto;
        _darkTheme.Display = Strings.Settings_ThemeDark;
        _lightTheme.Display = Strings.Settings_ThemeLight;
        StatusMessage = Strings.Common_Ready;
        _catalog.Data.Settings.Language = value;
        _catalog.Save();
        RefreshDevicePaths();
    }

    partial void OnThemeChanged(string value)
    {
        if (!_initialized || string.IsNullOrEmpty(value)) return;
        _themeService.Apply(value);
        _catalog.Data.Settings.Theme = value;
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
            string init = Path.Combine(Path.GetDirectoryName(paths.EvarsBat) ?? string.Empty, "evars.init");
            if (string.IsNullOrEmpty(paths.EvarsInit) || !File.Exists(paths.EvarsInit)) paths.EvarsInit = init;
        }

        var settings = _catalog.Data.Settings;
        settings.LocalProjectsDir = SepPaths.Normalize(LocalProjectsDir);
        settings.E3dLnk = SepPaths.Normalize(E3dLnk);
        settings.PluginsDir = SepPaths.Normalize(PluginsDir) is { Length: > 0 } pd ? pd : null;
        settings.AutoStart = AutoStart;
        settings.Language = Language;
        settings.Theme = Theme;

        _catalog.Save();
        ApplyAutoStart(AutoStart);
        StatusMessage = Strings.Settings_Saved;
        _toasts.Success(Strings.Settings_Saved);
        RefreshDevicePaths();
    }

    /// <summary>Start-with-Windows through the per-user Run key (no elevation needed).</summary>
    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            string exe = Environment.ProcessPath ?? string.Empty;
            if (enable && exe.Length > 0) key.SetValue("SEP", $"\"{exe}\"");
            else key.DeleteValue("SEP", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            App.Log($"autostart: {ex.Message}");
        }
    }

    [RelayCommand]
    private void BrowseInstallDir() { var f = _dialogs.PickFolder(Strings.Settings_InstallDirLabel, InstallDir); if (f != null) InstallDir = f; }

    [RelayCommand]
    private void BrowseProjectsDir() { var f = _dialogs.PickFolder(Strings.Settings_ProjectsDirLabel, ProjectsDir); if (f != null) ProjectsDir = f; }

    [RelayCommand]
    private void BrowseLocalProjectsDir() { var f = _dialogs.PickFolder(Strings.Settings_LocalProjectsDirLabel, LocalProjectsDir); if (f != null) LocalProjectsDir = f; }

    [RelayCommand]
    private void BrowsePluginsDir() { var f = _dialogs.PickFolder(Strings.Settings_PluginsDirLabel, PluginsDir); if (f != null) PluginsDir = f; }

    [RelayCommand]
    private void BrowseEvarsBat() { var f = _dialogs.PickFile(Strings.Settings_EvarsLabel, "evars.bat|evars.bat|Batch files (*.bat)|*.bat", Path.GetDirectoryName(EvarsBat)); if (f != null) EvarsBat = f; }

    [RelayCommand]
    private void BrowseLnk() { var f = _dialogs.PickFile(Strings.Settings_LnkLabel, "Shortcuts (*.lnk)|*.lnk"); if (f != null) E3dLnk = f; }

    [RelayCommand]
    private void OpenDataDir()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _toasts.Error(ex.Message); }
    }

    /// <summary>Same probe the web UI's "device paths" panel did: which of the usual folders exist on this machine.</summary>
    [RelayCommand]
    private void RefreshDevicePaths()
    {
        DevicePaths.Clear();
        void Add(string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            DevicePaths.Add(new DevicePath(label, path, Directory.Exists(path) || File.Exists(path)));
        }
        Add(Strings.Settings_InstallDirLabel, _tools.InstallDir);
        Add(Strings.Settings_EvarsLabel, _tools.EvarsBatPath);
        Add("evars.init", _tools.EvarsInitPath);
        Add(Strings.Settings_LocalProjectsDirLabel, _catalog.LocalProjectsDir);
        Add("custom_evars.bat", _tools.CustomEvarsPath);
        Add(Strings.Settings_PluginsDirLabel, _plugins.PluginsDir);
        Add("USERDATA", _tools.UserDataDir);
        Add(Strings.Settings_LnkLabel, string.IsNullOrWhiteSpace(E3dLnk) ? null : E3dLnk);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string aveva = Path.Combine(d.RootDirectory.FullName, "AVEVA");
            if (Directory.Exists(aveva)) Add(string.Format(Strings.Settings_DriveLabel, d.Name.TrimEnd('\\')), aveva);
        }
    }

    // ── config bundle export / import ────────────────────────────────────────────

    private static readonly JsonSerializerOptions BundleJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class Bundle
    {
        [JsonPropertyName("sep_bundle_version")] public int Version { get; set; } = 1;
        [JsonPropertyName("exported_at")] public string? ExportedAt { get; set; }
        [JsonPropertyName("source_device")] public string? SourceDevice { get; set; }
        [JsonPropertyName("data")] public SepData? Data { get; set; }
        [JsonPropertyName("paths")] public E3dPathsConfig? Paths { get; set; }
    }

    [RelayCommand]
    private void ExportBundle()
    {
        string? file = _dialogs.PickSaveFile(Strings.Settings_ExportTitle, "SEP bundle (*.json)|*.json", $"sep_bundle_{Environment.MachineName}_{DateTime.Now:yyyyMMdd}.json");
        if (file == null) return;
        try
        {
            var bundle = new Bundle { ExportedAt = SepPaths.NowIso(), SourceDevice = Environment.MachineName, Data = _catalog.Data, Paths = _catalog.Paths };
            File.WriteAllText(file, JsonSerializer.Serialize(bundle, BundleJson), new UTF8Encoding(false));
            StatusMessage = string.Format(Strings.Settings_Exported, file);
            _toasts.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            _toasts.Error(string.Format(Strings.Settings_ExportFailed, ex.Message));
        }
    }

    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        string? file = _dialogs.PickFile(Strings.Settings_ImportTitle, "SEP bundle (*.json)|*.json|All files (*.*)|*.*");
        if (file == null) return;
        try
        {
            var bundle = JsonSerializer.Deserialize<Bundle>(File.ReadAllText(file, Encoding.UTF8), BundleJson);
            var data = bundle?.Data ?? JsonSerializer.Deserialize<SepData>(File.ReadAllText(file, Encoding.UTF8), BundleJson);
            if (data == null) throw new InvalidDataException(Strings.Settings_ImportInvalid);

            bool ok = await _dialogs.ConfirmAsync(Strings.Settings_ImportTitle,
                string.Format(Strings.Settings_ImportConfirm, data.Libraries.Count, data.MyProjects.Count, bundle?.SourceDevice ?? "?"), Strings.Settings_ImportButton, danger: true);
            if (!ok) return;

            // paths from another machine: drop the drive to one that exists here
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name.Substring(0, 2).ToUpperInvariant()).ToList();
            string pref = drives.Contains("D:") ? "D:" : drives.FirstOrDefault() ?? "C:";
            var notes = new System.Collections.Generic.List<string>();
            string? Remap(string? value, string label)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value[1] != ':') return value;
                string drive = value[..2].ToUpperInvariant();
                if (drives.Contains(drive)) return value;
                string mapped = pref + value[2..];
                notes.Add($"{label}: {value} → {mapped}");
                return mapped;
            }
            data.Settings.LocalProjectsDir = Remap(data.Settings.LocalProjectsDir, "local_projects_dir") ?? string.Empty;
            data.Settings.PluginsDir = Remap(data.Settings.PluginsDir, "plugins_dir");
            data.Settings.Language = Language;
            data.Settings.Theme = Theme;

            _catalog.ImportData(data);
            if (bundle?.Paths != null)
            {
                _catalog.Paths.InstallDir = bundle.Paths.InstallDir; _catalog.Paths.ProjectsDir = bundle.Paths.ProjectsDir;
                _catalog.Paths.EvarsBat = bundle.Paths.EvarsBat; _catalog.Paths.EvarsInit = bundle.Paths.EvarsInit; _catalog.Paths.E3dVersion = bundle.Paths.E3dVersion;
                _catalog.Save();
            }
            if (notes.Count > 0) _catalog.AddNotification("info", Strings.Settings_ImportRemappedTitle, string.Join("; ", notes));
            LocalProjectsDir = data.Settings.LocalProjectsDir;
            PluginsDir = data.Settings.PluginsDir ?? string.Empty;
            E3dLnk = data.Settings.E3dLnk;
            StatusMessage = string.Format(Strings.Settings_Imported, data.Libraries.Count, data.MyProjects.Count);
            _toasts.Success(StatusMessage);
            _ = _catalog.RescanAllAsync(force: true);
        }
        catch (Exception ex)
        {
            _toasts.Error(string.Format(Strings.Settings_ImportFailed, ex.Message));
        }
    }
}
