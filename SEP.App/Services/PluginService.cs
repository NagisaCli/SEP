using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

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
    private static readonly Regex BlockRe = new(@"(?ms)^[ \t]*" + Regex.Escape(BlockStart) + @".*?" + Regex.Escape(BlockEnd) + @"[ \t]*\r?\n?", RegexOptions.Compiled);
    private static readonly Regex ProjectsBlockRe = new(@"(?ms)^[ \t]*" + Regex.Escape(ProjectsBlockStart) + @".*?" + Regex.Escape(ProjectsBlockEnd) + @"[ \t]*\r?\n?", RegexOptions.Compiled);
    private static readonly Regex EnabledRe = new(@"set\s+(?:PMLLIB|PMLNET|PMLUI|AVEVA_DESIGN_DFLTS)=[^\r\n]*?\\Plugins\\([^\\\r\n;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> PmlExts = new(StringComparer.OrdinalIgnoreCase) { ".pmlfrm", ".pmlobj", ".pmlfnc", ".pmlcmd", ".pmlmac" };
    private static readonly Regex FormRe = new(@"setup\s+form\s+!!([a-zA-Z0-9_]+)(?:\s+(dialog|modal|dockable|main))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObjectRe = new(@"define\s+object\s+([a-zA-Z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FunctionRe = new(@"define\s+function\s+!!?([a-zA-Z0-9_]+)\s*\((.*?)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int MaxParseBytes = 4 * 1024 * 1024;

    private readonly IProjectCatalog _catalog;

    public PluginService(IProjectCatalog catalog)
    {
        _catalog = catalog;
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
    }

    private string CustomEvarsPath
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
        if (!Directory.Exists(root)) return result;
        var enabled = new HashSet<string>(ReadEnabled(), StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase); }
        catch { return result; }

        foreach (var dir in entries)
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            var info = Inspect(dir);
            if (autoHeal && info.HasPmlLib && info.IndexNeedsRebuild && info.PmlFileCount > 0)
            {
                if (RebuildIndex(info.PmlLibPath!).Ok) info = Inspect(dir);
            }
            info.Enabled = enabled.Contains(name);
            result.Add(info);
        }
        return result;
    }

    private static PluginInfo Inspect(string folder)
    {
        string name = Path.GetFileName(folder);
        string? pmllib = null, pmlui = null, pmlnet = null, dflts = null;
        var diagnostics = new List<string>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                switch (Path.GetFileName(sub).ToLowerInvariant())
                {
                    case "pmllib": pmllib = sub; break;
                    case "pdmsui": case "pmlui": pmlui = sub; break;
                    case "bin": pmlnet = sub; break;
                    case "dflts": case "defaults": dflts = sub; break;
                }
            }
            if (pmllib == null && Directory.EnumerateFiles(folder).Any(f => PmlExts.Contains(Path.GetExtension(f)))) pmllib = folder;
        }
        catch (Exception ex)
        {
            diagnostics.Add(string.Format(Strings.Plugins_DiagUnreadable, ex.Message));
        }

        var forms = new List<PluginSymbol>(); var objects = new List<PluginSymbol>(); var functions = new List<PluginSymbol>();
        var macros = new List<PluginSymbol>(); var assemblies = new List<PluginSymbol>(); var uics = new List<PluginSymbol>();
        int pmlFiles = 0;
        foreach (var file in EnumerateFiles(folder))
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
                        pmlFiles++;
                        string text = ReadHead(file);
                        var m = FormRe.Match(text);
                        string fn = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fname);
                        forms.Add(new PluginSymbol("form", fn, fname, file, $"show !!{fn}"));
                        break;
                    }
                    case ".pmlobj":
                    {
                        pmlFiles++;
                        var m = ObjectRe.Match(ReadHead(file));
                        string on = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fname);
                        objects.Add(new PluginSymbol("object", on, fname, file, null));
                        break;
                    }
                    case ".pmlfnc":
                    {
                        pmlFiles++;
                        var m = FunctionRe.Match(ReadHead(file));
                        string fn = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fname);
                        string args = m.Success ? m.Groups[2].Value.Trim() : string.Empty;
                        functions.Add(new PluginSymbol("function", fn, fname, file, $"!!{fn}({args})"));
                        break;
                    }
                    case ".pmlcmd": case ".pmlmac":
                        pmlFiles++;
                        macros.Add(new PluginSymbol("macro", Path.GetFileNameWithoutExtension(fname), fname, file, $"$m {file}"));
                        break;
                    case ".mac":
                        macros.Add(new PluginSymbol("macro", Path.GetFileNameWithoutExtension(fname), fname, file, $"$m {file}"));
                        break;
                    case ".dll":
                    {
                        string asm = Path.GetFileNameWithoutExtension(fname);
                        bool framework = asm.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || asm.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                                         || asm.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) || asm.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase);
                        if (!framework) assemblies.Add(new PluginSymbol("assembly", asm, fname, file, $"import '{asm}'"));
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
        if (pmllib != null)
        {
            string idx = Path.Combine(pmllib, "pml.index");
            if (File.Exists(idx))
            {
                try
                {
                    indexCount = File.ReadLines(idx).Count(l => l.Trim().Length > 0 && !l.StartsWith('/'));
                    status = indexCount == pmlFiles ? "ok" : "outdated";
                    if (status == "outdated") diagnostics.Add(string.Format(Strings.Plugins_DiagIndexOutdated, indexCount, pmlFiles));
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
                if (pmlFiles > 0) diagnostics.Add(Strings.Plugins_DiagIndexMissing);
            }
        }
        if (pmlnet != null && assemblies.Count == 1) diagnostics.Add(Strings.Plugins_DiagSingleDll);

        var entry = new List<string>();
        entry.AddRange(forms.Select(f => f.CallCommand!));
        if (entry.Count == 0) entry.AddRange(functions.Select(f => f.CallCommand!));
        if (entry.Count == 0) entry.AddRange(macros.Select(m => m.CallCommand!));
        entry.AddRange(assemblies.Select(a => a.CallCommand!));

        var hot = new List<string>();
        if (pmllib != null) { hot.Add($"pml index '{pmllib}'"); hot.Add("pml rehash all"); }
        hot.AddRange(assemblies.Select(a => a.CallCommand!));

        return new PluginInfo
        {
            Name = name, Path = folder, PmlLibPath = pmllib, PmlUiPath = pmlui, PmlNetPath = pmlnet, DfltsPath = dflts,
            PmlIndexStatus = status, PmlIndexCount = indexCount, PmlFileCount = pmlFiles,
            Forms = forms, Objects = objects, Functions = functions, Macros = macros, Assemblies = assemblies, UicConfigs = uics,
            Diagnostics = diagnostics, EntryCommands = entry, HotloadCommands = hot,
        };
    }

    private static IEnumerable<string> EnumerateFiles(string folder)
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files, subs;
            try { files = Directory.EnumerateFiles(dir).ToList(); subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
            {
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

    // ── enable / disable through custom_evars.bat ───────────────────────────────

    public List<string> ReadEnabled()
    {
        string custom = CustomEvarsPath;
        if (!File.Exists(custom)) return new List<string>();
        string text;
        try { text = SepPaths.ReadTextSmart(custom).Text; } catch { return new List<string>(); }
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || t.StartsWith("::")) continue;
            var m = EnabledRe.Match(t);
            if (m.Success) enabled.Add(m.Groups[1].Value.Trim());
        }
        return enabled.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<ToolResult> SetEnabledAsync(string name, bool enabled) => Task.Run(() =>
    {
        try
        {
            var current = new HashSet<string>(ReadEnabled(), StringComparer.OrdinalIgnoreCase);
            if (enabled) current.Add(name); else current.Remove(name);
            WriteBlock(current.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
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

    private string BuildBlock(List<string> enabledNames)
    {
        var lines = new List<string> { BlockStart };
        string root = PluginsDir;
        foreach (var name in enabledNames)
        {
            string dir = Path.Combine(root, name);
            if (!Directory.Exists(dir)) continue;
            var info = Inspect(dir);
            lines.Add($"rem --- E3D Plugin: {name} ---");
            if (info.PmlLibPath != null) lines.Add($"if exist \"{info.PmlLibPath}\" set PMLLIB=%PMLLIB%;{info.PmlLibPath}");
            if (info.PmlUiPath != null) lines.Add($"if exist \"{info.PmlUiPath}\" set PMLUI=%PMLUI%;{info.PmlUiPath}");
            if (info.PmlNetPath != null) lines.Add($"if exist \"{info.PmlNetPath}\" set PMLNET=%PMLNET%;{info.PmlNetPath}");
            if (info.DfltsPath != null) lines.Add($"if exist \"{info.DfltsPath}\" set AVEVA_DESIGN_DFLTS=%AVEVA_DESIGN_DFLTS%;{info.DfltsPath}");
        }
        lines.Add(BlockEnd);
        return string.Join("\r\n", lines);
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
        if (groups.Count == 0) return (false, Strings.Plugins_IndexNoFiles);

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
        foreach (var p in plugins.Where(p => p.HasPmlLib))
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
            "",
            "-- 1. Index PML Libraries",
        };
        lines.AddRange(enabled.Where(p => p.HasPmlLib).Select(p => $"pml index '{p.PmlLibPath}'"));
        lines.Add(""); lines.Add("-- 2. Rehash all PML definitions into memory"); lines.Add("pml rehash all");
        lines.Add(""); lines.Add("-- 3. Import PML.NET Assemblies");
        lines.AddRange(enabled.SelectMany(p => p.Assemblies).Select(a => a.CallCommand!));
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

    /// <summary>The PMLLIB / PMLUI / PMLNET / DFLTS entries E3D ends up with, in order: install defaults first, then enabled plug-ins.</summary>
    public List<(string Variable, string Source, string Path)> ResolutionChain(IEnumerable<PluginInfo> plugins)
    {
        var chain = new List<(string, string, string)>();
        string install = SepPaths.Normalize(_catalog.Paths.InstallDir);
        if (install.Length > 0)
        {
            chain.Add(("PMLLIB", "E3D", Path.Combine(install, "pmllib")));
            chain.Add(("PMLUI", "E3D", Path.Combine(install, "PMLUI")));
        }
        // hand-written entries of custom_evars.bat outside the managed blocks
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
                    var m = Regex.Match(line.Trim(), @"^(?:if\s+exist\s+""[^""]*""\s+)?set\s+(PMLLIB|PMLUI|PMLNET|AVEVA_DESIGN_DFLTS)=(.+)$", RegexOptions.IgnoreCase);
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
        foreach (var p in plugins.Where(p => p.Enabled))
        {
            if (p.PmlLibPath != null) chain.Add(("PMLLIB", p.Name, p.PmlLibPath));
            if (p.PmlUiPath != null) chain.Add(("PMLUI", p.Name, p.PmlUiPath));
            if (p.PmlNetPath != null) chain.Add(("PMLNET", p.Name, p.PmlNetPath));
            if (p.DfltsPath != null) chain.Add(("AVEVA_DESIGN_DFLTS", p.Name, p.DfltsPath));
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
}
