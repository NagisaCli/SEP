using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

/// <summary>Structural classification of a plugin folder.</summary>
public enum PluginStructureType
{
    Standard,     // Standard pmllib, pdmsui/pmlui, bin
    Flat,         // Flat PML files directly at plugin root
    MultiVersion, // Subfolders for E3D3.1, E3D2.1, 120sp4, etc.
    NestedPackage,// Nested directory package (unzipped archive)
    AddinOnly,    // .NET Addin / DLL only
    FunctionOnly  // Pure functions/macros without form
}

/// <summary>External or unmanaged plugin discovered on local disks.</summary>
public sealed record DiscoveredPluginInfo(
    string Name,
    string Path,
    PluginStructureType StructureType,
    string LocationType,
    int FileCount,
    bool HasPml,
    bool HasDll,
    bool HasUic,
    bool HasIndex,
    bool IsAlreadyManaged,
    string KeyFeatureDescription
);

/// <summary>One PML / PML.NET definition found in a plug-in folder.</summary>
public sealed record PluginSymbol(string Kind, string Name, string File, string Path, string? CallCommand);

/// <summary>Details of a .NET assembly discovered in a plug-in folder.</summary>
public sealed record PluginAssembly(string Name, string FullPath, bool IsAddin, bool IsPmlNet, string? PdbPath);

/// <summary>A plug-in folder under the plug-ins root (D:\AVEVA\Plugins by default), as e3d_plugin.py sees it.</summary>
public sealed partial class PluginInfo : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public PluginStructureType StructureType { get; init; } = PluginStructureType.Standard;
    public string? PreferredLauncher { get; init; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveDisplayName))]
    [NotifyPropertyChangedFor(nameof(HasCustomDisplayName))]
    private string _displayName = string.Empty;

    public string EffectiveDisplayName => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : FormatDefaultDisplayName(Name);

    public bool HasCustomDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Name, System.StringComparison.OrdinalIgnoreCase);

    public static string FormatDefaultDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        if (name.Equals("Cad2E3DAids", System.StringComparison.OrdinalIgnoreCase)) return "CAD2E3D Aids";
        if (name.Equals("NozzleMgr", System.StringComparison.OrdinalIgnoreCase)) return "Nozzle Manager";
        if (name.Equals("Pipline_Aid", System.StringComparison.OrdinalIgnoreCase)) return "Pipeline Aid";
        if (name.Equals("PIP_BOP", System.StringComparison.OrdinalIgnoreCase)) return "Pipe BOP";
        if (name.Equals("TrueColor", System.StringComparison.OrdinalIgnoreCase)) return "TrueColor";

        // Generic formatting for any plugin: split underscores/dashes and camelCase
        string cleaned = name.Replace('_', ' ').Replace('-', ' ').Trim();
        string spaced = System.Text.RegularExpressions.Regex.Replace(cleaned, @"([a-z0-9])([A-Z])", "$1 $2");
        return System.Text.RegularExpressions.Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    public string? PmlLibPath { get; init; }
    public string? PmlUiPath { get; init; }
    public string? PmlNetPath { get; init; }
    public string? DfltsPath { get; init; }
    public string? TargetVersion { get; init; }
    public string? ResolvedVersionDir { get; init; }
    public bool IsMultiVersion => !string.IsNullOrEmpty(ResolvedVersionDir);

    public bool HasPmlLib => PmlLibPath != null;
    public bool HasPmlUi => PmlUiPath != null;
    public bool HasPmlNet => PmlNetPath != null;
    public bool HasDflts => DfltsPath != null;
    public bool HasUic => UicCount > 0;
    public bool HasAddin => AddinAssemblies.Count > 0;
    public List<PluginAssembly> AddinAssemblies { get; init; } = new();

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
