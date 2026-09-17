using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public enum LibraryKind { Collection, Project, Invalid, Unsupported }

/// <summary>Outcome of scanning one path. <see cref="Reason"/> is localized and shown in the UI.</summary>
public sealed record ScanResult(LibraryKind Kind, string Reason, List<ProjectRecord> Projects, bool TimedOut = false)
{
    public bool Ok => Kind is LibraryKind.Collection or LibraryKind.Project;
}

/// <summary>
/// Project discovery, same rules as e3d_scanner.py:
///  1. the path is an evarsXXX.bat file                       → single project
///  2. the folder itself contains evarsXXX.bat                → project folder
///  3. sub-folders (one level) containing evarsXXX.bat        → collection (each sub-folder is a project)
///  4. user-written "call" lines in custom_evars.bat are projects too, except SEP's managed block and
///     infrastructure files (projects.bat / evars.bat / custom_evars.bat).
/// Network paths get a TCP probe first and every listing runs under a deadline; sub-folders are probed in parallel.
/// </summary>
public static class LibraryScanner
{
    private static readonly Regex EvarsFileRe = new(@"^evars(.+)\.bat$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string ManagedStart = ":: >>> SEP MANAGED PROJECTS (do not edit) >>>";
    private const string ManagedEnd = ":: <<< SEP MANAGED PROJECTS <<<";
    private static readonly Regex ManagedBlockRe = new(
        @"(?ms)^[ \t]*" + Regex.Escape(ManagedStart) + @".*?" + Regex.Escape(ManagedEnd) + @"[ \t]*\r?\n?", RegexOptions.Compiled);
    private static readonly Regex CallRe = new(
        @"^\s*(?:if\s+(?:not\s+)?exist\s+[^\r\n]+?\s+)?call\s+(?:""([^""]+)""|([^\s\r\n]+))", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // Host reachability: an answer on 445 is trusted for 5 min, silence is retried after 30 s.
    private static readonly ConcurrentDictionary<string, (bool Up, DateTime At)> HostCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsHostReachable(string host, int timeoutMs = 800)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (HostCache.TryGetValue(host, out var c) && DateTime.UtcNow - c.At < (c.Up ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30)))
            return c.Up;

        bool up = false;
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect(host, 445, null, null);
            if (ar.AsyncWaitHandle.WaitOne(timeoutMs)) { client.EndConnect(ar); up = true; }
        }
        catch { }
        HostCache[host] = (up, DateTime.UtcNow);
        return up;
    }

    /// <summary>Scans a library path. Never throws; a network path that does not answer within <paramref name="timeout"/> comes back as Invalid/TimedOut.</summary>
    public static async Task<ScanResult> ScanAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        string norm = SepPaths.Normalize(path);
        if (norm.Length == 0) return new ScanResult(LibraryKind.Invalid, Strings.Scan_NotFound, new());
        if (SepPaths.IsUrl(norm)) return new ScanResult(LibraryKind.Unsupported, Strings.Scan_Unsupported, new());

        if (SepPaths.IsUnc(norm))
        {
            string host = SepPaths.UncHost(norm) ?? string.Empty;
            bool up = await Task.Run(() => IsHostReachable(host), ct);
            if (!up) return new ScanResult(LibraryKind.Invalid, Strings.Scan_HostOffline, new());
        }

        var work = Task.Run(() => ScanImpl(norm, ct), ct);
        var done = await Task.WhenAny(work, Task.Delay(timeout, ct));
        if (done != work)
            return new ScanResult(LibraryKind.Invalid, string.Format(Strings.Scan_Timeout, (int)timeout.TotalSeconds), new(), TimedOut: true);
        try { return await work; }
        catch (Exception ex) { return new ScanResult(LibraryKind.Invalid, string.Format(Strings.Scan_Error, ex.Message), new()); }
    }

    private static ScanResult ScanImpl(string norm, CancellationToken ct)
    {
        if (File.Exists(norm))
        {
            string name = Path.GetFileName(norm).ToLowerInvariant();
            if (name is "custom_evars.bat" or "custom_evar.bat")
            {
                var projs = ParseCustomEvars(norm, Path.GetDirectoryName(norm) ?? norm);
                return new ScanResult(LibraryKind.Collection, string.Empty, projs);
            }
            if (name != "evars.bat" && EvarsFileRe.IsMatch(name))
                return new ScanResult(LibraryKind.Project, string.Empty, new() { MakeProject(norm, norm, null) });
            return new ScanResult(LibraryKind.Invalid, Strings.Scan_NotProjectFile, new());
        }

        if (!Directory.Exists(norm))
        {
            try { _ = new DirectoryInfo(norm).EnumerateFileSystemInfos().Any(); }
            catch (UnauthorizedAccessException ex) { return new ScanResult(LibraryKind.Invalid, string.Format(Strings.Scan_AccessDenied, ex.Message), new()); }
            catch (IOException ex) { return new ScanResult(LibraryKind.Invalid, string.Format(Strings.Scan_Error, ex.Message), new()); }
            catch { }
            return new ScanResult(LibraryKind.Invalid, Strings.Scan_NotFound, new());
        }

        // One listing of the root (like os.scandir): direct evars files, custom_evars, sub-folders.
        var direct = new List<string>();
        var subdirs = new List<string>();
        string? customFile = null;
        foreach (var entry in new DirectoryInfo(norm).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            string lower = entry.Name.ToLowerInvariant();
            if (entry is DirectoryInfo) subdirs.Add(entry.FullName);
            else if (lower is "custom_evars.bat" or "custom_evar.bat") customFile = entry.FullName;
            else if (lower != "evars.bat" && lower.StartsWith("evars") && lower.EndsWith(".bat")) direct.Add(entry.FullName);
        }
        direct.Sort(StringComparer.OrdinalIgnoreCase);

        // Probe the sub-folders in parallel (network round-trips dominate).
        var subfolderEvars = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(subdirs, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, d =>
        {
            var evs = DirectEvars(d);
            if (evs.Count > 0) subfolderEvars[d] = evs;
        });

        var projects = new List<ProjectRecord>();
        if (customFile != null) projects.AddRange(ParseCustomEvars(customFile, norm));
        foreach (var d in subfolderEvars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            foreach (var bat in subfolderEvars[d])
                projects.Add(MakeProject(bat, norm, d));
        foreach (var bat in direct)
            projects.Add(MakeProject(bat, norm, null));
        projects = Dedupe(projects);

        if (customFile != null || subfolderEvars.Count > 0)
        {
            if (projects.Count == 0) return new ScanResult(LibraryKind.Invalid, Strings.Scan_NoProjects, new());
            ReadCodes(projects, ct);
            return new ScanResult(LibraryKind.Collection, string.Empty, projects);
        }
        if (direct.Count > 0)
        {
            ReadCodes(projects, ct);
            return new ScanResult(LibraryKind.Project, string.Empty, projects);
        }
        return new ScanResult(LibraryKind.Invalid, Strings.Scan_NoProjects, new());
    }

    private static List<string> DirectEvars(string dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "evars*.bat"))
            {
                string lower = Path.GetFileName(f).ToLowerInvariant();
                if (lower != "evars.bat") list.Add(f);
            }
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Projects referenced by user-written call lines in custom_evars.bat (SEP's managed block excluded).</summary>
    public static List<ProjectRecord> ParseCustomEvars(string customPath, string baseDir)
    {
        var result = new List<ProjectRecord>();
        string text;
        try { text = SepPaths.ReadTextSmart(customPath).Text; }
        catch { return result; }
        text = ManagedBlockRe.Replace(text, string.Empty);
        string baseNorm = SepPaths.Normalize(baseDir);

        foreach (Match m in CallRe.Matches(text))
        {
            string target = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (target.Length == 0) continue;
            string resolved = Regex.Replace(target, @"%projects_dir%\\?", baseNorm + "\\", RegexOptions.IgnoreCase);
            resolved = Regex.Replace(resolved, @"%~dp0\\?", baseNorm + "\\", RegexOptions.IgnoreCase);
            resolved = SepPaths.Normalize(resolved);
            string file = Path.GetFileName(resolved).ToLowerInvariant();
            if (file is "projects.bat" or "custom_evars.bat" or "custom_evar.bat" or "evars.bat") continue;
            if (!file.EndsWith(".bat")) continue;
            string? dir = Path.GetDirectoryName(resolved);
            result.Add(MakeProject(resolved, baseNorm, string.Equals(dir, baseNorm, StringComparison.OrdinalIgnoreCase) ? null : dir));
        }
        return Dedupe(result);
    }

    public static string ProjectName(string batPath)
    {
        string file = Path.GetFileName(batPath);
        var m = EvarsFileRe.Match(file);
        return m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(file);
    }

    private static ProjectRecord MakeProject(string batPath, string libPath, string? projectDir)
    {
        string bat = SepPaths.Normalize(batPath);
        return new ProjectRecord
        {
            Id = SepPaths.GenerateId("proj", bat),
            Name = ProjectName(bat),
            BatPath = bat,
            LibPath = SepPaths.Normalize(libPath),
            ProjectDir = projectDir == null ? null : SepPaths.Normalize(projectDir),
        };
    }

    private static List<ProjectRecord> Dedupe(List<ProjectRecord> projects)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return projects.Where(p => seen.Add(p.BatPath)).ToList();
    }

    private static void ReadCodes(List<ProjectRecord> projects, CancellationToken ct)
    {
        Parallel.ForEach(projects, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            p => p.Code = SepPaths.ReadProjectCode(p.BatPath));
    }

    /// <summary>Lock files (*.lck) inside the project's *000 folders; null when the folder could not be listed.</summary>
    public static List<string>? FindLockFiles(string projectDir)
    {
        try
        {
            var locks = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(projectDir, "*000"))
            {
                locks.AddRange(Directory.EnumerateFiles(d, "*.lck"));
                locks.AddRange(Directory.EnumerateFiles(d, "*.lok"));
            }
            return locks;
        }
        catch
        {
            return null;
        }
    }
}
