using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>
/// Who is working in which project — the port of e3d_session.py:
///  - when this machine launches a project, a heartbeat file goes into &lt;project&gt;\.sep_sessions\ and is refreshed
///    every 20 s while an E3D process runs (removed when E3D exits);
///  - inspecting a project reads other machines' heartbeats (stale after 3 min) and the owners of DABACON lock
///    files (user@computer inside *.lck / *.lok), and stores the result on the <see cref="ProjectItem"/>.
/// Every disk probe is bounded so an offline share can never stall the UI.
/// </summary>
public sealed class SessionService : IDisposable
{
    private static readonly TimeSpan ProbeTimeoutLocal = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ProbeTimeoutUnc = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(180);
    private static readonly HashSet<string> E3dProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "mon", "design", "draw", "isodraft", "e3ddes", "e3d" };

    private static readonly Regex LockOwnerEmailRe = new(@"([A-Za-z0-9_.\-\u4e00-\u9fa5]+)@([A-Za-z0-9_.\-]+)", RegexOptions.Compiled);
    private static readonly Regex LockOwnerDomainRe = new(@"([A-Za-z0-9_.\-]+)\\([A-Za-z0-9_.\-\u4e00-\u9fa5]+)", RegexOptions.Compiled);
    private static readonly Regex LockOwnerLockedByRe = new(@"LOCKED\s+BY\s+([A-Za-z0-9_.\-\u4e00-\u9fa5]+)(?:\s*@\s*([A-Za-z0-9_.\-]+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LockOwnerUserRe = new(@"USER\s+([A-Za-z0-9_.\-\u4e00-\u9fa5]+)(?:\s*@\s*([A-Za-z0-9_.\-]+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ConcurrentDictionary<string, SessionFile> _local = new();
    private readonly Timer _heartbeat;
    private DateTime _e3dCheckedAt = DateTime.MinValue;
    private bool _e3dRunning;

    public SessionService()
    {
        _heartbeat = new Timer(_ => Heartbeat(), null, HeartbeatEvery, HeartbeatEvery);
    }

    public static string ComputerName => Environment.MachineName;
    public static string UserName => Environment.UserName;

    // ── this machine's sessions ──────────────────────────────────────────────────

    /// <summary>Records that this machine has just launched the project (mode single) or projects (mode all).</summary>
    public void Register(IEnumerable<ProjectItem> projects)
    {
        foreach (var p in projects)
        {
            var info = new SessionFile
            {
                SessionId = SepPaths.GenerateId("ses", $"{ComputerName}_{UserName}_{p.Id}"),
                ProjectId = p.Id,
                ProjectName = p.Name,
                BatPath = p.BatPath,
                ComputerName = ComputerName,
                UserName = UserName,
                StartedAt = SepPaths.NowIso(),
                LastHeartbeat = SepPaths.NowIso(),
                Pid = Environment.ProcessId,
            };
            _local[p.Id] = info;
            _ = Task.Run(() => WriteBounded(info));
        }
    }

    public void Unregister(string? projectId = null)
    {
        var targets = projectId == null ? _local.Values.ToList() : (_local.TryRemove(projectId, out var one) ? new List<SessionFile> { one } : new());
        if (projectId == null) _local.Clear();
        foreach (var s in targets) _ = Task.Run(() => DeleteBounded(s));
    }

    private void Heartbeat()
    {
        if (_local.IsEmpty) return;
        bool alive = IsE3dRunning(force: true);
        foreach (var s in _local.Values.ToList())
        {
            if (alive)
            {
                s.LastHeartbeat = SepPaths.NowIso();
                WriteBounded(s);
            }
            else
            {
                DeleteBounded(s);
            }
        }
        if (!alive) _local.Clear();
    }

    /// <summary>True when an AVEVA E3D module process runs on this machine (cached for 5 s).</summary>
    public bool IsE3dRunning(bool force = false)
    {
        if (!force && DateTime.UtcNow - _e3dCheckedAt < TimeSpan.FromSeconds(5)) return _e3dRunning;
        try
        {
            _e3dRunning = Process.GetProcesses().Any(p => { try { return E3dProcesses.Contains(p.ProcessName); } catch { return false; } });
        }
        catch { _e3dRunning = false; }
        _e3dCheckedAt = DateTime.UtcNow;
        return _e3dRunning;
    }

    private static string SessionDir(string batPath) => Path.Combine(Path.GetDirectoryName(batPath) ?? batPath, ".sep_sessions");

    private static void WriteBounded(SessionFile s)
    {
        RunBounded(() =>
        {
            string dir = SessionDir(s.BatPath);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, s.SessionId + ".json");
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }, ProbeTimeoutLocal * 2);
    }

    private static void DeleteBounded(SessionFile s)
    {
        RunBounded(() =>
        {
            string file = Path.Combine(SessionDir(s.BatPath), s.SessionId + ".json");
            if (File.Exists(file)) File.Delete(file);
        }, ProbeTimeoutLocal);
    }

    private static void RunBounded(Action action, TimeSpan timeout)
    {
        try
        {
            var work = Task.Run(action);
            work.Wait(timeout);
        }
        catch (Exception ex)
        {
            App.Log($"session file: {ex.GetBaseException().Message}");
        }
    }

    // ── inspecting projects ──────────────────────────────────────────────────────

    /// <summary>Probes the given projects in parallel; each result lands on its item (Sessions / SessionsKnown).</summary>
    public async Task ProbeAsync(IEnumerable<ProjectItem> projects, int parallelism = 4)
    {
        using var limiter = new SemaphoreSlim(parallelism);
        await Task.WhenAll(projects.Select(async p =>
        {
            await limiter.WaitAsync();
            try { await InspectAsync(p); }
            finally { limiter.Release(); }
        }));
    }

    public async Task InspectAsync(ProjectItem project)
    {
        var timeout = project.IsUnc ? ProbeTimeoutUnc : ProbeTimeoutLocal;
        var work = Task.Run(() => Inspect(project.BatPath, project.ProjectDir, project.Id));
        if (await Task.WhenAny(work, Task.Delay(timeout)) != work) return;
        try
        {
            project.Sessions = await work;
            project.SessionsKnown = true;
        }
        catch { }
    }

    private IReadOnlyList<ProjectSession> Inspect(string batPath, string projectDir, string projectId)
    {
        var sessions = new List<ProjectSession>();
        string me = ComputerName;

        // 1. SEP heartbeats of every machine (stale ones are cleaned up)
        string sesDir = Path.Combine(projectDir, ".sep_sessions");
        if (Directory.Exists(sesDir))
        {
            foreach (var file in Directory.EnumerateFiles(sesDir, "*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                    if (s == null) continue;
                    var hb = SepPaths.ParseIso(s.LastHeartbeat ?? s.StartedAt);
                    if (hb != null && DateTime.Now - hb.Value >= StaleAfter)
                    {
                        try { File.Delete(file); } catch { }
                        continue;
                    }
                    sessions.Add(new ProjectSession(s.UserName ?? "?", s.ComputerName ?? "?", "sep",
                        string.Equals(s.ComputerName, me, StringComparison.OrdinalIgnoreCase), SepPaths.ParseIso(s.StartedAt)));
                }
                catch { }
            }
        }

        // 2. owners of DABACON lock files in candidate folders
        foreach (var lck in LibraryScanner.FindLockFiles(projectDir) ?? new List<string>())
        {
            try
            {
                using var fs = new FileStream(lck, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[1024];
                int n = fs.Read(buf, 0, buf.Length);
                if (TryParseLockOwner(buf, n, out string uName, out string cName))
                {
                    sessions.Add(new ProjectSession(uName, cName, "lock",
                        string.Equals(cName, me, StringComparison.OrdinalIgnoreCase), File.GetLastWriteTime(lck)));
                }
            }
            catch { }
        }

        // 3. this process's own registration: only when E3D is actively running on this machine
        if (IsE3dRunning() && _local.TryGetValue(projectId, out var mine) &&
            !sessions.Any(s => s.IsThisDevice && s.User.Equals(UserName, StringComparison.OrdinalIgnoreCase)))
        {
            sessions.Add(new ProjectSession(UserName, me, "local", true, SepPaths.ParseIso(mine.StartedAt)));
        }

        // one entry per user@computer, heartbeat beats lock
        return sessions
            .GroupBy(s => $"{s.Computer}|{s.User}".ToLowerInvariant())
            .Select(g => g.OrderBy(s => s.Source == "sep" ? 0 : s.Source == "local" ? 1 : 2).First())
            .OrderBy(s => s.User, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryParseLockOwner(byte[] buf, int count, out string user, out string computer)
    {
        user = string.Empty;
        computer = string.Empty;
        if (count <= 0) return false;

        string[] texts = {
            Encoding.UTF8.GetString(buf, 0, count),
            Encoding.Latin1.GetString(buf, 0, count),
            SepPaths.Gbk.GetString(buf, 0, count)
        };

        foreach (var text in texts)
        {
            var m = LockOwnerEmailRe.Match(text);
            if (m.Success)
            {
                user = m.Groups[1].Value.Trim();
                computer = m.Groups[2].Value.Trim();
                if (user.Length > 0 && computer.Length > 0) return true;
            }

            var md = LockOwnerDomainRe.Match(text);
            if (md.Success)
            {
                computer = md.Groups[1].Value.Trim();
                user = md.Groups[2].Value.Trim();
                if (user.Length > 0 && computer.Length > 0) return true;
            }

            var ml = LockOwnerLockedByRe.Match(text);
            if (ml.Success)
            {
                user = ml.Groups[1].Value.Trim();
                computer = ml.Groups[2].Success && ml.Groups[2].Value.Length > 0 ? ml.Groups[2].Value.Trim() : "DABACON";
                if (user.Length > 0) return true;
            }

            var mu = LockOwnerUserRe.Match(text);
            if (mu.Success)
            {
                user = mu.Groups[1].Value.Trim();
                computer = mu.Groups[2].Success && mu.Groups[2].Value.Length > 0 ? mu.Groups[2].Value.Trim() : "DABACON";
                if (user.Length > 0) return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _heartbeat.Dispose();
        Unregister();
    }

    private sealed class SessionFile
    {
        [JsonPropertyName("session_id")] public string SessionId { get; set; } = string.Empty;
        [JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
        [JsonPropertyName("project_name")] public string? ProjectName { get; set; }
        [JsonPropertyName("bat_path")] public string BatPath { get; set; } = string.Empty;
        [JsonPropertyName("computer_name")] public string? ComputerName { get; set; }
        [JsonPropertyName("user_name")] public string? UserName { get; set; }
        [JsonPropertyName("started_at")] public string? StartedAt { get; set; }
        [JsonPropertyName("last_heartbeat")] public string? LastHeartbeat { get; set; }
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
