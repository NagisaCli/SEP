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
/// Switches the active E3D project the same way the Python launcher (e3d_launcher.py, mode "single") does:
/// the project's evars*.bat is registered in the managed block of the local custom_evars.bat, and the
/// projects_dir= line of evars.bat / evars.init is pointed at the local project library so E3D reads that
/// custom_evars.bat. Every touched file is backed up first and restored if any step fails.
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

    private readonly IE3dProjectService _projectService;
    private string? _cachedShortcut;   // resolved once per process: the Start Menu walk is slow

    public E3dLauncherService(IE3dProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<(bool Success, string Message)> SwitchEnvironmentAsync(ProjectItem project)
    {
        return await Task.Run(() =>
        {
            var backups = new Dictionary<string, byte[]?>();
            try
            {
                var paths = _projectService.PathsConfig;
                string? projectsDir = paths.ProjectsDir;
                if (string.IsNullOrWhiteSpace(projectsDir))
                    return (false, Strings.Launch_ProjectsDirMissing);

                // 1. Locate the project's evars bat
                string targetBat = Path.Combine(project.Path, $"evars{project.Code}.bat");
                if (!File.Exists(targetBat))
                {
                    var cand = Directory.GetFiles(project.Path, "evars*.bat");
                    if (cand.Length > 0) targetBat = cand[0];
                }
                if (!File.Exists(targetBat))
                    return (false, string.Format(Strings.Launch_EvarsMissing, project.Code));
                if (targetBat.IndexOfAny(DangerousBatChars) >= 0)
                    return (false, string.Format(Strings.Launch_UnsafePath, targetBat));

                Directory.CreateDirectory(projectsDir);
                string customEvars = CustomEvarsPath(projectsDir);

                // 2. Back up everything we may touch, then write
                foreach (var f in new[] { customEvars, paths.EvarsBat, paths.EvarsInit })
                {
                    if (!string.IsNullOrEmpty(f)) backups[f] = File.Exists(f) ? File.ReadAllBytes(f) : null;
                }

                WriteManagedBlock(customEvars, new[] { targetBat });

                string localDir = projectsDir.TrimEnd('\\', '/') + "\\";
                foreach (var f in new[] { paths.EvarsBat, paths.EvarsInit })
                {
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) SetProjectsDir(f, localDir);
                }

                // 3. Remember the active project
                _projectService.SetActiveProjectAsync(project.Code).Wait();

                return (true, string.Format(Strings.Launch_SwitchSuccess, project.Code));
            }
            catch (Exception ex)
            {
                Restore(backups);
                return (false, string.Format(Strings.Launch_SwitchFailed, ex.Message));
            }
        });
    }

    public async Task<(bool Success, string Message)> LaunchE3dProcessAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                // 1. The installed shortcut carries the right arguments and working directory
                string? lnk = FindShortcut();
                if (lnk != null)
                {
                    Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                    return (true, Strings.Launch_ViaShortcut);
                }

                // 2. mon.exe next to evars.bat (or in install_dir), started the way the shortcut would
                string? installDir = _projectService.PathsConfig.InstallDir;
                string? evarsDir = string.IsNullOrEmpty(_projectService.PathsConfig.EvarsBat) ? null : Path.GetDirectoryName(_projectService.PathsConfig.EvarsBat);
                foreach (var dir in new[] { evarsDir, installDir })
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

    public async Task<(bool Success, string Message)> SwitchAndLaunchAsync(ProjectItem project, string? module = "Design")
    {
        var switchRes = await SwitchEnvironmentAsync(project);
        if (!switchRes.Success) return switchRes;

        var launchRes = await LaunchE3dProcessAsync();
        return (launchRes.Success, $"[{project.Code}] {launchRes.Message}");
    }

    // ── custom_evars.bat managed block ──────────────────────────────────────────────

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
        if (File.Exists(customEvarsPath)) (text, enc) = ReadTextSmart(customEvarsPath);
        else (text, enc) = (string.Empty, Encoding.GetEncoding("GBK"));

        text = ManagedBlockRe.Replace(text, string.Empty);
        text = text.Trim().Length > 0
            ? text.TrimEnd('\r', '\n') + "\r\n\r\n" + block
            : "@echo off\r\n\r\n" + block;

        File.WriteAllText(customEvarsPath, text, enc);
    }

    // ── projects_dir= in evars.bat / evars.init ─────────────────────────────────────

    /// <summary>
    /// Rewrites the projects_dir= value byte-for-byte safely: the file is handled as Latin-1 so every byte maps
    /// to one char, and only the replacement value is encoded with the file's real encoding (UTF-8 or GBK).
    /// </summary>
    private static void SetProjectsDir(string filePath, string newDir)
    {
        if (newDir.IndexOfAny(DangerousBatChars) >= 0)
            throw new InvalidOperationException(string.Format(Strings.Launch_UnsafePath, newDir));

        byte[] data = File.ReadAllBytes(filePath);
        var latin1 = Encoding.Latin1;
        string raw = latin1.GetString(data);
        var m = ProjectsDirRe.Match(raw);
        if (!m.Success)
            throw new InvalidOperationException(string.Format(Strings.Launch_ProjectsDirLineMissing, Path.GetFileName(filePath)));

        Encoding fileEnc = DetectEncoding(data);
        string newValueRaw = latin1.GetString(fileEnc.GetBytes(newDir));
        if (m.Value.Substring(m.Groups[1].Length) == newValueRaw) return;   // already pointing there: leave the file alone

        string updated = ProjectsDirRe.Replace(raw, mm => mm.Groups[1].Value + newValueRaw, 1);
        string backup = filePath + ".sep.bak";
        if (!File.Exists(backup)) File.Copy(filePath, backup);
        File.WriteAllBytes(filePath, latin1.GetBytes(updated));
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

    // ── encoding helpers (UTF-8 with/without BOM, otherwise GBK — what AVEVA files use) ──

    private static Encoding DetectEncoding(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8;
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GBK");
        }
    }

    private static (string Text, Encoding Encoding) ReadTextSmart(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var enc = DetectEncoding(data);
        return (enc.GetString(data), enc);
    }

    // ── shortcut lookup ─────────────────────────────────────────────────────────────

    private string? FindShortcut()
    {
        if (_cachedShortcut != null) return File.Exists(_cachedShortcut) ? _cachedShortcut : null;

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
