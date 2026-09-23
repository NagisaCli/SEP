using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

/// <summary>
/// System-wide / full-disk E3D plugin detection engine.
/// Discovers unmanaged or scattered third-party plugins across drives, user profiles,
/// project folders and E3D directories, and provides one-click hosting into SEP.
/// </summary>
public sealed class PluginDiscoveryService
{
    private static readonly HashSet<string> PmlExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pmlfrm", ".pmlobj", ".pmlfnc", ".pmlcmd", ".pmlmac"
    };

    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin", "system volume information", "windows", "perflogs", "programdata",
        "msocache", "$windows.~bt", "$winre_backup_partition.marker", "recovery",
        ".git", ".svn", ".vs", ".idea", "node_modules", "packages", "obj", "bin",
        "temp", "tmp", "appdata\\local\\temp"
    };

    // Official AVEVA core distribution subdirectories that should never be marked as third-party plugins
    private static readonly HashSet<string> AvevaCorePmlDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "design", "equipment", "piping", "structures", "structural", "draw", "draft",
        "isodraft", "schematics", "lexicon", "admin", "monitor", "spooler", "common",
        "diagrams", "reviewinterface", "vnet", "tags", "asl", "assembly", "hulldesign",
        "marinedrafting", "paragon", "router", "smm", "viewer3d", "global", "accommodation",
        "designreuse", "isometricadp"
    };

    private readonly PluginService _pluginService;
    private readonly IProjectCatalog _catalog;

    public PluginDiscoveryService(PluginService pluginService, IProjectCatalog catalog)
    {
        _pluginService = pluginService;
        _catalog = catalog;
    }

    /// <summary>
    /// Programmatically scans common locations and fixed drives for E3D plugins.
    /// </summary>
    public async Task<List<DiscoveredPluginInfo>> ScanSystemPluginsAsync(
        IProgress<(string Path, int Count)>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new ConcurrentDictionary<string, DiscoveredPluginInfo>(StringComparer.OrdinalIgnoreCase);
            string managedRoot = SepPaths.Normalize(_pluginService.PluginsDir);
            string installDir = SepPaths.Normalize(_catalog.Paths.InstallDir);

            // 1. Gather seed candidate search roots
            var searchRoots = new List<(string Root, string LocationType, int MaxDepth)>();

            // Current managed plugins dir (for baseline comparison)
            if (Directory.Exists(managedRoot))
                searchRoots.Add((managedRoot, "SEP 托管目录", 2));

            // User profile locations (Desktop, Downloads, Documents)
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (Directory.Exists(desktop)) searchRoots.Add((desktop, "桌面", 3));

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads)) searchRoots.Add((downloads, "下载目录", 3));

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(documents)) searchRoots.Add((documents, "我的文档", 3));

            // Discovered external directories from custom_evars.bat
            foreach (var extDir in _pluginService.DiscoverPluginDirsFromCustomEvars())
            {
                if (Directory.Exists(extDir)) searchRoots.Add((extDir, "环境变量引用", 2));
            }

            // Active Project libraries
            foreach (var lib in _catalog.Data.Libraries)
            {
                if (!string.IsNullOrWhiteSpace(lib.Path) && Directory.Exists(lib.Path))
                {
                    searchRoots.Add((lib.Path, "工程库目录", 3));
                }
            }

            // Common AVEVA default drive locations
            foreach (var cand in new[] { @"D:\AVEVA", @"C:\AVEVA", @"E:\AVEVA", @"D:\Plugins", @"C:\Plugins", @"D:\E3D_Plugins", @"D:\PDMS_Plugins" })
            {
                if (Directory.Exists(cand) && !cand.Equals(managedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    searchRoots.Add((cand, "磁盘插件目录", 3));
                }
            }

            // Fixed Drive Roots (scan targeted plugin-like folders at level 1 and 2)
            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    string driveRoot = drive.RootDirectory.FullName;
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(driveRoot))
                        {
                            string fn = Path.GetFileName(sub).ToLowerInvariant();
                            if (SkipDirNames.Contains(fn)) continue;
                            if (fn.Contains("plugin") || fn.Contains("e3d") || fn.Contains("pml") || fn.Contains("aveva") || fn.Contains("tool"))
                            {
                                searchRoots.Add((sub, $"驱动器 ({drive.Name.TrimEnd('\\')})", 3));
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Deduplicate search roots
            var distinctRoots = searchRoots
                .GroupBy(r => SepPaths.Normalize(r.Root), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (root, locationType, maxDepth) in distinctRoots)
            {
                if (ct.IsCancellationRequested) break;
                ScanDirectoryRecursive(root, locationType, 0, maxDepth, managedRoot, installDir, visitedDirs, results, progress, ct);
            }

            return results.Values
                .OrderBy(p => p.IsAlreadyManaged ? 1 : 0) // Unmanaged first
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    private void ScanDirectoryRecursive(
        string currentDir,
        string locationType,
        int currentDepth,
        int maxDepth,
        string managedRoot,
        string installDir,
        HashSet<string> visitedDirs,
        ConcurrentDictionary<string, DiscoveredPluginInfo> results,
        IProgress<(string Path, int Count)>? progress,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        string normCurrent = SepPaths.Normalize(currentDir);
        if (!visitedDirs.Add(normCurrent)) return;

        string dirName = Path.GetFileName(normCurrent);
        if (string.IsNullOrEmpty(dirName) || SkipDirNames.Contains(dirName) || dirName.StartsWith('.')) return;

        // Skip official AVEVA installation core directory and its subdirectories entirely
        if (!string.IsNullOrEmpty(installDir) && normCurrent.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        progress?.Report((normCurrent, results.Count));

        // Evaluate if currentDir is a plugin candidate
        var candidate = EvaluatePluginDirectory(normCurrent, locationType, managedRoot, installDir);
        if (candidate != null)
        {
            results.TryAdd(candidate.Path, candidate);
            // If it is a self-contained plugin with standard layout, we don't need to descend further into its internal components
            if (candidate.StructureType is PluginStructureType.Standard or PluginStructureType.MultiVersion or PluginStructureType.Flat)
            {
                return;
            }
        }

        if (currentDepth >= maxDepth) return;

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(normCurrent))
            {
                if (ct.IsCancellationRequested) return;
                ScanDirectoryRecursive(sub, locationType, currentDepth + 1, maxDepth, managedRoot, installDir, visitedDirs, results, progress, ct);
            }
        }
        catch { }
    }

    /// <summary>
    /// Evaluates whether a directory contains the hallmarks of an AVEVA E3D / PDMS plugin.
    /// </summary>
    public static DiscoveredPluginInfo? EvaluatePluginDirectory(string dir, string locationType, string managedRoot, string? installDir)
    {
        if (!Directory.Exists(dir)) return null;

        string name = Path.GetFileName(dir);
        if (name.StartsWith('.') || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            return null;

        bool isAlreadyManaged = !string.IsNullOrEmpty(managedRoot) && dir.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase);

        // Check for official core root
        if (!string.IsNullOrEmpty(installDir) && dir.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            return null;

        bool hasIndex = File.Exists(Path.Combine(dir, "pml.index")) || File.Exists(Path.Combine(dir, "pmllib", "pml.index"));
        bool hasPmlSubdir = Directory.Exists(Path.Combine(dir, "pmllib")) || Directory.Exists(Path.Combine(dir, "pdmsui")) || Directory.Exists(Path.Combine(dir, "pmlui"));
        bool hasUic = false;
        bool hasPmlFiles = false;
        bool hasAvevaDll = false;
        int fileCount = 0;
        var pmlFormNames = new List<string>();
        var assemblyNames = new List<string>();

        // 1. Check top level files
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                fileCount++;
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (PmlExts.Contains(ext))
                {
                    hasPmlFiles = true;
                    if (ext == ".pmlfrm") pmlFormNames.Add(Path.GetFileNameWithoutExtension(file));
                }
                else if (ext == ".uic" || (ext == ".xml" && file.Contains("uic", StringComparison.OrdinalIgnoreCase)))
                {
                    hasUic = true;
                }
                else if (ext == ".dll")
                {
                    if (IsAvevaRelatedAssembly(file))
                    {
                        hasAvevaDll = true;
                        assemblyNames.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
        }
        catch { }

        // 2. Check subdirectories for version folders or nested structures
        bool isMultiVersion = false;
        bool isNested = false;
        string? nestedCoreDir = null;

        try
        {
            var subDirs = Directory.EnumerateDirectories(dir).ToList();
            foreach (var sub in subDirs)
            {
                string sName = Path.GetFileName(sub);
                string sLow = sName.ToLowerInvariant();

                // Multi-version candidate (e.g. E3D3.1, E3D2.1, 120sp4)
                if (Regex.IsMatch(sName, @"(?i)\b(?:E3D|120sp|121sp|PDMS)[\d\.]*\b"))
                {
                    isMultiVersion = true;
                }

                // Check for nested wrapper (same name or sub contains pmllib/app/TURE_COLOR)
                if (sName.Equals(name, StringComparison.OrdinalIgnoreCase) || sLow is "app" or "ture_color" or "true_color")
                {
                    isNested = true;
                    nestedCoreDir = sub;
                }

                // Count sub files
                if (sLow is "pmllib" or "bin" or "pdmsui" or "pmlui")
                {
                    try
                    {
                        foreach (var sf in Directory.EnumerateFiles(sub, "*.*", SearchOption.AllDirectories))
                        {
                            fileCount++;
                            string sext = Path.GetExtension(sf).ToLowerInvariant();
                            if (PmlExts.Contains(sext)) hasPmlFiles = true;
                            if (sext == ".uic") hasUic = true;
                            if (sext == ".dll" && IsAvevaRelatedAssembly(sf))
                            {
                                hasAvevaDll = true;
                                assemblyNames.Add(Path.GetFileNameWithoutExtension(sf));
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // If not enough signals to qualify as an E3D plugin, return null
        if (!hasIndex && !hasPmlSubdir && !hasPmlFiles && !hasAvevaDll && !hasUic && !isMultiVersion && !isNested)
        {
            return null;
        }

        // Determine structure type
        PluginStructureType structType;
        if (isNested)
            structType = PluginStructureType.NestedPackage;
        else if (isMultiVersion)
            structType = PluginStructureType.MultiVersion;
        else if (hasPmlSubdir)
            structType = PluginStructureType.Standard;
        else if (hasPmlFiles && !hasPmlSubdir)
            structType = PluginStructureType.Flat;
        else if (hasAvevaDll && !hasPmlFiles)
            structType = PluginStructureType.AddinOnly;
        else
            structType = PluginStructureType.Standard;

        // Build feature description
        var features = new List<string>();
        if (hasPmlFiles) features.Add(pmlFormNames.Count > 0 ? $"{pmlFormNames.Count} 个表单" : "PML 宏/函数");
        if (hasAvevaDll) features.Add($"程序集: {string.Join(", ", assemblyNames.Distinct().Take(2))}");
        if (hasUic) features.Add("自定义 UIC");
        if (isMultiVersion) features.Add("多版本支持");
        if (isNested) features.Add("多层嵌套分包");
        if (hasIndex) features.Add("含索引");

        string desc = features.Count > 0 ? string.Join(" | ", features) : "E3D 辅助组件";

        return new DiscoveredPluginInfo(
            Name: name,
            Path: dir,
            StructureType: structType,
            LocationType: locationType,
            FileCount: fileCount,
            HasPml: hasPmlFiles || hasPmlSubdir,
            HasDll: hasAvevaDll,
            HasUic: hasUic,
            HasIndex: hasIndex,
            IsAlreadyManaged: isAlreadyManaged,
            KeyFeatureDescription: desc
        );
    }

    /// <summary>
    /// Checks whether a .NET DLL has AVEVA references without loading it into process memory.
    /// </summary>
    public static bool IsAvevaRelatedAssembly(string dllPath)
    {
        string fn = Path.GetFileNameWithoutExtension(dllPath).ToLowerInvariant();
        if (fn.StartsWith("system.") || fn.StartsWith("microsoft.") || fn.StartsWith("mscorlib")) return false;

        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var reader = pe.GetMetadataReader();
            foreach (var h in reader.AssemblyReferences)
            {
                var aref = reader.GetAssemblyReference(h);
                string refName = reader.GetString(aref.Name);
                if (refName.StartsWith("Aveva.", StringComparison.OrdinalIgnoreCase) ||
                    refName.Equals("PMLNet", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// One-click hosts an external discovered plugin into SEP's managed Plugins directory.
    /// Normalizes nested folders, rebuilds pml.index, and optionally enables it immediately.
    /// </summary>
    public async Task<ToolResult> HostIntoSepAsync(DiscoveredPluginInfo discovered, bool enableImmediately)
    {
        return await Task.Run(async () =>
        {
            try
            {
                string root = _pluginService.PluginsDir;
                Directory.CreateDirectory(root);

                string safeName = Regex.Replace(discovered.Name, @"[^a-zA-Z0-9_\-\u4e00-\u9fa5]", "_").Trim('_');
                if (safeName.Length == 0) safeName = "Plugin_" + Guid.NewGuid().ToString("N")[..6];

                string targetDir = Path.Combine(root, safeName);
                if (targetDir.Equals(discovered.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Failure($"该插件已位于 SEP 托管目录: {targetDir}");
                }

                if (Directory.Exists(targetDir))
                {
                    targetDir = Path.Combine(root, $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}");
                }

                string sourceDirToCopy = discovered.Path;

                // If nested package, resolve effective inner core directory
                if (discovered.StructureType == PluginStructureType.NestedPackage)
                {
                    string? inner = FindEffectivePluginRoot(discovered.Path);
                    if (inner != null && Directory.Exists(inner))
                    {
                        sourceDirToCopy = inner;
                    }
                }

                // Copy folder cleanly
                CopyDirectory(sourceDirToCopy, targetDir);

                // Auto-generate or repair pml.index if missing
                string pmlLibPath = Directory.Exists(Path.Combine(targetDir, "pmllib"))
                    ? Path.Combine(targetDir, "pmllib")
                    : targetDir;

                bool hasPml = Directory.EnumerateFiles(pmlLibPath, "*.*", SearchOption.AllDirectories)
                    .Any(f => PmlExts.Contains(Path.GetExtension(f)));

                if (hasPml)
                {
                    _pluginService.RebuildIndex(pmlLibPath);
                }

                string actualName = Path.GetFileName(targetDir);

                if (enableImmediately)
                {
                    await _pluginService.SetEnabledAsync(actualName, true);
                    return ToolResult.Success(string.Format(Strings.Plugins_HostAndEnableSuccess, actualName));
                }

                _pluginService.SyncE3dRibbon();
                return ToolResult.Success(string.Format(Strings.Plugins_HostSuccess, actualName));
            }
            catch (Exception ex)
            {
                return ToolResult.Failure(string.Format(Strings.Plugins_HostFailed, ex.Message));
            }
        });
    }

    /// <summary>
    /// Searches up to 3 levels deep to locate the true effective plugin root containing PML, UIC, or E3D version subfolders.
    /// </summary>
    public static string? FindEffectivePluginRoot(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        // Check if current dir is already valid
        if (Directory.Exists(Path.Combine(dir, "pmllib")) ||
            Directory.Exists(Path.Combine(dir, "pdmsui")) ||
            Directory.EnumerateFiles(dir).Any(f => PmlExts.Contains(Path.GetExtension(f)) || f.EndsWith(".uic", StringComparison.OrdinalIgnoreCase)))
        {
            return dir;
        }

        // Recursively inspect child directories
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                string sName = Path.GetFileName(sub).ToLowerInvariant();
                if (sName.StartsWith('.') || SkipDirNames.Contains(sName)) continue;

                if (Directory.Exists(Path.Combine(sub, "pmllib")) ||
                    Directory.Exists(Path.Combine(sub, "pdmsui")) ||
                    Directory.EnumerateFiles(sub).Any(f => PmlExts.Contains(Path.GetExtension(f)) || f.EndsWith(".uic", StringComparison.OrdinalIgnoreCase)))
                {
                    return sub;
                }

                // Check version or app subfolder
                if (sName is "app" or "ture_color" or "true_color" or "plugin" or "e3d")
                {
                    string? deeper = FindEffectivePluginRoot(sub);
                    if (deeper != null) return deeper;
                }
            }
        }
        catch { }

        return dir;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            string subName = Path.GetFileName(d);
            if (subName.StartsWith('.') || SkipDirNames.Contains(subName)) continue;
            CopyDirectory(d, Path.Combine(dst, subName));
        }
    }
}
