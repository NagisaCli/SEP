using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

/// <summary>One PML / PML.NET definition found in a plug-in folder.</summary>
public sealed record PluginSymbol(string Kind, string Name, string File, string Path, string? CallCommand);

/// <summary>A plug-in folder under the plug-ins root (D:\AVEVA\Plugins by default), as e3d_plugin.py sees it.</summary>
public sealed partial class PluginInfo : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isExpanded;

    public string? PmlLibPath { get; init; }
    public string? PmlUiPath { get; init; }
    public string? PmlNetPath { get; init; }
    public string? DfltsPath { get; init; }
    public bool HasPmlLib => PmlLibPath != null;
    public bool HasPmlUi => PmlUiPath != null;
    public bool HasPmlNet => PmlNetPath != null;
    public bool HasDflts => DfltsPath != null;

    /// <summary>"ok", "missing", "outdated", "error" or "none" (no pmllib).</summary>
    public string PmlIndexStatus { get; init; } = "none";
    public int PmlIndexCount { get; init; }
    public int PmlFileCount { get; init; }
    public bool IndexNeedsRebuild => PmlIndexStatus is "missing" or "outdated" or "error";

    public List<PluginSymbol> Forms { get; init; } = new();
    public List<PluginSymbol> Objects { get; init; } = new();
    public List<PluginSymbol> Functions { get; init; } = new();
    public List<PluginSymbol> Macros { get; init; } = new();
    public List<PluginSymbol> Assemblies { get; init; } = new();
    public List<PluginSymbol> UicConfigs { get; init; } = new();
    public List<string> Diagnostics { get; init; } = new();
    public List<string> EntryCommands { get; init; } = new();
    public List<string> HotloadCommands { get; init; } = new();

    public int FormCount => Forms.Count;
    public int ObjectCount => Objects.Count;
    public int FunctionCount => Functions.Count;
    public int MacroCount => Macros.Count;
    public int AssemblyCount => Assemblies.Count;
    public int UicCount => UicConfigs.Count;
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public bool HasEntryCommands => EntryCommands.Count > 0;
    public string EntryCommandsText => string.Join("\n", EntryCommands);
    public IEnumerable<PluginSymbol> AllSymbols => Forms.Concat(Objects).Concat(Functions).Concat(Macros).Concat(Assemblies).Concat(UicConfigs);
    public string Initials => Name.Length <= 2 ? Name.ToUpperInvariant() : Name[..2].ToUpperInvariant();
}

/// <summary>Two enabled plug-ins define the same form / object / function / assembly: E3D keeps the first on PMLLIB.</summary>
public sealed record PluginConflict(string Symbol, string Kind, IReadOnlyList<string> Plugins)
{
    public string PluginsText => string.Join(", ", Plugins);
    public string Winner => Plugins.Count > 0 ? Plugins[0] : string.Empty;
}
