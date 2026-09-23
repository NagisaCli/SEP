using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

/// <summary>
/// Switches what E3D loads, the way e3d_launcher.py does it:
///  - mode "single":  the project's evars bat goes into the managed block of the local library's custom_evars.bat
///                    and projects_dir= in evars.bat / evars.init points at that local library;
///  - mode "library": projects_dir= points at the library itself (a custom_evars.bat is generated there if missing).
/// Every touched file is backed up first and restored if any step fails.
/// </summary>
public class E3dLauncherService : IE3dLauncherService
{
    // Same markers as the Python tool, so both manage one block.
    private const string ManagedStart = ":: >>> SEP MANAGED PROJECTS (do not edit) >>>";
    private const string ManagedEnd = ":: <<< SEP MANAGED PROJECTS <<<";
    private static readonly Regex ManagedBlockRe = new(
        @"(?ms)^[ \t]*" + Regex.Escape(ManagedStart) + @".*?" + Regex.Escape(ManagedEnd) + @"[ \t]*\r?\n?", RegexOptions.Compiled);
    private static readonly Regex ProjectsDirRe = new(@"(?im)^(\s*(?:set\s+)?projects_dir=)[^\r\n]*", RegexOptions.Compiled);
    private static readonly char[] DangerousBatChars = { '"', '\r', '\n', '\0', '&', '|', '<', '>', '^' };
    private static readonly TimeSpan PathCheckTimeout = TimeSpan.FromSeconds(6);

    private readonly IProjectCatalog _catalog;
    private readonly SessionService _sessions;
    private readonly PluginService? _pluginService;
    private string? _cachedShortcut;   // resolved once per process: the Start Menu walk is slow

    public E3dLauncherService(IProjectCatalog catalog, SessionService sessions, PluginService? pluginService = null)
    {
        _catalog = catalog;
        _sessions = sessions;
        _pluginService = pluginService;
    }

    public async Task<(bool Success, string Message)> SwitchAndLaunchAsync(ProjectItem project)
    {
        var (ok, msg) = await Task.Run(() => SwitchSingle(project));
        if (!ok) return (false, msg);
        var launch = await LaunchE3dProcessAsync();
        if (launch.Success) _sessions.Register(new[] { project });
        return (launch.Success, $"[{project.Name}] {launch.Message}");
    }

    public async Task<(bool Success, string Message)> LoadLibraryAndLaunchAsync(LibraryItem library)
    {
        var (ok, msg) = await Task.Run(() => SwitchLibrary(library));
        if (!ok) return (false, msg);
        var launch = await LaunchE3dProcessAsync();
        return (launch.Success, $"[{library.Name}] {launch.Message}");
    }

    public async Task<(bool Success, string Message)> LoadMyProjectsAndLaunchAsync()
    {
        var (ok, msg) = await Task.Run(SwitchAll);
        if (!ok) return (false, msg);
        var launch = await LaunchE3dProcessAsync();
        if (launch.Success) _sessions.Register(_catalog.MyProjects);
        return (launch.Success, $"{msg} {launch.Message}");
    }

    public async Task<(bool Success, string Message)> SwitchAndLaunchMultipleAsync(IEnumerable<ProjectItem> projects)
    {
        var list = projects.ToList();
        if (list.Count == 0) return (false, Strings.Launch_NoMyProjects);
        var (ok, msg) = await Task.Run(() => SwitchMultipleProjects(list, isAllMine: false));
        if (!ok) return (false, msg);
        var launch = await LaunchE3dProcessAsync();
        if (launch.Success) _sessions.Register(list);
        return (launch.Success, $"{msg} {launch.Message}");
    }

    public Task<(bool Success, string Message)> SwitchAsync(ProjectItem project) => Task.Run(() => SwitchSingle(project));

    /// <summary>Mode "all" (e3d_launcher.write_mode): the managed block lists every project of "my projects".</summary>
    private (bool, string) SwitchAll() => SwitchMultipleProjects(_catalog.MyProjects, isAllMine: true);

    private (bool, string) SwitchMultipleProjects(IReadOnlyList<ProjectItem> targetProjects, bool isAllMine)
    {
        if (targetProjects.Count == 0) return (false, Strings.Launch_NoMyProjects);

        var backups = new Dictionary<string, byte[]?>();
        try
        {
            var bats = new List<string>();
            var slowOrOffline = new List<string>();
            foreach (var p in targetProjects)
            {
                if (p.BatPath.IndexOfAny(DangerousBatChars) >= 0) return (false, string.Format(Strings.Launch_UnsafePath, p.BatPath));
                // Crucial fix: never drop projects configured in MyProjects or selected by user.
                // Each line in custom_evars.bat is protected by 'if exist "%p%" call "%p%"'.
                bats.Add(p.BatPath);

                // Quick non-blocking probe to report any paths currently taking long
                if (!ExistsWithin(p.BatPath, TimeSpan.FromMilliseconds(400)))
                {
                    slowOrOffline.Add(p.Name);
                }
            }

            string localDir = _catalog.LocalProjectsDir;
            Directory.CreateDirectory(localDir);
            string customEvars = CustomEvarsPath(localDir);

            var (evarsBat, evarsInit) = RequireEvars();
            Backup(backups, customEvars, evarsBat, evarsInit);

            WriteManagedBlock(customEvars, bats);
            SetProjectsDir(evarsBat, localDir);
            SetProjectsDir(evarsInit, localDir);

            if (isAllMine)
            {
                _catalog.SetLastLaunchedAll();
            }
            else if (targetProjects.Count == 1)
            {
                _catalog.SetLastLaunched(targetProjects[0], "single");
            }
            else
            {
                _catalog.SetLastLaunchedAll();
            }

            string msg = string.Format(Strings.Launch_AllLoaded, bats.Count);
            if (slowOrOffline.Count > 0)
            {
                msg += " " + string.Format(Strings.Launch_AllSkipped, string.Join(", ", slowOrOffline));
            }
            return (true, msg);
        }
        catch (Exception ex)
        {
            Restore(backups);
            return (false, string.Format(Strings.Launch_SwitchFailed, ex.Message));
        }
    }

    private (bool, string) SwitchSingle(ProjectItem project)
    {
        var backups = new Dictionary<string, byte[]?>();
        try
        {
            string bat = project.BatPath;
            if (bat.IndexOfAny(DangerousBatChars) >= 0) return (false, string.Format(Strings.Launch_UnsafePath, bat));
            if (!ExistsWithin(bat, PathCheckTimeout)) return (false, string.Format(Strings.Launch_ProjectFileUnreachable, bat));

            string localDir = _catalog.LocalProjectsDir;
            Directory.CreateDirectory(localDir);
            string customEvars = CustomEvarsPath(localDir);

            var (evarsBat, evarsInit) = RequireEvars();
            Backup(backups, customEvars, evarsBat, evarsInit);

            WriteManagedBlock(customEvars, new[] { bat });
            SetProjectsDir(evarsBat, localDir);
            SetProjectsDir(evarsInit, localDir);

            _catalog.SetLastLaunched(project, "single");
            return (true, string.Format(Strings.Launch_SwitchSuccess, project.Name));
        }
        catch (Exception ex)
        {
            Restore(backups);
            return (false, string.Format(Strings.Launch_SwitchFailed, ex.Message));
        }
    }

    private (bool, string) SwitchLibrary(LibraryItem library)
    {
        var backups = new Dictionary<string, byte[]?>();
        try
        {
            string libDir = library.Path;
            if (libDir.IndexOfAny(DangerousBatChars) >= 0) return (false, string.Format(Strings.Launch_UnsafePath, libDir));
            if (!ExistsWithin(libDir, PathCheckTimeout) || !Directory.Exists(libDir))
                return (false, string.Format(Strings.Launch_LibraryUnreachable, libDir));

            var (evarsBat, evarsInit) = RequireEvars();
            Backup(backups, evarsBat, evarsInit);

            EnsureLibraryCustomEvars(libDir, _catalog.LocalProjectsDir);
            SetProjectsDir(evarsBat, libDir);
            SetProjectsDir(evarsInit, libDir);

            _catalog.SetLastLaunchedLibrary(library);
            return (true, string.Format(Strings.Launch_Library, library.Name));
        }
        catch (Exception ex)
        {
            Restore(backups);
            return (false, string.Format(Strings.Launch_SwitchFailed, ex.Message));
        }
    }

    public async Task<(bool Success, string Message)> LaunchE3dProcessAsync()
    {
        _pluginService?.SyncE3dRibbon();
        return await Task.Run(() =>
        {
            try
            {
                // 1. The shortcut carries the right arguments and working directory
                string? lnk = FindShortcut();
                if (lnk != null)
                {
                    Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                    return (true, Strings.Launch_ViaShortcut);
                }

                // 2. mon.exe next to evars.bat (or in install_dir), started the way the shortcut would
                var paths = _catalog.Paths;
                string? evarsDir = string.IsNullOrEmpty(paths.EvarsBat) ? null : Path.GetDirectoryName(paths.EvarsBat);
                foreach (var dir in new[] { evarsDir, paths.InstallDir })
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    string monExe = Path.Combine(dir, "mon.exe");
                    if (!File.Exists(monExe)) continue;

                    string initFile = Path.Combine(dir, "launch.init");
                    string userData = @"D:\AVEVA\USERDATA";
                    var psi = new ProcessStartInfo(monExe)
                    {
                        WorkingDirectory = Directory.Exists(userData) ? userData : dir,
                        UseShellExecute = true,
                    };
                    if (File.Exists(initFile))
                    {
                        psi.ArgumentList.Add("PROD"); psi.ArgumentList.Add("E3D"); psi.ArgumentList.Add("init"); psi.ArgumentList.Add(initFile);
                    }
                    Process.Start(psi);
                    return (true, Strings.Launch_ViaMonExe);
                }

                return (false, Strings.Launch_NotFound);
            }
            catch (Exception ex)
            {
                return (false, string.Format(Strings.Launch_Failed, ex.Message));
            }
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private (string EvarsBat, string EvarsInit) RequireEvars()
    {
        var paths = _catalog.Paths;
        string? bat = paths.EvarsBat, init = paths.EvarsInit;
        if (string.IsNullOrEmpty(bat) || !File.Exists(bat) || string.IsNullOrEmpty(init) || !File.Exists(init))
            throw new InvalidOperationException(Strings.Diag_EvarsMissing);
        return (bat, init);
    }

    private static bool ExistsWithin(string path, TimeSpan timeout)
    {
        var work = Task.Run(() => File.Exists(path) || Directory.Exists(path));
        return work.Wait(timeout) && work.Result;
    }

    private static void Backup(Dictionary<string, byte[]?> backups, params string[] files)
    {
        foreach (var f in files)
        {
            if (!string.IsNullOrEmpty(f)) backups[f] = File.Exists(f) ? File.ReadAllBytes(f) : null;
        }
    }

    private static void Restore(Dictionary<string, byte[]?> backups)
    {
        foreach (var (path, content) in backups)
        {
            try
            {
                if (content == null) { if (File.Exists(path)) File.Delete(path); }
                else File.WriteAllBytes(path, content);
            }
            catch { }
        }
    }

    /// <summary>Local custom_evars.bat; the misspelt custom_evar.bat is honoured when it already exists.</summary>
    private static string CustomEvarsPath(string localDir)
    {
        foreach (var name in new[] { "custom_evars.bat", "custom_evar.bat" })
        {
            string p = Path.Combine(localDir, name);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(localDir, "custom_evars.bat");
    }

    private static void WriteManagedBlock(string customEvarsPath, IEnumerable<string> projectBats)
    {
        var lines = new List<string> { ManagedStart };
        foreach (var p in projectBats) lines.Add($"if exist \"{p}\" call \"{p}\"");
        lines.Add(ManagedEnd);
        string block = string.Join("\r\n", lines) + "\r\n";

        string text; Encoding enc;
        if (File.Exists(customEvarsPath)) (text, enc) = SepPaths.ReadTextSmart(customEvarsPath);
        else (text, enc) = (string.Empty, SepPaths.Gbk);

        text = ManagedBlockRe.Replace(text, string.Empty);
        text = text.Trim().Length > 0
            ? text.TrimEnd('\r', '\n') + "\r\n\r\n" + block
            : "@echo off\r\n\r\n" + block;

        File.WriteAllText(customEvarsPath, text, enc);
    }

    /// <summary>Library mode needs a custom_evars.bat in the library; generate a %~dp0-relative one from its project folders when absent.</summary>
    private static void EnsureLibraryCustomEvars(string libraryDir, string localProjectsDir)
    {
        string custom = Path.Combine(libraryDir, "custom_evars.bat");
        if (File.Exists(custom))
        {
            try
            {
                string localCustom = CustomEvarsPath(localProjectsDir);
                if (File.Exists(localCustom))
                {
                    var (localText, _) = SepPaths.ReadTextSmart(localCustom);
                    var pm = PluginService.BlockRe.Match(localText);
                    if (pm.Success)
                    {
                        var (libText, libEnc) = SepPaths.ReadTextSmart(custom);
                        if (!PluginService.BlockRe.IsMatch(libText))
                        {
                            libText = libText.TrimEnd('\r', '\n') + "\r\n\r\n" + pm.Value.Trim() + "\r\n";
                            File.WriteAllText(custom, libText, libEnc);
                        }
                    }
                }
            }
            catch { }
            return;
        }

        var lines = new List<string>
        {
            "rem --------------------------------------------------",
            "rem Auto-generated by SEP for E3D library mode",
            "rem --------------------------------------------------",
        };
        foreach (var sub in Directory.EnumerateDirectories(libraryDir).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            string? bat = Directory.EnumerateFiles(sub, "evars*.bat")
                .FirstOrDefault(f => !Path.GetFileName(f).Equals("evars.bat", StringComparison.OrdinalIgnoreCase));
            if (bat != null) lines.Add($"if exist \"%~dp0{Path.GetFileName(sub)}\\{Path.GetFileName(bat)}\" call \"%~dp0{Path.GetFileName(sub)}\\{Path.GetFileName(bat)}\"");
        }

        try
        {
            string localCustom = CustomEvarsPath(localProjectsDir);
            if (File.Exists(localCustom))
            {
                var (localText, _) = SepPaths.ReadTextSmart(localCustom);
                var pm = PluginService.BlockRe.Match(localText);
                if (pm.Success)
                {
                    lines.Add("");
                    lines.Add(pm.Value.Trim());
                }
            }
        }
        catch { }

        if (lines.Count > 3) File.WriteAllText(custom, string.Join("\r\n", lines) + "\r\n", SepPaths.Gbk);
    }

    /// <summary>
    /// Rewrites the projects_dir= value byte-for-byte safely: the file is handled as Latin-1 so every byte maps
    /// to one char, and only the replacement value is encoded with the file's real encoding (UTF-8 or GBK).
    /// </summary>
    private static void SetProjectsDir(string filePath, string newDir)
    {
        newDir = newDir.TrimEnd('\\', '/') + "\\";
        if (newDir.IndexOfAny(DangerousBatChars) >= 0)
            throw new InvalidOperationException(string.Format(Strings.Launch_UnsafePath, newDir));

        byte[] data = File.ReadAllBytes(filePath);
        var latin1 = Encoding.Latin1;
        string raw = latin1.GetString(data);
        var m = ProjectsDirRe.Match(raw);
        if (!m.Success)
            throw new InvalidOperationException(string.Format(Strings.Launch_ProjectsDirLineMissing, Path.GetFileName(filePath)));

        Encoding fileEnc = SepPaths.DetectEncoding(data);
        string newValueRaw = latin1.GetString(fileEnc.GetBytes(newDir));
        if (string.Equals(m.Value.Substring(m.Groups[1].Length), newValueRaw, StringComparison.OrdinalIgnoreCase)) return;

        string updated = ProjectsDirRe.Replace(raw, mm => mm.Groups[1].Value + newValueRaw, 1);
        string backup = filePath + ".sep.bak";
        if (!File.Exists(backup)) File.Copy(filePath, backup);
        File.WriteAllBytes(filePath, latin1.GetBytes(updated));
    }

    // ── shortcut lookup ─────────────────────────────────────────────────────────────

    private string? FindShortcut()
    {
        string configured = SepPaths.Normalize(_catalog.Data.Settings.E3dLnk);
        if (configured.Length > 0 && File.Exists(configured)) return configured;
        if (_cachedShortcut != null && File.Exists(_cachedShortcut)) return _cachedShortcut;

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exact = new[]
        {
            Path.Combine(programData, @"Microsoft\Windows\Start Menu\Programs\AVEVA\Design\AVEVA Everything3D 3.1.lnk"),
            Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\AVEVA\Design\AVEVA Everything3D 3.1.lnk"),
        };
        string? found = exact.FirstOrDefault(File.Exists);

        if (found == null)
        {
            var roots = new[]
            {
                Path.Combine(programData, @"Microsoft\Windows\Start Menu"),
                Path.Combine(appData, @"Microsoft\Windows\Start Menu"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };
            string? loose = null;
            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileName(lnk).ToLowerInvariant();
                        if (name.Contains("everything3d 3.1")) { found = lnk; break; }
                        if (name.Contains("everything3d")) { found ??= lnk; }
                        else if (name.Contains("e3d")) { loose ??= lnk; }
                    }
                }
                catch { }
                if (found != null) break;
            }
            found ??= loose;
        }

        _cachedShortcut = found;
        return found;
    }
}
