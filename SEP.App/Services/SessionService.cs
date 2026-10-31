using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>
/// Which projects are open in E3D on this PC, found entirely on the client:
///  - the local E3D module processes (des, draw, mon, …) are listed every <see cref="PollEvery"/>;
///  - the files they hold open are read from the local handle table (<see cref="OpenFileProbe"/>);
///  - a project is in use when its system database (&lt;code&gt;000\&lt;code&gt;sys) is among them. Catalogue
///    projects that E3D only reads databases from are not counted: their sys database stays closed.
/// Nothing is read from, written to or deleted on a file server: libraries are mostly reached as Guest, where the
/// server cannot tell users apart, and every extra request kept an SMB session alive and used up the server's
/// connection limit (system error 71). If the handle table cannot be read, the projects SEP itself launched are
/// shown while E3D runs.
/// </summary>
public sealed class SessionService : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(10);

    // E3D 3.1 module executables (des = Design, draw = Draw, dop/iss/isd = Isodraft, spc/cata = Paragon/Spec, …).
    // Helper services that run without a project (sglRenderService, DabaconAgent, Launcher) are left out.
    private static readonly HashSet<string> E3dProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "mon", "des", "draw", "dop", "iss", "isd", "spc", "cata", "isba", "smm", "design", "isodraft", "e3d", "e3ddes" };

    private readonly IProjectCatalog _catalog;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _launched = new();     // project id → launched by SEP at
    private readonly Dictionary<string, DateTime> _openSince = new();    // project id → first seen open
    private static readonly int SessionId = CurrentSessionId();
    private DateTime _e3dCheckedAt = DateTime.MinValue;
    private bool _e3dRunning;
    private int _pollBusy;
    private int _itemsStale = 1;   // the catalog rebuilt its items: they need this PC's sessions written again
    private Task<HashSet<string>?>? _probe;   // still running after its deadline: never start a second one

    /// <summary>Raised (on a worker thread) when the set of projects open on this PC changes.</summary>
    public event EventHandler? PresenceChanged;

    /// <summary>Raised (on a worker thread) when an E3D module process appears or the last one ends.</summary>
    public event EventHandler? E3dRunningChanged;

    public SessionService(IProjectCatalog catalog)
    {
        _catalog = catalog;
        _catalog.Changed += (_, _) => Volatile.Write(ref _itemsStale, 1);
        _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(3), PollEvery);
    }

    private static int CurrentSessionId()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
    }

    public static string ComputerName => Environment.MachineName;
    public static string UserName => Environment.UserName;

    /// <summary>Projects this PC has open in E3D right now (ids).</summary>
    public IReadOnlyCollection<string> OpenProjectIds { get { lock (_gate) return _openSince.Keys.ToList(); } }

    /// <summary>Remembers what SEP launched; only used when the open files of E3D cannot be read.</summary>
    public void Register(IEnumerable<ProjectItem> projects)
    {
        lock (_gate)
        {
            _launched.Clear();
            foreach (var p in projects) _launched[p.Id] = DateTime.Now;
        }
        _ = Task.Run(Poll);
    }

    public void Unregister(string? projectId = null)
    {
        lock (_gate)
        {
            if (projectId == null) _launched.Clear();
            else _launched.Remove(projectId);
        }
    }

    /// <summary>True when an AVEVA E3D module process runs on this machine (cached for 5 s).</summary>
    public bool IsE3dRunning(bool force = false)
    {
        if (!force && DateTime.UtcNow - _e3dCheckedAt < TimeSpan.FromSeconds(5)) return _e3dRunning;
        _e3dRunning = E3dProcessIds().Count > 0;
        _e3dCheckedAt = DateTime.UtcNow;
        return _e3dRunning;
    }

    private static List<int> E3dProcessIds()
    {
        var ids = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)   // every Process object holds a handle; dispose them instead of waiting for the finalizer
            {
                try
                {
                    if (p.SessionId == SessionId && E3dProcesses.Contains(p.ProcessName)) ids.Add(p.Id);
                }
                catch { }
            }
        }
        return ids;
    }

    // ── detection ────────────────────────────────────────────────────────────────

    /// <summary>Kept for the pages' refresh buttons: re-reads this PC's E3D state now. Never touches a server.</summary>
    public Task ProbeAsync(IEnumerable<ProjectItem>? projects = null, int parallelism = 0) => Task.Run(Poll);

    public Task InspectAsync(ProjectItem project) => ProbeAsync();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        try { PollCore(); }
        catch (Exception ex) { App.Log($"E3D presence poll failed: {ex.Message}"); }
        finally { Volatile.Write(ref _pollBusy, 0); }
    }

    private void PollCore()
    {
        var pids = E3dProcessIds();
        bool wasRunning = _e3dRunning;
        _e3dRunning = pids.Count > 0;
        _e3dCheckedAt = DateTime.UtcNow;
        if (wasRunning != _e3dRunning) E3dRunningChanged?.Invoke(this, EventArgs.Empty);

        HashSet<string> open;
        if (pids.Count == 0)
        {
            open = new HashSet<string>();
            lock (_gate) _launched.Clear();
        }
        else
        {
            if (_probe != null && !_probe.IsCompleted) return;   // a name lookup on a vanished share is still hanging
            var work = _probe = Task.Run(() => OpenFileProbe.OpenFiles(pids));
            var files = work.Wait(ProbeDeadline) ? work.Result : null;
            open = files != null ? MatchProjects(files) : FallbackFromLaunches();
        }

        bool changed;
        lock (_gate)
        {
            changed = !open.SetEquals(_openSince.Keys);
            foreach (var id in _openSince.Keys.Where(id => !open.Contains(id)).ToList()) _openSince.Remove(id);
            foreach (var id in open) _openSince.TryAdd(id, DateTime.Now);
        }
        // the dispatcher round trip over every project only when something to show has changed
        if (changed || Interlocked.Exchange(ref _itemsStale, 0) == 1) Apply();
        if (changed)
        {
            App.Log($"E3D open on this PC: {(open.Count == 0 ? "none" : string.Join(", ", _catalog.Projects.Where(p => open.Contains(p.Id)).Select(p => p.Name)))}");
            PresenceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Maps open files to projects: a held-open "&lt;code&gt;sys" inside a "…000" folder marks the project whose folder
    /// contains it (or, for databases kept elsewhere, the project with that code).
    /// </summary>
    private HashSet<string> MatchProjects(HashSet<string> files)
    {
        var result = new HashSet<string>();
        var projects = _catalog.Projects.ToList();
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            if (!name.EndsWith("sys", StringComparison.OrdinalIgnoreCase) || name.Length < 4) continue;
            string? dbDir = Path.GetDirectoryName(file);
            if (dbDir == null || !Path.GetFileName(dbDir).EndsWith("000", StringComparison.OrdinalIgnoreCase)) continue;
            string code = name[..^3];

            // the deepest project folder that contains the database folder
            var byFolder = projects
                .Where(p => p.ProjectDir.Length > 0 && dbDir.StartsWith(p.ProjectDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.ProjectDir.Length)
                .ToList();
            var hit = byFolder.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)) ?? byFolder.FirstOrDefault();
            if (hit != null) { result.Add(hit.Id); continue; }

            foreach (var p in projects.Where(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
                result.Add(p.Id);
        }
        return result;
    }

    private HashSet<string> FallbackFromLaunches()
    {
        lock (_gate) return _launched.Count == 1 ? new HashSet<string>(_launched.Keys) : new HashSet<string>();
    }

    /// <summary>Writes this PC's sessions onto the project items (UI thread).</summary>
    private void Apply()
    {
        Dictionary<string, DateTime> open;
        lock (_gate) open = new Dictionary<string, DateTime>(_openSince);
        var projects = _catalog.Projects.ToList();

        void Set()
        {
            foreach (var p in projects)
            {
                IReadOnlyList<ProjectSession> sessions = open.TryGetValue(p.Id, out var since)
                    ? new[] { new ProjectSession(UserName, ComputerName, "local", true, since) }
                    : Array.Empty<ProjectSession>();
                if (p.Sessions.Count != sessions.Count) p.Sessions = sessions;
                p.SessionsKnown = true;
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Set();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Set);
    }

    public void Dispose() => _poll.Dispose();
}
