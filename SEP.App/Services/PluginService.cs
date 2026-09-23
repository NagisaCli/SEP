using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

/// <summary>Outcome of adopting legacy hand-written plugin and project configurations.</summary>
public sealed record AdoptionReport(
    bool Changed,
    int PluginsAdopted,
    int ProjectsAdopted,
    List<string> AdoptedPluginNames,
    List<string> AdoptedProjectNames,
    string? BackupPath,
    string Message
);

/// <summary>
/// PML / PML.NET plug-in management — the port of e3d_plugin.py. A plug-in is a folder under the plug-ins root
/// (settings.plugins_dir, default D:\AVEVA\Plugins) with pmllib / pdmsui / bin / dflts sub-folders. Enabled
/// plug-ins are wired into E3D through a managed block in the local custom_evars.bat (PMLLIB/PMLUI/PMLNET/DFLTS
/// appends), separate from the projects block and shared with the Python tool.
/// </summary>
public sealed class PluginService
{
    public const string BlockStart = ":: >>> SEP MANAGED PLUGINS (do not edit) >>>";
    public const string BlockEnd = ":: <<< SEP MANAGED PLUGINS <<<";
    private const string ProjectsBlockStart = ":: >>> SEP MANAGED PROJECTS (do not edit) >>>";
    private const string ProjectsBlockEnd = ":: <<< SEP MANAGED PROJECTS <<<";
    public static readonly Regex BlockRe = new(@"(?ms)^[ \t]*" + Regex.Escape(BlockStart) + @".*?" + Regex.Escape(BlockEnd) + @"[ \t]*\r?\n?", RegexOptions.Compiled);
    public static readonly Regex ProjectsBlockRe = new(@"(?ms)^[ \t]*" + Regex.Escape(ProjectsBlockStart) + @".*?" + Regex.Escape(ProjectsBlockEnd) + @"[ \t]*\r?\n?", RegexOptions.Compiled);
    private static readonly Regex EnabledRe = new(@"set\s+(?:PMLLIB|PMLNET|CAF_ADDINS_PATH|PMLUI|AVEVA_DESIGN_DFLTS)=[^\r\n]*?\\Plugins\\([^\\\r\n;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> PmlExts = new(StringComparer.OrdinalIgnoreCase) { ".pmlfrm", ".pmlobj", ".pmlfnc", ".pmlcmd", ".pmlmac" };
    private static readonly Regex FormRe = new(@"setup\s+form\s+!!([a-zA-Z0-9_]+)(?:\s+(dialog|modal|dockable|main))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObjectRe = new(@"define\s+object\s+([a-zA-Z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FunctionRe = new(@"define\s+function\s+!!?([a-zA-Z0-9_]+)\s*\((.*?)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int MaxParseBytes = 4 * 1024 * 1024;

    private readonly IProjectCatalog _catalog;
    private FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounceTimer;

    public event Action? PluginsDirectoryChanged;

    public PluginService(IProjectCatalog catalog)
    {
        _catalog = catalog;
        _debounceTimer = new System.Timers.Timer(600) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => PluginsDirectoryChanged?.Invoke();
        SetupWatcher();
    }

    public void SetupWatcher()
    {
        try
        {
            SyncE3dRibbon();
            _watcher?.Dispose();
            string root = PluginsDir;
            if (!Directory.Exists(root)) return;
            _watcher = new FileSystemWatcher(root)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };

            void OnChanged(object sender, FileSystemEventArgs e)
            {
                string fn = Path.GetFileName(e.FullPath);
                if (fn.StartsWith('.') || fn.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }

            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += (_, e) => OnChanged(_, e);
        }
        catch { }
    }

    // ── locations ────────────────────────────────────────────────────────────────

    /// <summary>Configured plug-ins root, else the first existing of the usual places, else D:\AVEVA\Plugins.</summary>
    public string PluginsDir
    {
        get
        {
            string configured = SepPaths.Normalize(_catalog.Data.Settings.PluginsDir);
            if (configured.Length > 0) return configured;
            var candidates = new List<string>();
            string install = SepPaths.Normalize(_catalog.Paths.InstallDir);
            if (install.Length > 0) candidates.Add(Path.Combine(Path.GetDirectoryName(install.TrimEnd('\\')) ?? install, "Plugins"));
            candidates.AddRange(new[] { @"D:\AVEVA\Plugins", @"C:\AVEVA\Plugins", @"E:\AVEVA\Plugins" });
            return candidates.FirstOrDefault(Directory.Exists) ?? @"D:\AVEVA\Plugins";
        }
    }

    public void SetPluginsDir(string path)
    {
        string norm = SepPaths.Normalize(path);
        _catalog.Data.Settings.PluginsDir = norm.Length == 0 ? null : norm;
        _catalog.Save();
        SetupWatcher();
    }

    public string CustomEvarsPath
    {
        get
        {
            string dir = _catalog.LocalProjectsDir;
            foreach (var name in new[] { "custom_evars.bat", "custom_evar.bat" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            return Path.Combine(dir, "custom_evars.bat");
        }
    }

    // ── scanning ─────────────────────────────────────────────────────────────────

    public Task<List<PluginInfo>> ScanAsync(bool autoHeal = true) => Task.Run(() => Scan(autoHeal));

    private List<PluginInfo> Scan(bool autoHeal)
    {
        string root = PluginsDir;
        var result = new List<PluginInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(ReadEnabled(), StringComparer.OrdinalIgnoreCase);

        // Discovered external directories from custom_evars.bat (e.g. C:\Cad2E3DAids)
        var externalDirs = DiscoverPluginDirsFromCustomEvars();

        var dirsToScan = new List<string>();

        // 1. Scan default PluginsDir
        if (Directory.Exists(root))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith('.') || name.Contains(".bak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || name.Contains("_backup_", StringComparison.OrdinalIgnoreCase)) continue;
                    dirsToScan.Add(dir);
                    seenNames.Add(name);
                }
            }
            catch { }
        }

        // 2. Scan remaining external directories not under PluginsDir
        foreach (var extDir in externalDirs)
        {
            string name = Path.GetFileName(extDir);
            if (seenNames.Add(name))
            {
                dirsToScan.Add(extDir);
            }
        }

        string? inst = _catalog.Paths.InstallDir;
        string? ver = _catalog.Paths.E3dVersion;
        foreach (var dir in dirsToScan)
        {
            string name = Path.GetFileName(dir);
            var info = Inspect(dir, inst, ver);
            if (autoHeal && info.HasPmlLib && info.IndexNeedsRebuild && info.PmlFileCount > 0)
            {
                if (RebuildIndex(info.PmlLibPath!).Ok) info = Inspect(dir, inst, ver);
            }
            info.Enabled = enabled.Contains(name);
            info.DisplayName = _catalog.GetPluginDisplayName(name) ?? string.Empty;
            result.Add(info);
        }

        return result;
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// Detects the numeric version string (e.g. "3.1", "2.1", "12.1") of the active E3D installation.
    /// </summary>
    public static string DetectActiveE3dVersion(string? installDir, string? configuredVersion = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredVersion))
        {
            var m = Regex.Match(configuredVersion, @"\b(\d+\.\d+|\d+)\b");
            if (m.Success) return m.Groups[1].Value;
        }

        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            string desExe = Path.Combine(installDir, "des.exe");
            if (File.Exists(desExe))
            {
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(desExe);
                    if (fvi.ProductMajorPart > 0) return $"{fvi.ProductMajorPart}.{fvi.ProductMinorPart}";
                    if (fvi.FileMajorPart > 0) return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}";
                }
                catch { }
            }

            string dirName = Path.GetFileName(installDir.TrimEnd('\\', '/'));
            var dm = Regex.Match(dirName, @"(?i)(?:E3D|Everything3D|PDMS)[^\d]*(\d+\.\d+|\d+)");
            if (dm.Success) return dm.Groups[1].Value;
        }

        return "3.1";
    }

    /// <summary>
    /// Checks if a plugin folder contains version-specific subdirectories (e.g. E3D3.1, E3D2.1, 120sp4, 12.1).
    /// If so, returns the best-matching subdirectory path for the active E3D version.
    /// </summary>
    public static (string? VersionDir, string? MatchedVersion) ResolveVersionDirectory(string pluginDir, string activeVersion)
    {
        if (!Directory.Exists(pluginDir)) return (null, null);

        var subs = Directory.EnumerateDirectories(pluginDir).ToList();
        if (subs.Count == 0) return (null, null);

        string normActive = activeVersion.Trim();
        string compactActive = normActive.Replace(".", "");

        var candidates = new List<(string Dir, string CleanName, int Score)>();

        foreach (var sub in subs)
        {
            string name = Path.GetFileName(sub);
            string low = name.ToLowerInvariant();
            if (low is "pmllib" or "pdmsui" or "pmlui" or "ui" or "dflts" or "defaults" or ".git" or "doc" or "docs")
                continue;
            if (low.StartsWith('.') || low.Contains("bak") || low.Contains("backup") || low.EndsWith(".old"))
                continue;

            int score = 0;
            if (Regex.IsMatch(name, $@"(?i)\bE3D[_\-\s]*{Regex.Escape(normActive)}\b") || name.Equals($"E3D{normActive}", StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else if (name.Equals(normActive, StringComparison.OrdinalIgnoreCase))
            {
                score = 95;
            }
            else if (Regex.IsMatch(name, $@"(?i)\bE3D[_\-\s]*{Regex.Escape(compactActive)}\b") || name.Equals($"E3D{compactActive}", StringComparison.OrdinalIgnoreCase))
            {
                score = 90;
            }
            else if (name.Contains(normActive, StringComparison.OrdinalIgnoreCase))
            {
                score = 80;
            }
            else if (compactActive.Length > 1 && name.Contains(compactActive, StringComparison.OrdinalIgnoreCase))
            {
                score = 70;
            }

            if (score > 10)
            {
                candidates.Add((sub, name, score));
            }
        }

        if (candidates.Count > 0)
        {
            var best = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.CleanName).First();
            return (best.Dir, best.CleanName);
        }

        return (null, null);
    }

    /// <summary>
    /// Inspects a .NET assembly without executing or loading it into process memory.
    /// Categorizes whether it is an AVEVA Application Add-in (IAddin) or PML.NET assembly.
    /// </summary>
    public static PluginAssembly ClassifyAssembly(string dllPath, string? installDir = null)
    {
        string asmName = Path.GetFileNameWithoutExtension(dllPath);
        string? pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (!File.Exists(pdbPath)) pdbPath = null;

        bool isAddin = false;
        bool isPmlNet = false;
        bool hasAppFramework = false;
        bool hasPmlNetRef = false;
        bool hasAvevaRef = false;

        if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
        {
            string designAddins = Path.Combine(installDir, "DesignAddins.xml");
            if (File.Exists(designAddins))
            {
                try
                {
                    string xml = File.ReadAllText(designAddins);
                    if (xml.Contains($"<string>{asmName}</string>", StringComparison.OrdinalIgnoreCase))
                    {
                        isAddin = true;
                    }
                }
                catch { }
            }
        }

        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            if (peReader.HasMetadata)
            {
                var mdReader = peReader.GetMetadataReader();

                foreach (var h in mdReader.AssemblyReferences)
                {
                    var aref = mdReader.GetAssemblyReference(h);
                    string refName = mdReader.GetString(aref.Name);

                    if (refName.Equals("Aveva.ApplicationFramework", StringComparison.OrdinalIgnoreCase) ||
                        refName.Equals("Aveva.ApplicationFramework.Presentation", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAppFramework = true;
                    }
                    else if (refName.Equals("PMLNet", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPmlNetRef = true;
                    }
                    else if (refName.StartsWith("Aveva.", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAvevaRef = true;
                    }
                }

                if (hasAppFramework)
                {
                    foreach (var h in mdReader.TypeDefinitions)
                    {
                        var tdef = mdReader.GetTypeDefinition(h);
                        string typeName = mdReader.GetString(tdef.Name);

                        if (typeName.Contains("addin", StringComparison.OrdinalIgnoreCase) ||
                            typeName.Equals("IAddin", StringComparison.OrdinalIgnoreCase) ||
                            typeName.Equals("addinClass1", StringComparison.OrdinalIgnoreCase))
                        {
                            isAddin = true;
                            break;
                        }
                    }

                    if (!isAddin && !hasPmlNetRef)
                    {
                        isAddin = true;
                    }
                }
            }
        }
        catch { }

        if (isAddin)
        {
            isPmlNet = false;
        }
        else
        {
            if (hasPmlNetRef || hasAvevaRef)
            {
                isPmlNet = true;
            }
            else
            {
                string parentName = Path.GetFileName(Path.GetDirectoryName(dllPath) ?? string.Empty);
                if (parentName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    parentName.Equals("pmlnet", StringComparison.OrdinalIgnoreCase) ||
                    parentName.Equals("lib", StringComparison.OrdinalIgnoreCase))
                {
                    isPmlNet = true;
                }
            }
        }

        return new PluginAssembly(asmName, dllPath, isAddin, isPmlNet, pdbPath);
    }

    private PluginInfo Inspect(string folder, string? installDir = null, string? configuredVersion = null)
    {
        string name = Path.GetFileName(folder);
        string inst = !string.IsNullOrEmpty(installDir) ? SepPaths.Normalize(installDir) :
            (Directory.Exists(@"D:\AVEVA\Everything3D3.1") ? @"D:\AVEVA\Everything3D3.1" : (Directory.Exists(@"C:\AVEVA\Everything3D3.1") ? @"C:\AVEVA\Everything3D3.1" : string.Empty));
        string activeVersion = DetectActiveE3dVersion(inst, configuredVersion ?? _catalog.Paths.E3dVersion);

        string folderToInspect = folder;
        string? effectiveRoot = PluginDiscoveryService.FindEffectivePluginRoot(folder);
        if (effectiveRoot != null && !effectiveRoot.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            folderToInspect = effectiveRoot;
        }

        var (versionDir, matchedVer) = ResolveVersionDirectory(folderToInspect, activeVersion);
        if (versionDir == null && folderToInspect != folder)
        {
            (versionDir, matchedVer) = ResolveVersionDirectory(folder, activeVersion);
        }

        string? pmllib = null, pmlui = null, pmlnet = null, dflts = null;
        var diagnostics = new List<string>();

        try
        {
            if (versionDir != null)
            {
                foreach (var sub in Directory.EnumerateDirectories(versionDir))
                {
                    switch (Path.GetFileName(sub).ToLowerInvariant())
                    {
                        case "pmllib": pmllib = sub; break;
                        case "pdmsui": case "pmlui": case "ui": pmlui = sub; break;
                        case "bin": case "pmlnet": case "lib": pmlnet = sub; break;
                        case "dflts": case "defaults": dflts = sub; break;
                    }
                }
                if (pmlnet == null && Directory.EnumerateFiles(versionDir).Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    pmlnet = versionDir;
                }
                if (pmllib == null && Directory.EnumerateFiles(versionDir).Any(f => PmlExts.Contains(Path.GetExtension(f)) || f.EndsWith(".mac", StringComparison.OrdinalIgnoreCase)))
                {
                    pmllib = versionDir;
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(folderToInspect))
            {
                switch (Path.GetFileName(sub).ToLowerInvariant())
                {
                    case "pmllib": pmllib ??= sub; break;
                    case "pdmsui": case "pmlui": case "ui": pmlui ??= sub; break;
                    case "bin": case "pmlnet": case "lib": pmlnet ??= sub; break;
                    case "dflts": case "defaults": dflts ??= sub; break;
                }
            }
            if (pmllib == null && Directory.EnumerateFiles(folderToInspect).Any(f => PmlExts.Contains(Path.GetExtension(f)))) pmllib = folderToInspect;
            if (pmlnet == null && Directory.EnumerateFiles(folderToInspect).Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) pmlnet = folderToInspect;
        }
        catch (Exception ex)
        {
            diagnostics.Add(string.Format(Strings.Plugins_DiagUnreadable, ex.Message));
        }

        var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (versionDir != null)
        {
            foreach (var sub in Directory.EnumerateDirectories(folderToInspect))
            {
                if (!sub.Equals(versionDir, StringComparison.OrdinalIgnoreCase))
                {
                    string subName = Path.GetFileName(sub).ToLowerInvariant();
                    if (subName.StartsWith("e3d") || subName.StartsWith("12") || subName.StartsWith("pdms") || subName.Contains("sp"))
                    {
                        excludedDirs.Add(sub);
                    }
                }
            }
        }

        var forms = new List<PluginSymbol>(); var objects = new List<PluginSymbol>(); var functions = new List<PluginSymbol>();
        var macros = new List<PluginSymbol>(); var assemblies = new List<PluginSymbol>(); var uics = new List<PluginSymbol>();
        var addins = new List<PluginAssembly>();

        foreach (var file in EnumerateFiles(folderToInspect, excludedDirs))
        {
            string fname = Path.GetFileName(file);
            string low = fname.ToLowerInvariant();
            if (low.EndsWith(".bak") || low.EndsWith(".old") || low.EndsWith(".invalid") || low.Contains(".bak")) continue;
            string ext = Path.GetExtension(low);
            try
            {
                switch (ext)
                {
                    case ".pmlfrm":
                    {
                        string text = ReadHead(file);
                        var m = FormRe.Match(text);
                        string fn = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fname);
                        forms.Add(new PluginSymbol("form", fn, fname, file, $"show !!{fn}"));
                        break;
                    }
                    case ".pmlobj":
                    {
                        var m = ObjectRe.Match(ReadHead(file));
                        string on = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fname);
                        objects.Add(new PluginSymbol("object", on, fname, file, null));
                        break;
                    }
                    case ".pmlfnc":
                    {
                        var m = FunctionRe.Match(ReadHead(file));
                        string fn = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fname);
                        string args = m.Success ? m.Groups[2].Value.Trim() : string.Empty;
                        functions.Add(new PluginSymbol("function", fn, fname, file, $"!!{fn}({args})"));
                        break;
                    }
                    case ".pmlcmd": case ".pmlmac": case ".mac":
                        macros.Add(new PluginSymbol("macro", Path.GetFileNameWithoutExtension(fname), fname, file, $"$m {file}"));
                        break;
                    case ".dll":
                    {
                        string asm = Path.GetFileNameWithoutExtension(fname);
                        bool framework = asm.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                                         asm.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                                         asm.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                                         asm.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase);
                        if (!framework)
                        {
                            if (!assemblies.Any(a => a.Name.Equals(asm, StringComparison.OrdinalIgnoreCase)))
                            {
                                var pa = ClassifyAssembly(file, inst);
                                if (pa.IsAddin)
                                {
                                    addins.Add(pa);
                                    assemblies.Add(new PluginSymbol("addin", asm, fname, file, $"-- Addin: {asm}"));
                                }
                                else
                                {
                                    assemblies.Add(new PluginSymbol("assembly", asm, fname, file, $"import '{asm}'"));
                                }
                            }
                        }
                        break;
                    }
                    case ".uic":
                        uics.Add(new PluginSymbol("uic", Path.GetFileNameWithoutExtension(fname), fname, file, null));
                        break;
                    case ".xml":
                        if (low.Contains("uic") || low.Contains("ribbon")) uics.Add(new PluginSymbol("uic", Path.GetFileNameWithoutExtension(fname), fname, file, null));
                        break;
                }
            }
            catch { }
        }

        string status = "none";
        int indexCount = 0;
        int pmlIndexableCount = 0;

        if (pmllib != null && Directory.Exists(pmllib))
        {
            var pmlFilesInLib = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateFiles(pmllib))
            {
                string fn = Path.GetFileName(file);
                string low = fn.ToLowerInvariant();
                string ext = Path.GetExtension(low);
                if (PmlExts.Contains(ext) &&
                    !low.EndsWith(".bak") &&
                    !low.EndsWith(".old") &&
                    !low.EndsWith(".invalid") &&
                    !low.Contains(".bak"))
                {
                    pmlFilesInLib.Add(fn);
                }
            }

            pmlIndexableCount = pmlFilesInLib.Count;

            if (pmlIndexableCount == 0)
            {
                // This library directory does not contain indexable PML definitions (.pmlfrm, .pmlobj, .pmlfnc, .pmlcmd, .pmlmac)
                status = "none";
                string idx = Path.Combine(pmllib, "pml.index");
                if (File.Exists(idx))
                {
                    try { File.Delete(idx); } catch { }
                }
            }
            else
            {
                string idx = Path.Combine(pmllib, "pml.index");
                if (File.Exists(idx))
                {
                    try
                    {
                        var indexedFiles = File.ReadLines(idx)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0 && !l.StartsWith('/'))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        indexCount = indexedFiles.Count;
                        bool isMatch = indexedFiles.SetEquals(pmlFilesInLib);
                        status = isMatch ? "ok" : "outdated";

                        if (status == "outdated")
                        {
                            diagnostics.Add(string.Format(Strings.Plugins_DiagIndexOutdated, indexCount, pmlIndexableCount));
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "error";
                        diagnostics.Add(string.Format(Strings.Plugins_DiagIndexError, ex.Message));
                    }
                }
                else
                {
                    status = "missing";
                    diagnostics.Add(Strings.Plugins_DiagIndexMissing);
                }
            }
        }

        // Find preferred launcher macro (e.g. launch_*.mac, start_*.mac, run_*.mac)
        var launcherMacro = macros.FirstOrDefault(m => Regex.IsMatch(m.Name, @"(?i)^(?:launch|start|run)_"));
        string? preferredLauncher = launcherMacro?.CallCommand;

        var entry = new List<string>();
        if (!string.IsNullOrEmpty(preferredLauncher)) entry.Add(preferredLauncher);
        entry.AddRange(forms.Select(f => f.CallCommand!));
        if (entry.Count == 0) entry.AddRange(functions.Select(f => f.CallCommand!));
        if (entry.Count == 0) entry.AddRange(macros.Select(m => m.CallCommand!));
        entry.AddRange(assemblies.Select(a => a.CallCommand!));

        var hot = new List<string>();
        if (pmllib != null) { hot.Add("pml rehash all"); }
        hot.AddRange(assemblies.Where(a => a.Kind != "addin").Select(a => a.CallCommand!));

        PluginStructureType structType;
        if (versionDir != null) structType = PluginStructureType.MultiVersion;
        else if (!folder.Equals(folderToInspect, StringComparison.OrdinalIgnoreCase)) structType = PluginStructureType.NestedPackage;
        else if (pmllib != null && pmllib.Equals(folder, StringComparison.OrdinalIgnoreCase)) structType = PluginStructureType.Flat;
        else if (addins.Count > 0 && forms.Count == 0 && pmlIndexableCount == 0) structType = PluginStructureType.AddinOnly;
        else if (forms.Count == 0 && functions.Count > 0) structType = PluginStructureType.FunctionOnly;
        else structType = PluginStructureType.Standard;

        return new PluginInfo
        {
            Name = name, Path = folder, PmlLibPath = pmllib, PmlUiPath = pmlui, PmlNetPath = pmlnet, DfltsPath = dflts,
            TargetVersion = matchedVer, ResolvedVersionDir = versionDir,
            PmlIndexStatus = status, PmlIndexCount = indexCount, PmlFileCount = pmlIndexableCount,
            Forms = forms, Objects = objects, Functions = functions, Macros = macros, Assemblies = assemblies, UicConfigs = uics,
            Diagnostics = diagnostics, EntryCommands = entry, HotloadCommands = hot,
            AddinAssemblies = addins,
            StructureType = structType,
            PreferredLauncher = preferredLauncher,
        };
    }

    private static IEnumerable<string> EnumerateFiles(string folder, ISet<string>? excludedDirs = null)
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (excludedDirs != null && excludedDirs.Contains(dir)) continue;
            IEnumerable<string> files, subs;
            try { files = Directory.EnumerateFiles(dir).ToList(); subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
            {
                if (excludedDirs != null && excludedDirs.Contains(s)) continue;
                string n = Path.GetFileName(s).ToLowerInvariant();
                if (n.StartsWith('.') || n.Contains("backup") || n.Contains("bak")) continue;
                stack.Push(s);
            }
        }
    }

    private static string ReadHead(string file)
    {
        var fi = new FileInfo(file);
        if (fi.Length > MaxParseBytes) return string.Empty;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(fi.Length, 256 * 1024)];
        int n = fs.Read(buf, 0, buf.Length);
        return Encoding.Latin1.GetString(buf, 0, n);
    }

    // ── helpers for paths and external plugins ───────────────────────────────────

    public static string ExtractPluginNameFromPath(string path)
    {
        string norm = SepPaths.Normalize(path);
        if (norm.Length == 0) return string.Empty;
        int pIdx = norm.IndexOf(@"\Plugins\", StringComparison.OrdinalIgnoreCase);
        if (pIdx >= 0)
        {
            string after = norm[(pIdx + @"\Plugins\".Length)..];
            string pluginName = after.Split('\\', '/')[0];
            if (!string.IsNullOrEmpty(pluginName)) return pluginName;
        }
        string leaf = Path.GetFileName(norm).ToLowerInvariant();
        if (leaf is "pmllib" or "pdmsui" or "pmlui" or "bin" or "dflts" or "defaults")
        {
            string? parent = Path.GetDirectoryName(norm);
            return parent != null ? Path.GetFileName(parent) : string.Empty;
        }
        return Path.GetFileName(norm);
    }

    public List<string> DiscoverPluginDirsFromCustomEvars()
    {
        string custom = CustomEvarsPath;
        var dirs = new List<string>();
        if (!File.Exists(custom)) return dirs;
        string text;
        try { text = SepPaths.ReadTextSmart(custom).Text; } catch { return dirs; }

        string root = PluginsDir.TrimEnd('\\', '/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("rem [SEP ADOPTED PLUGINS:", StringComparison.OrdinalIgnoreCase))
            {
                int colonIdx = t.IndexOf(']');
                if (colonIdx > 0) t = t[(colonIdx + 1)..].Trim();
            }
            else if (t.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || t.StartsWith("::"))
            {
                continue;
            }

            var ms = Regex.Match(t, @"^(?:if\s+exist\s+""[^""]*""\s+)?set\s+(?:PMLLIB|PMLNET|CAF_ADDINS_PATH|PMLUI|AVEVA_DESIGN_DFLTS)=(.+)$", RegexOptions.IgnoreCase);
            if (!ms.Success) continue;
            foreach (var part in ms.Groups[1].Value.Split(';'))
            {
                string p = part.Trim().Trim('"').Trim();
                if (p.Length == 0 || p.StartsWith('%')) continue;
                string norm = SepPaths.Normalize(p);

                // If inside PluginsDir, resolve to top-level plugin root
                if (norm.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = norm[(root.Length + 1)..];
                    string pluginName = rel.Split('\\', '/')[0];
                    string actualPluginDir = Path.Combine(root, pluginName);
                    if (Directory.Exists(actualPluginDir) && seen.Add(actualPluginDir))
                    {
                        dirs.Add(actualPluginDir);
                    }
                    continue;
                }

                string leaf = Path.GetFileName(norm).ToLowerInvariant();
                string rootCandidate = (leaf is "pmllib" or "pdmsui" or "pmlui" or "bin" or "dflts" or "defaults")
                    ? Path.GetDirectoryName(norm) ?? norm
                    : norm;
                if (Directory.Exists(rootCandidate) && seen.Add(rootCandidate))
                {
                    dirs.Add(rootCandidate);
                }
            }
        }
        return dirs;
    }

    // ── enable / disable through custom_evars.bat ───────────────────────────────

    public List<string> ReadEnabled()
    {
        string custom = CustomEvarsPath;
        if (!File.Exists(custom)) return new List<string>();
        string text;
        try { text = SepPaths.ReadTextSmart(custom).Text; } catch { return new List<string>(); }
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. From managed block (authoritative: read from plugin comment header)
        var mb = BlockRe.Match(text);
        if (mb.Success)
        {
            foreach (var line in mb.Value.Split('\n'))
            {
                string t = line.Trim();
                var mp = Regex.Match(t, @"^rem\s+---\s+E3D Plugin:\s*([^\r\n-]+?)\s*---", RegexOptions.IgnoreCase);
                if (mp.Success) enabled.Add(mp.Groups[1].Value.Trim());
            }
        }

        // 2. From unmanaged lines outside managed blocks (e.g. legacy hand-written configs before adoption)
        string outside = BlockRe.Replace(text, string.Empty);
        outside = ProjectsBlockRe.Replace(outside, string.Empty);
        foreach (var line in outside.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || t.StartsWith("::")) continue;
            var ms = Regex.Match(t, @"^(?:if\s+exist\s+""[^""]*""\s+)?set\s+(?:PMLLIB|PMLNET|CAF_ADDINS_PATH|PMLUI|AVEVA_DESIGN_DFLTS)=(.+)$", RegexOptions.IgnoreCase);
            if (ms.Success)
            {
                foreach (var part in ms.Groups[1].Value.Split(';'))
                {
                    string p = part.Trim().Trim('"').Trim();
                    if (p.Length == 0 || p.StartsWith('%')) continue;
                    string name = ExtractPluginNameFromPath(p);
                    if (name.Length > 0) enabled.Add(name);
                }
            }
        }

        return enabled.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<ToolResult> SetEnabledAsync(string name, bool enabled) => Task.Run(() =>
    {
        try
        {
            var current = new HashSet<string>(ReadEnabled(), StringComparer.OrdinalIgnoreCase);
            if (enabled) current.Add(name); else current.Remove(name);
            var sorted = current.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            WriteBlock(sorted);
            SyncE3dRibbon(sorted);
            return ToolResult.Success(string.Format(enabled ? Strings.Plugins_Enabled : Strings.Plugins_Disabled, name));
        }
        catch (Exception ex)
        {
            return ToolResult.Failure(string.Format(Strings.Plugins_WriteFailed, ex.Message));
        }
    });

    public Task<ToolResult> SetAllEnabledAsync(IEnumerable<PluginInfo> plugins, bool enabled) => Task.Run(() =>
    {
        try
        {
            var names = enabled ? plugins.Select(p => p.Name).ToList() : new List<string>();
            WriteBlock(names);
            SyncE3dRibbon(names);
            return ToolResult.Success(enabled ? string.Format(Strings.Plugins_AllEnabled, names.Count) : Strings.Plugins_AllDisabled);
        }
        catch (Exception ex)
        {
            return ToolResult.Failure(string.Format(Strings.Plugins_WriteFailed, ex.Message));
        }
    });

    /// <summary>Rewrites the plug-ins block; the projects block is kept last, as e3d_plugin.write_plugins_block does.</summary>
    private void WriteBlock(List<string> enabledNames)
    {
        string localDir = _catalog.LocalProjectsDir;
        Directory.CreateDirectory(localDir);
        string custom = CustomEvarsPath;
        string text; Encoding enc;
        bool existed = File.Exists(custom);
        if (existed) (text, enc) = SepPaths.ReadTextSmart(custom);
        else (text, enc) = ("@echo off\r\n", SepPaths.Gbk);

        text = BlockRe.Replace(text, string.Empty);
        string block = BuildBlock(enabledNames);

        var pm = ProjectsBlockRe.Match(text);
        if (pm.Success)
        {
            string projectsBlock = pm.Value.Trim();
            string before = ProjectsBlockRe.Replace(text, string.Empty).TrimEnd('\r', '\n');
            text = before + "\r\n\r\n" + block + "\r\n\r\n" + projectsBlock + "\r\n";
        }
        else
        {
            text = text.TrimEnd('\r', '\n') + "\r\n\r\n" + block + "\r\n";
        }

        string bak = custom + ".sep.bak";
        if (existed && !File.Exists(bak)) File.Copy(custom, bak);
        File.WriteAllText(custom, text, enc);
    }

    public string BuildBlock(List<string> enabledNames, IReadOnlyDictionary<string, string>? customPluginPaths = null)
    {
        var lines = new List<string> { BlockStart };
        string root = PluginsDir;
        var extDirs = DiscoverPluginDirsFromCustomEvars()
            .DistinctBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(d => Path.GetFileName(d), d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var name in enabledNames)
        {
            string? dir = null;
            if (customPluginPaths != null && customPluginPaths.TryGetValue(name, out var cp) && Directory.Exists(cp))
                dir = cp;
            else if (Directory.Exists(Path.Combine(root, name)))
                dir = Path.Combine(root, name);
            else if (extDirs.TryGetValue(name, out var ep) && Directory.Exists(ep))
                dir = ep;

            if (dir == null || !Directory.Exists(dir)) continue;
            var info = Inspect(dir, _catalog.Paths.InstallDir, _catalog.Paths.E3dVersion);
            lines.Add($"rem --- E3D Plugin: {name} ---");
            if (info.PmlLibPath != null)
            {
                var pmlDirs = new List<string> { info.PmlLibPath };
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(info.PmlLibPath))
                    {
                        if (Directory.EnumerateFiles(sub, "*.*", SearchOption.AllDirectories)
                            .Any(f => PmlExts.Contains(Path.GetExtension(f)) || f.EndsWith(".mac", StringComparison.OrdinalIgnoreCase)))
                        {
                            pmlDirs.Add(sub);
                        }
                    }
                }
                catch { }

                string pmlJoined = string.Join(";", pmlDirs.Distinct(StringComparer.OrdinalIgnoreCase));
                lines.Add($"if exist \"{info.PmlLibPath}\" set PMLLIB={pmlJoined};%PMLLIB%");
            }

            if (info.ResolvedVersionDir != null && !info.ResolvedVersionDir.Equals(info.PmlLibPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.EnumerateFiles(info.ResolvedVersionDir, "*.*")
                        .Any(f => PmlExts.Contains(Path.GetExtension(f)) || f.EndsWith(".mac", StringComparison.OrdinalIgnoreCase)))
                    {
                        lines.Add($"if exist \"{info.ResolvedVersionDir}\" set PMLLIB={info.ResolvedVersionDir};%PMLLIB%");
                    }
                }
                catch { }
            }

            if (info.PmlUiPath != null)
            {
                lines.Add($"if exist \"{info.PmlUiPath}\" set PMLUI={info.PmlUiPath};%PMLUI%");
                lines.Add($"if exist \"{info.PmlUiPath}\" set PDMSUI={info.PmlUiPath};%PDMSUI%");
            }
            if (info.PmlNetPath != null)
            {
                lines.Add($"if exist \"{info.PmlNetPath}\" set PMLNET={info.PmlNetPath};%PMLNET%");
                lines.Add($"if exist \"{info.PmlNetPath}\" set CAF_ADDINS_PATH={info.PmlNetPath};%CAF_ADDINS_PATH%");
            }
            if (info.DfltsPath != null) lines.Add($"if exist \"{info.DfltsPath}\" set AVEVA_DESIGN_DFLTS={info.DfltsPath};%AVEVA_DESIGN_DFLTS%");
        }
        lines.Add(BlockEnd);
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Scans custom_evars.bat for hand-written plugin and project configurations outside the SEP managed blocks,
    /// backs up the original file, safely comments out the unmanaged lines, and migrates them into standard managed blocks.
    /// </summary>
    public AdoptionReport AdoptLegacyConfigs(bool force = false)
    {
        string custom = CustomEvarsPath;
        if (!File.Exists(custom))
        {
            return new AdoptionReport(false, 0, 0, new(), new(), null, "custom_evars.bat not found");
        }

        string text;
        Encoding enc;
        try { (text, enc) = SepPaths.ReadTextSmart(custom); }
        catch (Exception ex)
        {
            return new AdoptionReport(false, 0, 0, new(), new(), null, $"Failed to read custom_evars.bat: {ex.Message}");
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var adoptedPluginNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adoptedProjectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedLines = new List<string>();

        bool inPluginsBlock = false;
        bool inProjectsBlock = false;
        bool hasUnmanaged = false;

        // First pass: detect unmanaged lines
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i].Trim();
            if (raw.Contains(BlockStart)) { inPluginsBlock = true; continue; }
            if (raw.Contains(BlockEnd)) { inPluginsBlock = false; continue; }
            if (raw.Contains(ProjectsBlockStart)) { inProjectsBlock = true; continue; }
            if (raw.Contains(ProjectsBlockEnd)) { inProjectsBlock = false; continue; }

            if (inPluginsBlock || inProjectsBlock) continue;
            if (raw.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("::")) continue;

            // Check if unmanaged plugin line
            var mp = Regex.Match(raw, @"^(?:if\s+exist\s+""[^""]*""\s+)?set\s+(?:PMLLIB|PMLNET|CAF_ADDINS_PATH|PMLUI|AVEVA_DESIGN_DFLTS)=(.+)$", RegexOptions.IgnoreCase);
            if (mp.Success)
            {
                hasUnmanaged = true;
                foreach (var part in mp.Groups[1].Value.Split(';'))
                {
                    string p = part.Trim().Trim('"').Trim();
                    if (p.Length == 0 || p.StartsWith('%')) continue;
                    string name = ExtractPluginNameFromPath(p);
                    if (name.Length > 0) adoptedPluginNames.Add(name);
                }
                continue;
            }

            // Check if unmanaged project call line
            var mc = Regex.Match(raw, @"^\s*(?:if\s+(?:not\s+)?exist\s+[^\r\n]+?\s+)?call\s+(?:""([^""]+)""|([^\s\r\n]+))", RegexOptions.IgnoreCase);
            if (mc.Success)
            {
                string target = (mc.Groups[1].Success ? mc.Groups[1].Value : mc.Groups[2].Value).Trim().Trim('"').Trim();
                string fn = Path.GetFileName(target).ToLowerInvariant();
                if (fn.Contains("projects.bat") || fn.Contains("custom_evars.bat") || fn.Contains("custom_evar.bat") || fn.Contains("evars.bat"))
                    continue;
                if (fn.EndsWith(".bat"))
                {
                    hasUnmanaged = true;
                    string pName = LibraryScanner.ProjectName(target);
                    adoptedProjectNames.Add(pName);
                }
            }
        }

        if (!hasUnmanaged && !force)
        {
            return new AdoptionReport(false, 0, 0, new(), new(), null, Strings.Tools_CheckAdoptClean);
        }

        // Timestamped backup before modifying
        string backupPath = $"{custom}.adopt_bak_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        try
        {
            File.Copy(custom, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            return new AdoptionReport(false, 0, 0, new(), new(), null, $"Failed to create backup: {ex.Message}");
        }

        var existingEnabled = new HashSet<string>(ReadEnabled(), StringComparer.OrdinalIgnoreCase);
        bool inManagedBlock = false;

        foreach (var line in lines)
        {
            string raw = line.Trim();
            if (raw.StartsWith(BlockStart, StringComparison.OrdinalIgnoreCase) || raw.StartsWith(ProjectsBlockStart, StringComparison.OrdinalIgnoreCase))
            {
                inManagedBlock = true;
                modifiedLines.Add(line);
                continue;
            }
            if (raw.StartsWith(BlockEnd, StringComparison.OrdinalIgnoreCase) || raw.StartsWith(ProjectsBlockEnd, StringComparison.OrdinalIgnoreCase))
            {
                inManagedBlock = false;
                modifiedLines.Add(line);
                continue;
            }
            if (inManagedBlock || raw.Length == 0 || raw.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("::"))
            {
                modifiedLines.Add(line);
                continue;
            }

            // Check if unmanaged plugin line
            var mp = Regex.Match(raw, @"^(?:if\s+exist\s+""[^""]*""\s+)?set\s+(?:PMLLIB|PMLNET|CAF_ADDINS_PATH|PMLUI|AVEVA_DESIGN_DFLTS)=(.+)$", RegexOptions.IgnoreCase);
            if (mp.Success)
            {
                string tag = string.Empty;
                foreach (var part in mp.Groups[1].Value.Split(';'))
                {
                    string p = part.Trim().Trim('"').Trim();
                    if (p.Length == 0 || p.StartsWith('%')) continue;
                    string name = ExtractPluginNameFromPath(p);
                    if (name.Length > 0)
                    {
                        existingEnabled.Add(name);
                        adoptedPluginNames.Add(name);
                    }
                }
                foreach (var part in mp.Groups[1].Value.Split(';'))
                {
                    string p = part.Trim().Trim('"').Trim();
                    if (p.Length == 0 || p.StartsWith('%')) continue;
                    string name = ExtractPluginNameFromPath(p);
                    if (name.Length > 0) { tag = name; break; }
                }
                if (tag.Length == 0) tag = "legacy";
                modifiedLines.Add($"rem [SEP ADOPTED PLUGINS: {tag}] {line}");
                continue;
            }

            // Check if unmanaged project call line
            var mc = Regex.Match(raw, @"^\s*(?:if\s+(?:not\s+)?exist\s+[^\r\n]+?\s+)?call\s+(?:""([^""]+)""|([^\s\r\n]+))", RegexOptions.IgnoreCase);
            if (mc.Success)
            {
                string target = (mc.Groups[1].Success ? mc.Groups[1].Value : mc.Groups[2].Value).Trim().Trim('"').Trim();
                string fn = Path.GetFileName(target).ToLowerInvariant();
                if (fn.Contains("projects.bat") || fn.Contains("custom_evars.bat") || fn.Contains("custom_evar.bat") || fn.Contains("evars.bat"))
                {
                    modifiedLines.Add(line);
                    continue;
                }
                if (fn.EndsWith(".bat"))
                {
                    string pName = LibraryScanner.ProjectName(target);
                    adoptedProjectNames.Add(pName);
                    modifiedLines.Add($"rem [SEP ADOPTED PROJECT: {pName}] {line}");
                    continue;
                }
            }

            modifiedLines.Add(line);
        }

        if (adoptedPluginNames.Count == 0 && adoptedProjectNames.Count == 0 && !force)
        {
            return new AdoptionReport(false, 0, 0, new(), new(), null, Strings.Tools_CheckAdoptClean);
        }

        string newText = string.Join("\r\n", modifiedLines);

        // Regenerate the SEP MANAGED PLUGINS block with all enabled plugins
        newText = BlockRe.Replace(newText, string.Empty);
        string newPluginBlock = BuildBlock(existingEnabled.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

        var pjm = ProjectsBlockRe.Match(newText);
        if (pjm.Success)
        {
            string projBlock = pjm.Value.Trim();
            string before = ProjectsBlockRe.Replace(newText, string.Empty).TrimEnd('\r', '\n');
            newText = before + "\r\n\r\n" + newPluginBlock + "\r\n\r\n" + projBlock + "\r\n";
        }
        else
        {
            newText = newText.TrimEnd('\r', '\n') + "\r\n\r\n" + newPluginBlock + "\r\n";
        }

        try
        {
            File.WriteAllText(custom, newText, enc);
            SyncE3dRibbon(existingEnabled);
        }
        catch (Exception ex)
        {
            try { File.Copy(backupPath, custom, overwrite: true); } catch { }
            return new AdoptionReport(false, 0, 0, new(), new(), backupPath, $"Failed to write adopted custom_evars: {ex.Message}");
        }

        string msg = string.Format(Strings.Plugins_AdoptedBody, adoptedPluginNames.Count, adoptedProjectNames.Count, Path.GetFileName(backupPath));
        return new AdoptionReport(
            true,
            adoptedPluginNames.Count,
            adoptedProjectNames.Count,
            adoptedPluginNames.OrderBy(n => n).ToList(),
            adoptedProjectNames.OrderBy(n => n).ToList(),
            backupPath,
            msg
        );
    }

    // ── pml.index ────────────────────────────────────────────────────────────────

    public (bool Ok, string Message) RebuildIndex(string pmllib)
    {
        if (!Directory.Exists(pmllib)) return (false, Strings.Plugins_IndexNoDir);
        var groups = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFiles(pmllib))
        {
            string fname = Path.GetFileName(file);
            string low = fname.ToLowerInvariant();
            if (!PmlExts.Contains(Path.GetExtension(low)) || low.EndsWith(".bak") || low.EndsWith(".old") || low.EndsWith(".invalid") || low.Contains(".bak")) continue;
            string rel = Path.GetRelativePath(pmllib, Path.GetDirectoryName(file)!).Replace('\\', '/');
            string key = rel == "." ? "/" : "/" + rel.Trim('/') + "/";
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<string>();
            list.Add(fname);
        }
        if (groups.Count == 0)
        {
            string idxPath = Path.Combine(pmllib, "pml.index");
            if (File.Exists(idxPath))
            {
                try { File.Delete(idxPath); } catch { }
            }
            return (true, Strings.Plugins_IndexNoFiles);
        }

        var lines = new List<string>();
        var keys = groups.Keys.Where(k => k != "/").ToList();
        if (groups.ContainsKey("/")) keys.Add("/");
        int total = 0;
        foreach (var k in keys)
        {
            lines.Add(k);
            foreach (var f in groups[k].OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) { lines.Add(f); total++; }
        }
        try
        {
            File.WriteAllText(Path.Combine(pmllib, "pml.index"), string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
            return (true, string.Format(Strings.Plugins_IndexRebuilt, total));
        }
        catch (Exception ex)
        {
            return (false, string.Format(Strings.Plugins_IndexWriteFailed, ex.Message));
        }
    }

    public Task<ToolResult> RebuildAllIndexesAsync(IEnumerable<PluginInfo> plugins) => Task.Run(() =>
    {
        var output = new List<string>();
        int ok = 0;
        foreach (var p in plugins.Where(p => p.HasPmlLib && p.PmlFileCount > 0))
        {
            var (success, msg) = RebuildIndex(p.PmlLibPath!);
            if (success) ok++;
            output.Add($"{p.Name}: {msg}");
        }
        return ToolResult.Success(string.Format(Strings.Plugins_IndexAllDone, ok), output);
    });

    // ── hot-load macro / conflicts / chain ───────────────────────────────────────

    public (string Path, string Content) GenerateHotloadMacro(IEnumerable<PluginInfo> plugins)
    {
        var enabled = plugins.Where(p => p.Enabled).ToList();
        string path = Path.Combine(PluginsDir, "load_all_plugins.mac");
        var lines = new List<string>
        {
            "-- ========================================================",
            "-- AVEVA E3D Plugin Dynamic Hot-Load Macro",
            "-- Auto-generated by SEP (Smart E3D Project Launcher)",
            "-- Usage in E3D: $m " + path,
            "-- ========================================================",
            "-- 1. Rehash all PML definitions into memory",
            "pml rehash all",
            "",
            "-- 2. Import PML.NET Assemblies",
        };
        foreach (var a in enabled.SelectMany(p => p.Assemblies))
        {
            if (a.Kind != "addin" && !string.IsNullOrWhiteSpace(a.CallCommand))
            {
                string asmPathNoExt = Path.Combine(Path.GetDirectoryName(a.Path) ?? "", Path.GetFileNameWithoutExtension(a.File)).Replace('\\', '/');
                lines.Add($"import '{a.Name}'");
                lines.Add("handle any");
                lines.Add($"  import '{asmPathNoExt}'");
                lines.Add("  handle any");
                lines.Add("  endhandle");
                lines.Add("endhandle");
            }
        }
        lines.Add("");
        lines.Add("$P ========================================================");
        lines.Add($"$P [SEP] {enabled.Count} E3D plugins successfully hot-loaded!");
        lines.Add("$P Available Entry Commands:");
        foreach (var p in enabled)
            lines.Add($"$P   * {p.Name}: {(p.EntryCommands.Count > 0 ? string.Join(", ", p.EntryCommands) : "No direct form")}");
        lines.Add("$P ========================================================");
        string content = string.Join("\r\n", lines) + "\r\n";
        try
        {
            Directory.CreateDirectory(PluginsDir);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            App.Log($"hotload macro: {ex.Message}");
        }
        return (path, content);
    }

    public static List<PluginConflict> DetectConflicts(IEnumerable<PluginInfo> plugins)
    {
        var map = new Dictionary<string, (string Kind, List<string> Plugins)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugins.Where(p => p.Enabled))
        {
            foreach (var s in p.Forms) Add($"!!{s.Name}", "form", p.Name);
            foreach (var s in p.Objects) Add(s.Name, "object", p.Name);
            foreach (var s in p.Functions) Add($"!!{s.Name}()", "function", p.Name);
            foreach (var s in p.Assemblies) Add(s.Name + ".dll", "assembly", p.Name);
        }
        return map.Where(kv => kv.Value.Plugins.Count > 1)
            .Select(kv => new PluginConflict(kv.Key, kv.Value.Kind, kv.Value.Plugins))
            .OrderBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string symbol, string kind, string plugin)
        {
            string key = kind + ":" + symbol;
            if (!map.TryGetValue(key, out var entry)) map[key] = entry = (kind, new List<string>());
            if (!entry.Plugins.Contains(plugin)) entry.Plugins.Add(plugin);
        }
    }

    /// <summary>The PMLLIB / PMLUI / PMLNET / DFLTS entries E3D ends up with, in order: enabled plug-ins (prepended priority) first, then install defaults.</summary>
    public List<(string Variable, string Source, string Path)> ResolutionChain(IEnumerable<PluginInfo> plugins)
    {
        var chain = new List<(string, string, string)>();

        // 1. Enabled plugins (highest priority, prepended)
        foreach (var p in plugins.Where(p => p.Enabled))
        {
            if (p.PmlLibPath != null) chain.Add(("PMLLIB", p.Name, p.PmlLibPath));
            if (p.PmlUiPath != null) chain.Add(("PMLUI", p.Name, p.PmlUiPath));
            if (p.PmlNetPath != null) chain.Add(("PMLNET", p.Name, p.PmlNetPath));
            if (p.DfltsPath != null) chain.Add(("AVEVA_DESIGN_DFLTS", p.Name, p.DfltsPath));
        }

        // 2. Hand-written entries of custom_evars.bat outside the managed blocks
        string custom = CustomEvarsPath;
        if (File.Exists(custom))
        {
            try
            {
                string text = SepPaths.ReadTextSmart(custom).Text;
                text = BlockRe.Replace(text, string.Empty);
                text = ProjectsBlockRe.Replace(text, string.Empty);
                foreach (var line in text.Split('\n'))
                {
                    var m = Regex.Match(line.Trim(), @"^(?:if\s+exist\s+""[^""]*""\s+)?set\s+(PMLLIB|PMLUI|PMLNET|CAF_ADDINS_PATH|AVEVA_DESIGN_DFLTS)=(.+)$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    foreach (var part in m.Groups[2].Value.Split(';'))
                    {
                        string p = part.Trim();
                        if (p.Length == 0 || p.StartsWith('%')) continue;
                        chain.Add((m.Groups[1].Value.ToUpperInvariant(), "custom_evars.bat", p));
                    }
                }
            }
            catch { }
        }

        // 3. E3D base installation defaults (fallback)
        string install = SepPaths.Normalize(_catalog.Paths.InstallDir);
        if (install.Length > 0)
        {
            chain.Add(("PMLLIB", "E3D", Path.Combine(install, "pmllib")));
            chain.Add(("PMLUI", "E3D", Path.Combine(install, "PMLUI")));
        }

        return chain;
    }

    // ── create / import / open ───────────────────────────────────────────────────

    public Task<(bool Ok, string Message, string? Path)> CreateSkeletonAsync(string name, bool pmllib, bool pmlnet, bool pmlui) => Task.Run(() =>
    {
        string safe = Regex.Replace(name.Trim(), @"[^a-zA-Z0-9_\-]", string.Empty);
        if (safe.Length == 0) return (false, Strings.Plugins_NameInvalid, (string?)null);
        string target = Path.Combine(PluginsDir, safe);
        if (Directory.Exists(target)) return (false, string.Format(Strings.Plugins_AlreadyExists, safe), null);
        try
        {
            Directory.CreateDirectory(target);
            if (pmllib)
            {
                string lib = Path.Combine(target, "pmllib");
                Directory.CreateDirectory(Path.Combine(lib, "objects"));
                Directory.CreateDirectory(Path.Combine(lib, "forms"));
                Directory.CreateDirectory(Path.Combine(lib, "functions"));
                string fnc = Path.Combine(lib, "functions", $"{safe.ToLowerInvariant()}_hello.pmlfnc");
                File.WriteAllText(fnc, $"define function !!{safe.ToLowerInvariant()}_hello()\r\n  $P Hello from {safe} plugin!\r\nendfunction\r\n", new UTF8Encoding(false));
                RebuildIndex(lib);
            }
            if (pmlnet) Directory.CreateDirectory(Path.Combine(target, "bin"));
            if (pmlui) Directory.CreateDirectory(Path.Combine(target, "pdmsui"));
            File.WriteAllText(Path.Combine(target, "README.md"), $"# {safe}\r\n\r\nCreated by SEP plug-in manager.\r\n", new UTF8Encoding(false));
            return (true, string.Format(Strings.Plugins_Created, safe), target);
        }
        catch (Exception ex)
        {
            return (false, string.Format(Strings.Plugins_CreateFailed, ex.Message), null);
        }
    });

    public Task<(bool Ok, string Message, string? Name)> ImportAsync(string source) => Task.Run(() =>
    {
        string src = SepPaths.Normalize(source);
        if (!File.Exists(src) && !Directory.Exists(src)) return (false, string.Format(Strings.Plugins_ImportMissing, src), (string?)null);
        string root = PluginsDir;
        try
        {
            Directory.CreateDirectory(root);
            string baseName = Path.GetFileNameWithoutExtension(src.TrimEnd('\\'));
            string safe = Regex.Replace(baseName, @"[^a-zA-Z0-9_\-\u4e00-\u9fa5]", "_").Trim('_');
            if (safe.Length == 0) safe = "Plugin_" + Guid.NewGuid().ToString("N")[..6];
            string target = Path.Combine(root, safe);

            if (Directory.Exists(src))
            {
                if (Directory.Exists(target)) target = Path.Combine(root, $"{safe}_{DateTime.Now:yyyyMMddHHmmss}");
                CopyDirectory(src, target);
            }
            else if (src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(src, target, overwriteFiles: true);
                var items = Directory.EnumerateFileSystemEntries(target).Where(e => !Path.GetFileName(e).StartsWith('.')).ToList();
                if (items.Count == 1 && Directory.Exists(items[0]))
                {
                    // MyPlugin.zip that wraps a single MyPlugin/ folder: unwrap it
                    foreach (var inner in Directory.EnumerateFileSystemEntries(items[0]).ToList())
                    {
                        string dest = Path.Combine(target, Path.GetFileName(inner));
                        if (Directory.Exists(inner)) Directory.Move(inner, dest); else File.Move(inner, dest, overwrite: true);
                    }
                    Directory.Delete(items[0]);
                }
            }
            else
            {
                Directory.CreateDirectory(target);
                File.Copy(src, Path.Combine(target, Path.GetFileName(src)), overwrite: true);
            }

            var info = Inspect(target);
            if (info.HasPmlLib) RebuildIndex(info.PmlLibPath!);
            return (true, string.Format(Strings.Plugins_Imported, Path.GetFileName(target)), Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            return (false, string.Format(Strings.Plugins_ImportFailed, ex.Message), null);
        }
    });

    /// <summary>
    /// Safely and completely uninstalls a plugin:
    ///  1. Disables plugin and removes it from custom_evars.bat.
    ///  2. Withdraws deployed Addin assemblies from E3D install directory and DesignAddins.xml.
    ///  3. Removes Ribbon tools from SEP.uic.
    ///  4. Cleans metadata from settings.
    ///  5. Moves the plugin directory to _recycle (or permanently deletes it).
    /// </summary>
    public Task<ToolResult> UninstallPluginAsync(string name, bool permanentDelete = false) => Task.Run(async () =>
    {
        try
        {
            string root = PluginsDir;
            string pluginDir = Path.Combine(root, name);
            if (!Directory.Exists(pluginDir))
            {
                var extDirs = DiscoverPluginDirsFromCustomEvars();
                var matched = extDirs.FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (matched != null && Directory.Exists(matched))
                {
                    pluginDir = matched;
                }
                else
                {
                    return ToolResult.Failure($"未找到待卸载插件目录: {name}");
                }
            }

            // 1. Safely disable plugin first (cleans evars, syncs UIC and deployed addins)
            await SetEnabledAsync(name, false);

            // 2. Extra safety: explicitly withdraw any deployed addins matching this plugin
            string installDir = !string.IsNullOrEmpty(_catalog.Paths.InstallDir) && Directory.Exists(_catalog.Paths.InstallDir)
                ? SepPaths.Normalize(_catalog.Paths.InstallDir)
                : (Directory.Exists(@"D:\AVEVA\Everything3D3.1") ? @"D:\AVEVA\Everything3D3.1" : (Directory.Exists(@"C:\AVEVA\Everything3D3.1") ? @"C:\AVEVA\Everything3D3.1" : string.Empty));

            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
            {
                WithdrawAddinsForPlugin(installDir, name, pluginDir);
            }

            // 3. Clear custom metadata
            _catalog.SetPluginDisplayName(name, null);

            // 4. Remove physical folder (move to recycle backup or delete)
            if (permanentDelete)
            {
                Directory.Delete(pluginDir, recursive: true);
            }
            else
            {
                string recycleBase = Path.Combine(root, "_recycle");
                Directory.CreateDirectory(recycleBase);
                string recycleDir = Path.Combine(recycleBase, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.Move(pluginDir, recycleDir);
            }

            // 5. Trigger resync of Ribbon
            SyncE3dRibbon();

            return ToolResult.Success(string.Format(Strings.Plugins_UninstallSuccess, name));
        }
        catch (Exception ex)
        {
            return ToolResult.Failure(string.Format(Strings.Plugins_UninstallFailed, ex.Message));
        }
    });

    private static void WithdrawAddinsForPlugin(string installDir, string pluginName, string pluginDir)
    {
        try
        {
            string xmlPath = Path.Combine(installDir, "DesignAddins.xml");
            var candidateDlls = Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (candidateDlls.Count == 0) return;

            foreach (var asm in candidateDlls)
            {
                string targetDll = Path.Combine(installDir, asm + ".dll");
                string targetPdb = Path.Combine(installDir, asm + ".pdb");
                try { if (File.Exists(targetDll)) File.Delete(targetDll); } catch { }
                try { if (File.Exists(targetPdb)) File.Delete(targetPdb); } catch { }
            }

            if (File.Exists(xmlPath))
            {
                string text = File.ReadAllText(xmlPath, Encoding.UTF8);
                bool changed = false;
                foreach (var asm in candidateDlls)
                {
                    if (!string.IsNullOrEmpty(asm) && text.Contains($"<string>{asm}</string>", StringComparison.OrdinalIgnoreCase))
                    {
                        text = Regex.Replace(text, $@"\s*<string>{Regex.Escape(asm)}<\/string>", string.Empty, RegexOptions.IgnoreCase);
                        changed = true;
                    }
                }
                if (changed)
                {
                    File.WriteAllText(xmlPath, text, new UTF8Encoding(false));
                }
            }
        }
        catch { }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public void OpenFolder(string? path = null)
    {
        string dir = path ?? PluginsDir;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log($"open folder: {ex.Message}");
        }
    }

    // ── Unified E3D Ribbon & UI Customization Engine ─────────────────────────────

    /// <summary>
    /// Synchronizes the unified E3D 【SEP】 Ribbon tab and cleanly unregisters all legacy scattered UI entries.
    /// Only plugins that are currently enabled in SEP will have their groups and buttons appear under 【SEP】.
    /// </summary>
    public void SyncE3dRibbon(IEnumerable<string>? enabledNames = null)
    {
        try
        {
            var enabled = new HashSet<string>(enabledNames ?? ReadEnabled(), StringComparer.OrdinalIgnoreCase);
            string installDir = !string.IsNullOrEmpty(_catalog.Paths.InstallDir) && Directory.Exists(_catalog.Paths.InstallDir)
                ? SepPaths.Normalize(_catalog.Paths.InstallDir)
                : (Directory.Exists(@"D:\AVEVA\Everything3D3.1") ? @"D:\AVEVA\Everything3D3.1" : (Directory.Exists(@"C:\AVEVA\Everything3D3.1") ? @"C:\AVEVA\Everything3D3.1" : string.Empty));

            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return;

            var allPlugins = Scan(autoHeal: false);

            // 1. Generate the unified SEP.uic
            GenerateSepUic(installDir, enabled, allPlugins);

            // 2. Sync DesignCustomization.xml (registers SEP.uic, unregisters legacy NozzleMgr.uic)
            SyncDesignCustomizationXml(installDir);

            // 3. Clean legacy scattered injection files in E3D core directory
            CleanCoreUicFiles(installDir);

            // 4. Clean user profile UIC duplicates
            CleanUserDesignUic();

            // 5. Generic Sync DesignAddins.xml and deploy/link Add-in binaries
            SyncDesignAddins(installDir, allPlugins);
        }
        catch (Exception ex)
        {
            App.Log($"SyncE3dRibbon error: {ex.Message}");
        }
    }

    private static void CleanCoreUicFiles(string installDir)
    {
        try
        {
            // 1. Clean AVEVA.Design.piping.uic of rogue Custom.Piping.PipelineAid
            string pipingUic = Path.Combine(installDir, "AVEVA.Design.piping.uic");
            if (File.Exists(pipingUic))
            {
                string text = File.ReadAllText(pipingUic, Encoding.UTF8);
                if (text.Contains("Custom.Piping.PipelineAid", StringComparison.OrdinalIgnoreCase))
                {
                    string bak = pipingUic + ".sep.bak";
                    if (!File.Exists(bak)) File.Copy(pipingUic, bak);

                    // Remove Tool element from Groups
                    text = Regex.Replace(text, @"\s*<Tool\s+Name=""Custom\.Piping\.PipelineAid""[^>]*\/>", string.Empty, RegexOptions.IgnoreCase);
                    // Remove ButtonTool element from Tools
                    text = Regex.Replace(text, @"\s*<ButtonTool\s+Name=""Custom\.Piping\.PipelineAid"">[\s\S]*?<\/ButtonTool>", string.Empty, RegexOptions.IgnoreCase);

                    File.WriteAllText(pipingUic, text, new UTF8Encoding(false));
                }
            }

            // 2. Backup and retire rogue NozzleMgr.uic in installDir (since it is now in SEP.uic)
            string nozzleUic = Path.Combine(installDir, "NozzleMgr.uic");
            if (File.Exists(nozzleUic))
            {
                string bak = nozzleUic + ".sep.bak";
                if (!File.Exists(bak)) File.Copy(nozzleUic, bak, true);
                try { File.Delete(nozzleUic); } catch { }
            }

            // 3. Backup and retire rogue PMLLIB\toolkit in installDir (served from Pipline_Aid plugin)
            string coreToolkit = Path.Combine(installDir, "PMLLIB", "toolkit");
            if (Directory.Exists(coreToolkit))
            {
                string bak = Path.Combine(installDir, "PMLLIB", "toolkit.sep.bak");
                if (!Directory.Exists(bak)) Directory.Move(coreToolkit, bak);
                else try { Directory.Delete(coreToolkit, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            App.Log($"CleanCoreUicFiles error: {ex.Message}");
        }
    }

    private static void SyncDesignCustomizationXml(string installDir)
    {
        string xmlPath = Path.Combine(installDir, "DesignCustomization.xml");
        if (!File.Exists(xmlPath)) return;
        try
        {
            string text = File.ReadAllText(xmlPath, Encoding.UTF8);
            bool changed = false;

            // Remove legacy NozzleMgr.uic entry if present
            if (text.Contains("<CustomizationFile Name=\"NozzleMgr\"", StringComparison.OrdinalIgnoreCase))
            {
                text = Regex.Replace(text, @"\s*<CustomizationFile\s+Name=""NozzleMgr""\s+Path=""NozzleMgr\.uic""\s*\/>", string.Empty, RegexOptions.IgnoreCase);
                changed = true;
            }

            // Ensure SEP.uic is registered
            if (!text.Contains("<CustomizationFile Name=\"SEP\"", StringComparison.OrdinalIgnoreCase))
            {
                int closeIdx = text.IndexOf("</UICustomizationFiles>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    string entry = "    <CustomizationFile Name=\"SEP\" Path=\"SEP.uic\" />\r\n  ";
                    text = text.Insert(closeIdx, entry);
                    changed = true;
                }
            }

            if (changed)
            {
                string bak = xmlPath + ".sep.bak";
                if (!File.Exists(bak)) File.Copy(xmlPath, bak);
                File.WriteAllText(xmlPath, text, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            App.Log($"SyncDesignCustomizationXml error: {ex.Message}");
        }
    }

    private static void CleanUserDesignUic()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userUic = Path.Combine(appData, @"Aveva\AVEVA E3D Design\3.1\UserDesign.uic");
            if (!File.Exists(userUic)) return;

            string text = File.ReadAllText(userUic, Encoding.UTF8);
            if (text.Contains("Cad2E3D", StringComparison.OrdinalIgnoreCase))
            {
                string bak = userUic + ".sep.bak";
                if (!File.Exists(bak)) File.Copy(userUic, bak);

                string cleanUic = @"<?xml version=""1.0"" encoding=""utf-8""?>
<UserInterfaceCustomization xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns=""www.aveva.com"">
  <Version>1.0</Version>
  <FormsMetaData />
  <Tools />
  <InstanceTools />
  <MenuBar />
  <CommandBars />
  <TaskPanes />
  <ContextMenus />
  <AreaLeftTools />
  <AreaRightTools />
  <FooterTools />
  <NavigationMenuTools />
  <QATTools />
  <ContextualTabGroups />
  <TabToolbarTools />
  <Tabs />
  <MiniToolbarTools />
</UserInterfaceCustomization>";
                File.WriteAllText(userUic, cleanUic, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            App.Log($"CleanUserDesignUic error: {ex.Message}");
        }
    }

    private const string ManagedAddinsFile = ".sep_managed_addins.json";

    private sealed class DeployedAddinRecord
    {
        public string AddinName { get; set; } = string.Empty;
        public string SourceDll { get; set; } = string.Empty;
        public string TargetDll { get; set; } = string.Empty;
        public string? TargetPdb { get; set; }
        public DateTime DeployedAt { get; set; }
    }

    /// <summary>
    /// Synchronizes DesignAddins.xml and dynamically deploys/links required Add-in binaries
    /// directly to the active E3D installation root, cleanly withdrawing them when disabled.
    /// ZERO HARDCODING: works for any third-party Add-in (e.g. TrueColor, etc.).
    /// </summary>
    private static void SyncDesignAddins(string installDir, IReadOnlyList<PluginInfo> allPlugins)
    {
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return;
        string xmlPath = Path.Combine(installDir, "DesignAddins.xml");
        string trackingPath = Path.Combine(installDir, ManagedAddinsFile);

        try
        {
            var previouslyDeployed = new List<DeployedAddinRecord>();
            if (File.Exists(trackingPath))
            {
                try
                {
                    string json = File.ReadAllText(trackingPath);
                    previouslyDeployed = System.Text.Json.JsonSerializer.Deserialize<List<DeployedAddinRecord>>(json) ?? new();
                }
                catch { }
            }

            var enabledAddins = new List<(string AddinName, PluginAssembly Assembly)>();
            foreach (var p in allPlugins.Where(p => p.Enabled))
            {
                foreach (var asm in p.AddinAssemblies)
                {
                    enabledAddins.Add((asm.Name, asm));
                }
            }

            var enabledAddinNames = new HashSet<string>(enabledAddins.Select(a => a.AddinName), StringComparer.OrdinalIgnoreCase);

            // Clean up disabled addins
            var stillDeployed = new List<DeployedAddinRecord>();
            foreach (var rec in previouslyDeployed)
            {
                if (!enabledAddinNames.Contains(rec.AddinName))
                {
                    try
                    {
                        if (File.Exists(rec.TargetDll)) File.Delete(rec.TargetDll);
                        if (!string.IsNullOrEmpty(rec.TargetPdb) && File.Exists(rec.TargetPdb)) File.Delete(rec.TargetPdb);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"Error withdrawing addin {rec.AddinName}: {ex.Message}");
                    }
                }
                else
                {
                    stillDeployed.Add(rec);
                }
            }

            // Deploy enabled addins
            foreach (var (addinName, asm) in enabledAddins)
            {
                string targetDll = Path.Combine(installDir, addinName + ".dll");
                string targetPdb = Path.Combine(installDir, addinName + ".pdb");
                bool needsDeploy = false;

                if (!File.Exists(targetDll))
                {
                    needsDeploy = true;
                }
                else
                {
                    var srcFi = new FileInfo(asm.FullPath);
                    var tgtFi = new FileInfo(targetDll);
                    if (srcFi.Length != tgtFi.Length || Math.Abs((srcFi.LastWriteTimeUtc - tgtFi.LastWriteTimeUtc).TotalSeconds) > 2)
                    {
                        needsDeploy = true;
                    }
                }

                if (needsDeploy)
                {
                    try
                    {
                        if (File.Exists(targetDll)) File.Delete(targetDll);
                        bool linked = CreateHardLink(targetDll, asm.FullPath, IntPtr.Zero);
                        if (!linked)
                        {
                            File.Copy(asm.FullPath, targetDll, true);
                        }

                        if (!string.IsNullOrEmpty(asm.PdbPath) && File.Exists(asm.PdbPath))
                        {
                            if (File.Exists(targetPdb)) File.Delete(targetPdb);
                            bool pdbLinked = CreateHardLink(targetPdb, asm.PdbPath, IntPtr.Zero);
                            if (!pdbLinked)
                            {
                                File.Copy(asm.PdbPath, targetPdb, true);
                            }
                        }

                        stillDeployed.RemoveAll(d => d.AddinName.Equals(addinName, StringComparison.OrdinalIgnoreCase));
                        stillDeployed.Add(new DeployedAddinRecord
                        {
                            AddinName = addinName,
                            SourceDll = asm.FullPath,
                            TargetDll = targetDll,
                            TargetPdb = !string.IsNullOrEmpty(asm.PdbPath) && File.Exists(targetPdb) ? targetPdb : null,
                            DeployedAt = DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        App.Log($"Error deploying addin {addinName}: {ex.Message}");
                    }
                }
            }

            try
            {
                string newJson = System.Text.Json.JsonSerializer.Serialize(stillDeployed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(trackingPath, newJson, new UTF8Encoding(false));
            }
            catch { }

            // Sync DesignAddins.xml
            if (File.Exists(xmlPath))
            {
                string text = File.ReadAllText(xmlPath, Encoding.UTF8);
                bool changed = false;

                foreach (var name in enabledAddinNames)
                {
                    if (!text.Contains($"<string>{name}</string>", StringComparison.OrdinalIgnoreCase))
                    {
                        int closeIdx = text.IndexOf("</ArrayOfString>", StringComparison.OrdinalIgnoreCase);
                        if (closeIdx >= 0)
                        {
                            text = text.Insert(closeIdx, $"  <string>{name}</string>\r\n");
                            changed = true;
                        }
                    }
                }

                foreach (var rec in previouslyDeployed)
                {
                    if (!enabledAddinNames.Contains(rec.AddinName))
                    {
                        if (text.Contains($"<string>{rec.AddinName}</string>", StringComparison.OrdinalIgnoreCase))
                        {
                            text = Regex.Replace(text, $@"\s*<string>{Regex.Escape(rec.AddinName)}<\/string>", string.Empty, RegexOptions.IgnoreCase);
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    string bak = xmlPath + ".sep.bak";
                    if (!File.Exists(bak)) File.Copy(xmlPath, bak);
                    File.WriteAllText(xmlPath, text, new UTF8Encoding(false));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"SyncDesignAddins error: {ex.Message}");
        }
    }

    private void GenerateSepUic(string installDir, HashSet<string> enabled, IReadOnlyList<PluginInfo>? allPlugins = null)
    {
        string sepExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SEP.exe");
        if (!File.Exists(sepExePath)) sepExePath = @"C:\Muvsera\Projects\SEP\SEP.exe";
        string sepExeEscaped = sepExePath.Replace("\\", "/");

        var formsMetaList = new List<string>();
        var toolsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupsList = new List<string>();

        // Find all plugin directories
        var allPluginDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = PluginsDir;
        if (Directory.Exists(root))
        {
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                string fn = Path.GetFileName(d);
                if (!fn.StartsWith('.')) allPluginDirs[fn] = d;
            }
        }
        foreach (var d in DiscoverPluginDirsFromCustomEvars())
        {
            string fn = Path.GetFileName(d);
            if (!allPluginDirs.ContainsKey(fn)) allPluginDirs[fn] = d;
        }

        foreach (var name in enabled)
        {
            if (!allPluginDirs.TryGetValue(name, out var folder) || !Directory.Exists(folder)) continue;
            var info = allPlugins?.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? Inspect(folder, installDir, _catalog.Paths.E3dVersion);
            string safeName = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
            string normFolder = folder.Replace('\\', '/').TrimEnd('/');

            // Look for .uic file in resolved version folder or plugin root
            string? uicPath = null;
            if (info.ResolvedVersionDir != null && Directory.Exists(info.ResolvedVersionDir))
            {
                uicPath = Directory.EnumerateFiles(info.ResolvedVersionDir, "*.uic", SearchOption.AllDirectories).FirstOrDefault();
            }
            uicPath ??= Directory.EnumerateFiles(folder, "*.uic", SearchOption.AllDirectories)
                .OrderBy(f => Path.GetDirectoryName(f) == folder ? 0 : 1)
                .FirstOrDefault();

            if (uicPath != null && File.Exists(uicPath))
            {
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(uicPath);
                    var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

                    // 1. Extract FormsMetaData
                    var formsMetaElem = doc.Descendants(ns + "FormsMetaData").FirstOrDefault();
                    if (formsMetaElem != null)
                    {
                        foreach (var fm in formsMetaElem.Elements(ns + "FormMetaData"))
                        {
                            string fmXml = fm.ToString();
                            fmXml = Regex.Replace(fmXml, @"\s*xmlns=""[^""]*""", "");
                            if (!formsMetaList.Contains(fmXml))
                            {
                                formsMetaList.Add("    " + fmXml.Trim());
                            }
                        }
                    }

                    // 2. Extract Tools (preserving full original titles, tooltips, and attributes)
                    var toolsElem = doc.Descendants(ns + "Tools").FirstOrDefault();
                    if (toolsElem != null)
                    {
                        foreach (var tool in toolsElem.Elements())
                        {
                            string toolName = tool.Attribute("Name")?.Value ?? string.Empty;
                            if (string.IsNullOrEmpty(toolName)) continue;

                            // Safely redirect outdated macro paths ONLY if the original target does not exist
                            foreach (var macroElem in tool.Descendants(ns + "Macro"))
                            {
                                string macroText = macroElem.Value.Trim();
                                var match = Regex.Match(macroText, @"(?i)^\$m\s+([A-Za-z]:[\\/][^ \r\n\t]+)");
                                if (match.Success)
                                {
                                    string full = match.Groups[1].Value.Replace('\\', '/');
                                    if (!File.Exists(full))
                                    {
                                        int pmlIdx = full.IndexOf("/pmllib/", StringComparison.OrdinalIgnoreCase);
                                        int pdmsuiIdx = full.IndexOf("/pdmsui/", StringComparison.OrdinalIgnoreCase);
                                        if (pmlIdx >= 0)
                                        {
                                            string candidate = Path.Combine(folder, full[(pmlIdx + 1)..].Replace('/', '\\'));
                                            if (File.Exists(candidate)) macroElem.Value = $"$m {normFolder}{full[pmlIdx..]}";
                                        }
                                        else if (pdmsuiIdx >= 0)
                                        {
                                            string candidate = Path.Combine(folder, full[(pdmsuiIdx + 1)..].Replace('/', '\\'));
                                            if (File.Exists(candidate)) macroElem.Value = $"$m {normFolder}{full[pdmsuiIdx..]}";
                                        }
                                    }
                                }
                            }

                            string toolXml = tool.ToString();
                            toolXml = Regex.Replace(toolXml, @"\s*xmlns=""[^""]*""", "");
                            toolsMap[toolName] = "    " + toolXml.Trim();
                        }
                    }

                    // 3. Extract Groups from source UIC (preserving multi-group workflow structure)
                    var tabsElem = doc.Descendants(ns + "Tabs").FirstOrDefault();
                    bool hadGroups = false;
                    if (tabsElem != null)
                    {
                        var groups = tabsElem.Descendants(ns + "Group").ToList();
                        if (groups.Count > 0)
                        {
                            hadGroups = true;
                            foreach (var g in groups)
                            {
                                string gName = g.Attribute("Name")?.Value ?? $"SEP.Group.{safeName}";
                                string gCaption = g.Element(ns + "Caption")?.Value?.Trim() ?? PluginInfo.FormatDefaultDisplayName(name);
                                var toolsInGroup = g.Descendants(ns + "Tool").ToList();
                                if (toolsInGroup.Count == 0) continue;

                                var groupSb = new StringBuilder();
                                groupSb.AppendLine($"        <Group Name=\"{gName}\">");
                                groupSb.AppendLine("          <Tools>");
                                foreach (var tg in toolsInGroup)
                                {
                                    string tn = tg.Attribute("Name")?.Value ?? string.Empty;
                                    string isLarge = tg.Attribute("LargeImage")?.Value ?? "False";
                                    string imgOnly = tg.Attribute("ImageOnly")?.Value ?? "False";
                                    if (!string.IsNullOrEmpty(tn) && toolsMap.ContainsKey(tn))
                                    {
                                        groupSb.AppendLine($"            <Tool Name=\"{tn}\" LargeImage=\"{isLarge}\" ImageOnly=\"{imgOnly}\" />");
                                    }
                                }
                                groupSb.AppendLine("          </Tools>");
                                groupSb.AppendLine($"          <Caption>{gCaption}</Caption>");
                                groupSb.AppendLine("        </Group>");
                                groupsList.Add(groupSb.ToString());
                            }
                        }
                    }

                    if (hadGroups) continue;
                }
                catch (Exception ex)
                {
                    App.Log($"Parsing plugin UIC failed for {name}: {ex.Message}");
                }
            }

            // Fallback: auto-generate tools from Forms, Macros, and Functions
            var autoTools = new List<string>();
            var pluginGroupSb = new StringBuilder();

            // 1. If plugin has a dedicated launcher macro (e.g. launch_pipe_aligner.mac), prioritize it as primary button
            var launcherMacro = info.Macros.FirstOrDefault(m => Regex.IsMatch(m.Name, @"(?i)^(?:launch|start|run)_"));
            if (launcherMacro != null)
            {
                string toolKey = $"SEP.{safeName}.Launcher_{launcherMacro.Name}";
                string macroPath = launcherMacro.Path.Replace('\\', '/');
                string caption = info.EffectiveDisplayName;
                string toolXml = $@"    <ButtonTool Name=""{toolKey}"">
      <Command>
        <Type>Macro</Type>
        <Macro>$m {macroPath}</Macro>
        <Key>{toolKey}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>{caption}</Caption>
      <HelpContext />
      <Tooltip>Launch {info.EffectiveDisplayName} ($m {macroPath})</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";
                toolsMap[toolKey] = toolXml;
                autoTools.Add(toolKey);
            }

            foreach (var f in info.Forms)
            {
                string toolKey = $"SEP.{safeName}.Form_{f.Name}";
                if (toolsMap.ContainsKey(toolKey)) continue;
                string toolXml = $@"    <ButtonTool Name=""{toolKey}"">
      <Command>
        <Type>Macro</Type>
        <Macro>show !!{f.Name}</Macro>
        <Key>{toolKey}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>{f.Name}</Caption>
      <HelpContext />
      <Tooltip>Open {f.Name} form</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";
                toolsMap[toolKey] = toolXml;
                autoTools.Add(toolKey);
            }

            foreach (var m in info.Macros.Where(m => launcherMacro == null || !m.Path.Equals(launcherMacro.Path, StringComparison.OrdinalIgnoreCase)).Take(6))
            {
                string toolKey = $"SEP.{safeName}.Macro_{m.Name}";
                if (toolsMap.ContainsKey(toolKey)) continue;
                string macroPath = m.Path.Replace('\\', '/');
                string caption = m.Name;
                if (m.Name.Equals("ExportPml", StringComparison.OrdinalIgnoreCase)) caption = "导出 PML";
                string toolXml = $@"    <ButtonTool Name=""{toolKey}"">
      <Command>
        <Type>Macro</Type>
        <Macro>$m {macroPath}</Macro>
        <Key>{toolKey}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>{caption}</Caption>
      <HelpContext />
      <Tooltip>Execute macro {m.Name}</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";
                toolsMap[toolKey] = toolXml;
                autoTools.Add(toolKey);
            }

            // If no forms or macros, fallback to public PML functions
            if (autoTools.Count == 0 && info.Functions.Count > 0)
            {
                foreach (var fn in info.Functions.Take(4))
                {
                    string toolKey = $"SEP.{safeName}.Func_{fn.Name}";
                    if (toolsMap.ContainsKey(toolKey)) continue;
                    string toolXml = $@"    <ButtonTool Name=""{toolKey}"">
      <Command>
        <Type>Macro</Type>
        <Macro>{fn.CallCommand}</Macro>
        <Key>{toolKey}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>{fn.Name}</Caption>
      <HelpContext />
      <Tooltip>Execute function {fn.Name}</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";
                    toolsMap[toolKey] = toolXml;
                    autoTools.Add(toolKey);
                }
            }

            if (autoTools.Count == 0 && info.HasAddin)
            {
                foreach (var asm in info.AddinAssemblies)
                {
                    string toolKey = $"SEP.{safeName}.Addin_{asm.Name}";
                    string toolXml = $@"    <ButtonTool Name=""{toolKey}"">
      <Command>
        <Type>Macro</Type>
        <Macro>$P [SEP] {asm.Name} add-in is active</Macro>
        <Key>{toolKey}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>{info.EffectiveDisplayName}</Caption>
      <HelpContext />
      <Tooltip>AVEVA Add-in: {asm.Name}</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";
                    toolsMap[toolKey] = toolXml;
                    autoTools.Add(toolKey);
                }
            }

            if (autoTools.Count > 0)
            {
                string? customName = _catalog.GetPluginDisplayName(name);
                string groupCaption = !string.IsNullOrWhiteSpace(customName) ? customName : PluginInfo.FormatDefaultDisplayName(name);
                pluginGroupSb.AppendLine($"        <Group Name=\"SEP.Group.{safeName}\">");
                pluginGroupSb.AppendLine("          <Tools>");
                int idx = 0;
                foreach (var at in autoTools)
                {
                    string isLarge = idx < 2 ? "True" : "False";
                    pluginGroupSb.AppendLine($"            <Tool Name=\"{at}\" LargeImage=\"{isLarge}\" ImageOnly=\"False\" />");
                    idx++;
                }
                pluginGroupSb.AppendLine("          </Tools>");
                pluginGroupSb.AppendLine($"          <Caption>{groupCaption}</Caption>");
                pluginGroupSb.AppendLine("        </Group>");
                groupsList.Add(pluginGroupSb.ToString());
            }
        }

        // Always add standard Maintenance group
        string rehashKey = "SEP.Tools.PmlRehash";
        string openSepKey = "SEP.Tools.OpenSep";

        toolsMap[rehashKey] = @"    <ButtonTool Name=""SEP.Tools.PmlRehash"">
      <Command>
        <Type>Macro</Type>
        <Macro>PML REHASH ALL</Macro>
        <Key>SEP.Tools.PmlRehash</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_REFRESH</ImageResourceId>
      <Caption>刷新PML</Caption>
      <HelpContext />
      <Tooltip>重新加载PML库到内存 (PML REHASH ALL)</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";

        toolsMap[openSepKey] = $@"    <ButtonTool Name=""SEP.Tools.OpenSep"">
      <Command>
        <Type>Macro</Type>
        <Macro>syscom 'start """" ""{sepExeEscaped}""'</Macro>
        <Key>SEP.Tools.OpenSep</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>SEP 平台</Caption>
      <HelpContext />
      <Tooltip>启动 Smart E3D Platform 管理控制台</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";

        groupsList.Add(@"        <Group Name=""SEP.Group.Maintenance"">
          <Tools>
            <Tool Name=""SEP.Tools.PmlRehash"" LargeImage=""True"" ImageOnly=""False"" />
            <Tool Name=""SEP.Tools.OpenSep"" LargeImage=""True"" ImageOnly=""False"" />
          </Tools>
          <Caption>SEP 运维</Caption>
        </Group>");

        var formsMetaXml = new StringBuilder();
        foreach (var fm in formsMetaList)
        {
            formsMetaXml.AppendLine(fm);
        }

        var toolsXml = new StringBuilder();
        foreach (var t in toolsMap.Values)
        {
            toolsXml.AppendLine(t);
        }

        var groupsXml = new StringBuilder();
        foreach (var g in groupsList)
        {
            groupsXml.Append(g);
        }

        string fullUicXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<UserInterfaceCustomization xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns=""www.aveva.com"">
  <Version>1.0</Version>
  <FormsMetaData>
{formsMetaXml}  </FormsMetaData>
  <Tools>
{toolsXml}  </Tools>
  <InstanceTools />
  <MenuBar />
  <CommandBars />
  <TaskPanes />
  <ContextMenus />
  <AreaLeftTools />
  <AreaRightTools />
  <FooterTools />
  <NavigationMenuTools />
  <QATTools />
  <ContextualTabGroups />
  <TabToolbarTools />
  <Tabs>
    <Tab Name=""SEP.Tab"">
      <Caption>【SEP】</Caption>
      <Groups>
{groupsXml}      </Groups>
    </Tab>
  </Tabs>
  <MiniToolbarTools />
  <Namespace>SEP</Namespace>
</UserInterfaceCustomization>
";

        string finalUicPath = Path.Combine(installDir, "SEP.uic");
        File.WriteAllText(finalUicPath, fullUicXml, new UTF8Encoding(false));
    }
}

