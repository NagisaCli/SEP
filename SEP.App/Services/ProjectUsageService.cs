using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SEP.App.Models;

namespace SEP.App.Services;

public sealed record UsageSummary(int Checked, int InUse, int Free, int Unreachable);

/// <summary>
/// "Who is in this project" — run only when the user asks (one project, a library, a group, all projects or my
/// projects); never on a timer. Per project it costs one open of the system database and, only when that project is
/// in use, one read of its session register. Nothing is ever written on the server.
///  1. In use or free: the project's &lt;code&gt;000\&lt;code&gt;sys is opened read-only with no sharing. Every E3D
///     session in the project holds that file open, so the file server refuses the open while anybody is in —
///     without a handle being created, so nobody is disturbed. When the project is free the file is held for a few
///     milliseconds only.
///  2. Who: every AVEVA program that enters the project leaves a record in the project's communications database
///     (&lt;code&gt;com): process tag "X&lt;hex pid&gt;-&lt;computer&gt;", Windows user, computer and start time
///     "HH:mm dd MMM" in that computer's own clock. While the session runs, the record also lists the databases it
///     has open; E3D clears that list when the session ends, so records with a list are the sessions still running.
///     A session whose E3D crashed keeps its list until an administrator expunges it: records of this PC are
///     checked against the live processes, records of other PCs older than <see cref="StaleAfter"/> are shown apart
///     as "probably left without closing E3D".
/// </summary>
public sealed class ProjectUsageService
{
    private static readonly TimeSpan PerProjectDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);   // still-marked sessions older than this probably crashed
    private const int MaxRegisterBytes = 32 * 1024 * 1024;
    private const int SharingViolation = 32, LockViolation = 33;
    private static readonly Regex TagRe = new(@"^X[0-9A-Fa-f]{2,8}-[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    private readonly SessionService _sessions;
    private readonly SemaphoreSlim _running = new(1, 1);

    public ProjectUsageService(SessionService sessions)
    {
        _sessions = sessions;
    }

    public bool IsBusy => _running.CurrentCount == 0;

    /// <summary>Checks the given projects (four at a time) and stores the outcome on each item.</summary>
    public async Task<UsageSummary> CheckAsync(IEnumerable<ProjectItem> projects)
    {
        var list = projects.Distinct().ToList();
        await _running.WaitAsync();
        try
        {
            await _sessions.ProbeAsync();   // this PC's own E3D sessions first (local only)
            using var limiter = new SemaphoreSlim(4);
            var results = await Task.WhenAll(list.Select(async p =>
            {
                await limiter.WaitAsync();
                try
                {
                    var work = Task.Run(() => CheckOne(p));
                    var result = await Task.WhenAny(work, Task.Delay(PerProjectDeadline)) == work
                        ? await work
                        : (UsageState.Unreachable, (IReadOnlyList<ProjectSession>)Array.Empty<ProjectSession>());
                    OnUi(() =>
                    {
                        p.UsageLogins = result.Item2;
                        p.Usage = result.Item1;
                        p.UsageCheckedAt = DateTime.Now;
                    });
                    return result.Item1;
                }
                finally { limiter.Release(); }
            }));
            var summary = new UsageSummary(list.Count, results.Count(r => r == UsageState.InUse), results.Count(r => r == UsageState.Free),
                results.Count(r => r is UsageState.Unreachable or UsageState.Unknown));
            App.Log($"Usage check: {summary.Checked} project(s), {summary.InUse} in use, {summary.Free} free, {summary.Unreachable} not checked");
            return summary;
        }
        finally { _running.Release(); }
    }

    private (UsageState, IReadOnlyList<ProjectSession>) CheckOne(ProjectItem project)
    {
        var none = (IReadOnlyList<ProjectSession>)Array.Empty<ProjectSession>();
        try
        {
            if (project.IsUnc)
            {
                // cached TCP answer (no SMB session) — an offline or full server is not asked again and again
                string host = SepPaths.UncHost(project.BatPath) ?? string.Empty;
                if (!LibraryScanner.IsHostReachable(host)) return (UsageState.Unreachable, none);
            }
            // a project open in E3D on this PC is in use without asking the server at all
            bool openHere = _sessions.OpenProjectIds.Contains(project.Id);

            var (sys, com) = DatabaseFiles(project);
            if (sys == null) return openHere ? (UsageState.InUse, none) : (UsageState.Unknown, none);

            UsageState state = openHere ? UsageState.InUse : ProbeInUse(sys);
            if (state != UsageState.InUse) return (state, none);
            return (UsageState.InUse, com == null ? none : ReadRegister(com));
        }
        catch (Exception ex)
        {
            App.Log($"Usage check {project.Name}: {ex.GetBaseException().Message}");
            return (UsageState.Unreachable, none);
        }
    }

    /// <summary>Read-only, no-sharing open: refused by the server while any E3D session holds the file.</summary>
    private static UsageState ProbeInUse(string sysFile)
    {
        try
        {
            using var fs = new FileStream(sysFile, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return UsageState.Free;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is SharingViolation or LockViolation)
        {
            return UsageState.InUse;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return UsageState.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UsageState.Unreachable;
        }
    }

    /// <summary>
    /// &lt;code&gt;sys / &lt;code&gt;com of the project: in &lt;project&gt;\&lt;code&gt;000 normally, otherwise wherever
    /// the evars file's "set &lt;CODE&gt;000=" points.
    /// </summary>
    private static (string? Sys, string? Com) DatabaseFiles(ProjectItem project)
    {
        if (string.IsNullOrEmpty(project.Code)) return (null, null);
        string code = project.Code.ToLowerInvariant();
        foreach (var dir in CandidateDbDirs(project, code))
        {
            string sys = Path.Combine(dir, code + "sys");
            if (!File.Exists(sys)) continue;
            string com = Path.Combine(dir, code + "com");
            return (sys, File.Exists(com) ? com : null);
        }
        return (null, null);
    }

    private static IEnumerable<string> CandidateDbDirs(ProjectItem project, string code)
    {
        yield return Path.Combine(project.ProjectDir, code + "000");
        string? fromEvars = null;
        try
        {
            var (text, _) = SepPaths.ReadTextSmart(project.BatPath);
            var m = Regex.Match(text, $@"(?im)^\s*set\s+""?{Regex.Escape(code)}000\s*=\s*""?([^""\r\n]+)");
            if (m.Success)
            {
                string batDir = Path.GetDirectoryName(project.BatPath) ?? project.ProjectDir;
                string projectsDir = Path.GetDirectoryName(project.ProjectDir.TrimEnd('\\')) ?? batDir;
                string value = Regex.Replace(m.Groups[1].Value.Trim(), @"%projects_dir%\\?", projectsDir.TrimEnd('\\') + "\\", RegexOptions.IgnoreCase);
                value = Regex.Replace(value, @"%~dp0\\?", batDir.TrimEnd('\\') + "\\", RegexOptions.IgnoreCase);
                if (!value.Contains('%')) fromEvars = SepPaths.Normalize(value);
            }
        }
        catch { }
        if (fromEvars != null) yield return fromEvars;
    }

    /// <summary>Logins in E3D's session register (communications database), newest first, one per user@computer.</summary>
    private static IReadOnlyList<ProjectSession> ReadRegister(string comFile)
    {
        byte[] data;
        using (var fs = new FileStream(comFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (fs.Length > MaxRegisterBytes) return Array.Empty<ProjectSession>();
            data = new byte[fs.Length];
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n == 0) break;
                read += n;
            }
        }
        return ParseRegister(data, DateTime.Now);
    }

    /// <summary>
    /// A session record starts with runs of 36-byte slots, each a big-endian length and up to 32 bytes of text in the
    /// writing computer's ANSI code page (bytes after the length are left-overs of older, longer values): process tag
    /// "X&lt;hex pid&gt;-&lt;computer&gt;" at T, Windows user at T+32, the same in upper case at T+68, computer at
    /// T+104, start time "HH:mm dd MMM" at T+140. The session's open databases are listed from T+196 (pairs of
    /// big-endian integers) with their number at T+256; both are zero once the session has ended.
    /// </summary>
    internal static IReadOnlyList<ProjectSession> ParseRegister(byte[] data, DateTime now)
    {
        var found = new Dictionary<string, ProjectSession>(StringComparer.OrdinalIgnoreCase);
        string me = Environment.MachineName;
        for (int i = 4; i + 260 < data.Length; i++)
        {
            if (data[i] != (byte)'X') continue;
            string? tag = Slot(data, i - 4);
            if (tag == null || !TagRe.IsMatch(tag)) continue;
            string? user = Slot(data, i + 32);
            string? computer = Slot(data, i + 104);
            if (string.IsNullOrEmpty(computer)) computer = tag[(tag.IndexOf('-') + 1)..];
            string? time = Slot(data, i + 140);
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(computer) || time == null) continue;
            bool running = IsRunning(data, i);
            i += 255;
            if (!running) continue;

            DateTime? since = ParseTime(time, now);
            bool thisPc = string.Equals(computer, me, StringComparison.OrdinalIgnoreCase);
            bool stale = false;
            if (thisPc)
            {
                if (!IsLocalProcessAlive(tag)) continue;   // exact for this PC: a crashed session of ours is not counted
            }
            else stale = since == null || now - since.Value > StaleAfter;
            found[tag] = new ProjectSession(user, computer, "e3d", thisPc, since, stale);
        }
        return found.Values.OrderBy(s => s.Stale).ThenByDescending(s => s.Since).ToList();
    }

    /// <summary>True while the record still lists open databases (E3D clears the list when the session ends).</summary>
    private static bool IsRunning(byte[] data, int tag)
    {
        for (int k = tag + 196; k < tag + 212; k++) if (data[k] != 0) return true;
        return (data[tag + 256] | data[tag + 257] | data[tag + 258] | data[tag + 259]) != 0;
    }

    /// <summary>The tag's pid (E3D's console process) still runs on this PC.</summary>
    private static bool IsLocalProcessAlive(string tag)
    {
        int dash = tag.IndexOf('-');
        if (dash < 2 || !int.TryParse(tag[1..dash], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pid)) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? Slot(byte[] data, int at)
    {
        if (at < 0 || at + 36 > data.Length) return null;
        int len = (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
        if (len < 0 || len > 32) return null;
        bool ascii = true;
        for (int k = 0; k < len; k++)
        {
            byte c = data[at + 4 + k];
            if (c < 0x20 || c == 0x7F) return null;
            if (c > 0x7F) ascii = false;
        }
        if (ascii) return Encoding.ASCII.GetString(data, at + 4, len).Trim();
        try { return SepPaths.Gbk.GetString(data, at + 4, len).Trim(); }   // Chinese Windows account names
        catch (ArgumentException) { return Encoding.Latin1.GetString(data, at + 4, len).Trim(); }
    }

    /// <summary>"01:46 28 Sep" (no year): this year, or last year when that would lie in the future.</summary>
    private static DateTime? ParseTime(string s, DateTime now)
    {
        if (!DateTime.TryParseExact($"{s} {now.Year}", "HH:mm d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t))
            return null;
        return t > now.AddDays(1) ? t.AddYears(-1) : t;
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.Invoke(action);
    }
}
