using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>One entry of the resolution-chain view.</summary>
public sealed record ChainEntry(int Index, string Variable, string Source, string Path);

/// <summary>Plug-ins page: scan, enable/disable, index, conflicts, resolution chain, hot-load macro, create/import.</summary>
public partial class PluginsViewModel : ObservableObject
{
    private readonly PluginService _plugins;
    private readonly PluginDiscoveryService _discovery;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly IProjectCatalog _catalog;

    private List<PluginInfo> _all = new();

    public ObservableCollection<PluginInfo> Items { get; } = new();
    public ObservableCollection<PluginConflict> Conflicts { get; } = new();
    public ObservableCollection<ChainEntry> Chain { get; } = new();
    public ObservableCollection<DiscoveredPluginInfo> DiscoveredItems { get; } = new();

    [ObservableProperty] private string _pluginsDir = string.Empty;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand))] private bool _isBusy;
    [ObservableProperty] private bool _isScanningSystem;
    [ObservableProperty] private string _statusMessage = Strings.Common_Ready;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _discoveredCount;
    [ObservableProperty] private int _unmanagedCount;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private bool _isEmpty;
    /// <summary>"list" | "conflicts" | "chain" | "macro" | "discovery"</summary>
    [ObservableProperty] private string _panel = "list";
    [ObservableProperty] private string _macroText = string.Empty;
    [ObservableProperty] private string _macroPath = string.Empty;
    [ObservableProperty] private int _conflictCount;

    public bool ShowList => Panel == "list";
    public bool ShowConflicts => Panel == "conflicts";
    public bool ShowChain => Panel == "chain";
    public bool ShowMacro => Panel == "macro";
    public bool ShowDiscovery => Panel == "discovery";

    public PluginsViewModel(
        PluginService plugins,
        PluginDiscoveryService discovery,
        IDialogService dialogs,
        ToastService toasts,
        IProjectCatalog catalog)
    {
        _plugins = plugins;
        _discovery = discovery;
        _dialogs = dialogs;
        _toasts = toasts;
        _catalog = catalog;
        PluginsDir = plugins.PluginsDir;
        _plugins.PluginsDirectoryChanged += OnPluginsDirectoryChanged;
        _ = RefreshAsync();
    }

    private void OnPluginsDirectoryChanged()
    {
        if (Application.Current?.Dispatcher is { } d && !d.HasShutdownStarted)
        {
            d.BeginInvoke(async () =>
            {
                if (!IsBusy) await RefreshAsync();
            });
        }
    }

    partial void OnPanelChanged(string value)
    {
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowConflicts));
        OnPropertyChanged(nameof(ShowChain));
        OnPropertyChanged(nameof(ShowMacro));
        OnPropertyChanged(nameof(ShowDiscovery));
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        SetStatus(Strings.Plugins_Scanning, false);
        try
        {
            PluginsDir = _plugins.PluginsDir;
            var expanded = new HashSet<string>(_all.Where(p => p.IsExpanded).Select(p => p.Name));
            _all = await _plugins.ScanAsync();
            foreach (var p in _all) p.IsExpanded = expanded.Contains(p.Name);
            Total = _all.Count;
            EnabledCount = _all.Count(p => p.Enabled);
            HasLoaded = true;
            IsEmpty = _all.Count == 0;
            ApplyFilter();
            RefreshAnalysis();
            SetStatus(string.Format(Strings.Plugins_ScanDone, Total, EnabledCount, PluginsDir), false);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(Strings.Plugins_ScanFailed, ex.Message), true);
        }
        finally { IsBusy = false; }
    }

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        var wanted = _all.Where(p => f.Length == 0
            || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || p.AllSymbols.Any(s => s.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || s.File.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (wanted.SequenceEqual(Items)) return;
        Items.Clear();
        foreach (var p in wanted) Items.Add(p);
    }

    private void RefreshAnalysis()
    {
        Conflicts.Clear();
        foreach (var c in PluginService.DetectConflicts(_all)) Conflicts.Add(c);
        ConflictCount = Conflicts.Count;
        Chain.Clear();
        int i = 1;
        foreach (var (variable, source, path) in _plugins.ResolutionChain(_all)) Chain.Add(new ChainEntry(i++, variable, source, path));
        var (macroPath, content) = _plugins.GenerateHotloadMacro(_all);
        MacroPath = macroPath;
        MacroText = content;
    }

    private void SetStatus(string text, bool error) { StatusMessage = text; StatusIsError = error; }

    [RelayCommand]
    private async Task ToggleAsync(PluginInfo? plugin)
    {
        if (plugin == null) return;
        bool target = !plugin.Enabled;
        plugin.IsBusy = true;
        var res = await _plugins.SetEnabledAsync(plugin.Name, target);
        plugin.IsBusy = false;
        _toasts.Result(res.Ok, res.Message);
        if (res.Ok)
        {
            plugin.Enabled = target;
            EnabledCount = _all.Count(p => p.Enabled);
            RefreshAnalysis();
        }
        else
        {
            // the switch already flipped on screen; push the real value through the binding again
            plugin.Enabled = target;
            plugin.Enabled = !target;
        }
    }

    [RelayCommand]
    private async Task SetAllAsync(bool enable)
    {
        IsBusy = true;
        var res = await _plugins.SetAllEnabledAsync(_all, enable);
        IsBusy = false;
        _toasts.Result(res.Ok, res.Message);
        await RefreshAsync();
    }

    [RelayCommand]
    private void ToggleExpanded(PluginInfo? plugin)
    {
        if (plugin != null) plugin.IsExpanded = !plugin.IsExpanded;
    }

    [RelayCommand]
    private async Task RebuildIndexAsync(PluginInfo? plugin)
    {
        if (plugin?.PmlLibPath == null) return;
        plugin.IsBusy = true;
        var (ok, msg) = await Task.Run(() => _plugins.RebuildIndex(plugin.PmlLibPath));
        plugin.IsBusy = false;
        _toasts.Result(ok, $"{plugin.Name}: {msg}");
        if (ok) await RefreshAsync();
    }

    [RelayCommand]
    private async Task RebuildAllIndexesAsync()
    {
        IsBusy = true;
        var res = await _plugins.RebuildAllIndexesAsync(_all);
        IsBusy = false;
        _toasts.Result(res.Ok, res.Message);
        await RefreshAsync();
    }

    [RelayCommand]
    private void ShowPanel(string panel) => Panel = panel;

    [RelayCommand]
    private void CopyMacro()
    {
        try { Clipboard.SetText(MacroText); _toasts.Success(Strings.Plugins_MacroCopied); }
        catch (Exception ex) { _toasts.Error(ex.Message); }
    }

    [RelayCommand]
    private void CopyRunCommand()
    {
        try { Clipboard.SetText($"$m {MacroPath}"); _toasts.Success(Strings.Plugins_RunCopied); }
        catch (Exception ex) { _toasts.Error(ex.Message); }
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); _toasts.Success(string.Format(Strings.Project_PathCopied, text)); }
        catch (Exception ex) { _toasts.Error(ex.Message); }
    }

    [RelayCommand]
    private void OpenFolder(PluginInfo? plugin) => _plugins.OpenFolder(plugin?.Path);

    [RelayCommand]
    private async Task EditDisplayNameAsync(PluginInfo? plugin)
    {
        if (plugin == null) return;
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = Strings.Plugins_EditDisplayNameTitle,
            Message = Strings.Plugins_EditDisplayNameBody,
            TextLabel = Strings.Plugins_EditDisplayNameLabel,
            TextInitial = plugin.DisplayName,
            TextPlaceholder = plugin.EffectiveDisplayName,
            TextMaxLength = 50,
            OkLabel = Strings.Common_Save,
        });
        if (res == null) return;

        string trimmed = res.Text?.Trim() ?? string.Empty;
        _catalog.SetPluginDisplayName(plugin.Name, string.IsNullOrEmpty(trimmed) ? null : trimmed);
        plugin.DisplayName = trimmed;
        _plugins.SyncE3dRibbon();
        _toasts.Success(Strings.Plugins_DisplayNameUpdated);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = Strings.Plugins_NewTitle,
            Message = Strings.Plugins_NewBody,
            TextLabel = Strings.Plugins_NewNameLabel,
            TextPlaceholder = "MyPlugin",
            TextMaxLength = 40,
            OkLabel = Strings.Plugins_NewConfirm,
        });
        if (res == null) return;
        var (ok, msg, _) = await _plugins.CreateSkeletonAsync(res.Text, pmllib: true, pmlnet: true, pmlui: false);
        _toasts.Result(ok, msg);
        if (ok) await RefreshAsync();
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        string? folder = _dialogs.PickFolder(Strings.Plugins_ImportFolderTitle);
        if (folder == null) return;
        await ImportAsync(folder);
    }

    [RelayCommand]
    private async Task ImportZipAsync()
    {
        string? file = _dialogs.PickFile(Strings.Plugins_ImportZipTitle, "Zip archives (*.zip)|*.zip|All files (*.*)|*.*");
        if (file == null) return;
        await ImportAsync(file);
    }

    private async Task ImportAsync(string source)
    {
        IsBusy = true;
        var (ok, msg, name) = await _plugins.ImportAsync(source);
        IsBusy = false;
        _toasts.Result(ok, msg);
        if (ok && name != null)
        {
            await _plugins.SetEnabledAsync(name, true);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ChangeDirAsync()
    {
        string? folder = _dialogs.PickFolder(Strings.Plugins_PickDirTitle, PluginsDir);
        if (folder == null) return;
        _plugins.SetPluginsDir(folder);
        PluginsDir = _plugins.PluginsDir;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UninstallAsync(PluginInfo? plugin)
    {
        if (plugin == null) return;
        bool confirmed = await _dialogs.ConfirmAsync(
            Strings.Plugins_UninstallConfirmTitle,
            string.Format(Strings.Plugins_UninstallConfirmBody, plugin.EffectiveDisplayName),
            Strings.Plugins_Uninstall,
            danger: true
        );
        if (!confirmed) return;

        plugin.IsBusy = true;
        var res = await _plugins.UninstallPluginAsync(plugin.Name);
        plugin.IsBusy = false;
        _toasts.Result(res.Ok, res.Message);
        if (res.Ok)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ScanSystemPluginsAsync()
    {
        if (IsScanningSystem) return;
        IsScanningSystem = true;
        Panel = "discovery";
        SetStatus(Strings.Plugins_ScanningSystem, false);
        try
        {
            var progress = new Progress<(string Path, int Count)>(p =>
            {
                StatusMessage = $"{Strings.Plugins_ScanningSystem} ({p.Count}) {System.IO.Path.GetFileName(p.Path)}";
            });

            var discovered = await _discovery.ScanSystemPluginsAsync(progress);
            DiscoveredItems.Clear();
            foreach (var item in discovered)
            {
                DiscoveredItems.Add(item);
            }
            DiscoveredCount = DiscoveredItems.Count;
            UnmanagedCount = DiscoveredItems.Count(d => !d.IsAlreadyManaged);
            SetStatus(string.Format(Strings.Plugins_ScanSystemDone, DiscoveredCount, DiscoveredCount - UnmanagedCount, UnmanagedCount), false);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(Strings.Plugins_ScanFailed, ex.Message), true);
        }
        finally
        {
            IsScanningSystem = false;
        }
    }

    [RelayCommand]
    private async Task HostDiscoveredAsync(DiscoveredPluginInfo? item)
    {
        if (item == null) return;
        IsBusy = true;
        var res = await _discovery.HostIntoSepAsync(item, enableImmediately: false);
        IsBusy = false;
        _toasts.Result(res.Ok, res.Message);
        if (res.Ok)
        {
            await RefreshAsync();
            await ScanSystemPluginsAsync();
        }
    }

    [RelayCommand]
    private async Task HostAndEnableAsync(DiscoveredPluginInfo? item)
    {
        if (item == null) return;
        IsBusy = true;
        var res = await _discovery.HostIntoSepAsync(item, enableImmediately: true);
        IsBusy = false;
        _toasts.Result(res.Ok, res.Message);
        if (res.Ok)
        {
            await RefreshAsync();
            await ScanSystemPluginsAsync();
        }
    }

    [RelayCommand]
    private void OpenDiscoveredFolder(DiscoveredPluginInfo? item)
    {
        if (item?.Path != null) _plugins.OpenFolder(item.Path);
    }
}
