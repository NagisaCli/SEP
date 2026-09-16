using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public sealed class ProjectCatalog : IProjectCatalog
{
    private static readonly TimeSpan LibraryScanTimeout = TimeSpan.FromSeconds(12);   // same as rescan_library(timeout=12)
    private static readonly TimeSpan LockProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScanReuseWindow = TimeSpan.FromSeconds(60);

    private readonly SepDataStore _store;
    private readonly object _gate = new();
    private Task? _runningScan;
    private DateTime _lastFullScan = DateTime.MinValue;
    private List<LibraryItem> _libraries = new();
    private List<ProjectItem> _projects = new();

    public SepData Data { get; }
    public E3dPathsConfig Paths { get; }
    public IReadOnlyList<LibraryItem> Libraries => _libraries;
    public IReadOnlyList<ProjectItem> Projects => _projects;
    public bool IsScanning { get; private set; }
    public ProjectItem? ActiveProject => _projects.FirstOrDefault(p => p.IsActive);

    public string LocalProjectsDir
    {
        get
        {
            string dir = SepPaths.Normalize(Data.Settings.LocalProjectsDir);
            if (dir.Length == 0) dir = SepPaths.Normalize(Paths.ProjectsDir);
            return dir.Length == 0 ? @"D:\AVEVA\Projects\E3D3.1" : dir;
        }
    }

    public event EventHandler? Changed;
    public event EventHandler? ScanStateChanged;

    public ProjectCatalog(SepDataStore store)
    {
        _store = store;
        Data = store.LoadData();
        Paths = store.LoadPaths();
        if (string.IsNullOrWhiteSpace(Data.Settings.LocalProjectsDir))
            Data.Settings.LocalProjectsDir = LocalProjectsDir;   // same default the Python store fills in
        RebuildModels();
    }

    // ── model building (cache → UI items) ────────────────────────────────────────

    private void RebuildModels()
    {
        List<LibraryItem> libs;
        List<ProjectItem> projs;
        lock (_gate)
        {
            var previous = _libraries.ToDictionary(l => l.Id, l => l);
            libs = Data.Libraries.Select(r =>
            {
                var item = new LibraryItem
                {
                    Id = r.Id,
                    Name = string.IsNullOrWhiteSpace(r.Name) ? r.Path : r.Name,
                    Path = r.Path,
                    IsUnc = SepPaths.IsUnc(r.Path),
                    IsCollection = r.Type != "project",
                    LastError = string.IsNullOrWhiteSpace(r.LastError) ? null : r.LastError,
                    LastScan = SepPaths.ParseIso(r.LastScan),
                };
                if (previous.TryGetValue(r.Id, out var old))
                {
                    item.IsExpanded = old.IsExpanded;
                    item.IsScanning = old.IsScanning;
                }
                return item;
            }).ToList();

            var libById = libs.ToDictionary(l => l.Id, l => l);
            var libByPath = libs.GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var mineIds = new HashSet<string>(Data.MyProjects.Select(m => m.Id));
            var mineBats = new HashSet<string>(Data.MyProjects.Select(m => SepPaths.Normalize(m.BatPath)), StringComparer.OrdinalIgnoreCase);
            string active = Data.Settings.LastLaunched ?? string.Empty;

            projs = new List<ProjectItem>();
            foreach (var r in Data.AllProjectsCache)
            {
                LibraryItem? lib = null;
                if (r.LibId != null) libById.TryGetValue(r.LibId, out lib);
                lib ??= libByPath.GetValueOrDefault(r.LibPath);
                if (lib == null) continue;   // orphaned cache entry (library removed)

                Data.ProjectMeta.TryGetValue(r.Id, out var meta);
                projs.Add(new ProjectItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Code = r.Code,
                    BatPath = r.BatPath,
                    ProjectDir = r.ProjectDir ?? (Path.GetDirectoryName(r.BatPath) ?? r.LibPath),
                    LibraryId = lib.Id,
                    LibraryName = lib.Name,
                    IsUnc = SepPaths.IsUnc(r.BatPath),
                    DisplayName = meta?.Name,
                    Owner = meta?.Owner,
                    Tags = meta?.Tags ?? new List<string>(),
                    IsFavorite = mineIds.Contains(r.Id) || mineBats.Contains(r.BatPath),
                    IsActive = active.Length > 0 && string.Equals(r.Name, active, StringComparison.OrdinalIgnoreCase),
                    IsCached = lib.LastError != null,
                });
            }
            foreach (var lib in libs)
                lib.ProjectCount = projs.Count(p => p.LibraryId == lib.Id);

            _libraries = libs;
            _projects = projs;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── scanning ─────────────────────────────────────────────────────────────────

    public Task RescanAllAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_runningScan != null && !_runningScan.IsCompleted) return _runningScan;
            if (!force && DateTime.UtcNow - _lastFullScan < ScanReuseWindow) return Task.CompletedTask;
            _runningScan = ScanLibrariesAsync(Data.Libraries.ToList(), markFull: true);
            return _runningScan;
        }
    }

    public Task RescanLibraryAsync(string libraryId)
    {
        var rec = Data.Libraries.FirstOrDefault(l => l.Id == libraryId);
        return rec == null ? Task.CompletedTask : ScanLibrariesAsync(new List<LibraryRecord> { rec }, markFull: false);
    }

    private async Task ScanLibrariesAsync(List<LibraryRecord> records, bool markFull)
    {
        if (records.Count == 0) { if (markFull) _lastFullScan = DateTime.UtcNow; return; }
        SetScanning(true);
        foreach (var rec in records) { var it = FindItem(rec.Id); if (it != null) it.IsScanning = true; }
        try
        {
            using var limiter = new SemaphoreSlim(8);
            var tasks = records.Select(async rec =>
            {
                await limiter.WaitAsync();
                try { return (rec, result: await LibraryScanner.ScanAsync(rec.Path, LibraryScanTimeout)); }
                finally { limiter.Release(); }
            }).ToList();
            var results = await Task.WhenAll(tasks);

            lock (_gate)
            {
                foreach (var (rec, result) in results)
                {
                    rec.LastScan = SepPaths.NowIso();
                    if (result.Ok)
                    {
                        rec.LastError = null;
                        rec.Type = result.Kind == LibraryKind.Project ? "project" : "collection";
                        string norm = SepPaths.Normalize(rec.Path);
                        Data.AllProjectsCache.RemoveAll(p => p.LibId == rec.Id ||
                            (p.LibId == null && string.Equals(p.LibPath, norm, StringComparison.OrdinalIgnoreCase)));
                        foreach (var p in result.Projects)
                        {
                            p.LibId = rec.Id;
                            p.DiscoveredAt = rec.LastScan;
                            Data.AllProjectsCache.Add(p);
                        }
                    }
                    else
                    {
                        rec.LastError = result.Reason;   // cached projects are kept, exactly like the Python tool
                    }
                }
            }
            if (markFull) _lastFullScan = DateTime.UtcNow;
            foreach (var rec in records) { var it = FindItem(rec.Id); if (it != null) it.IsScanning = false; }
            RebuildModels();
            Save();
        }
        finally
        {
            SetScanning(false);
        }

        _ = ProbeLocksAsync(records.Select(r => r.Id).ToHashSet());
    }

    /// <summary>Lock-file probe for the projects of reachable libraries, in parallel and under a deadline each.</summary>
    private async Task ProbeLocksAsync(HashSet<string> libraryIds)
    {
        List<ProjectItem> targets;
        lock (_gate)
        {
            var reachable = _libraries.Where(l => l.IsReachable && libraryIds.Contains(l.Id)).Select(l => l.Id).ToHashSet();
            targets = _projects.Where(p => reachable.Contains(p.LibraryId)).ToList();
        }
        using var limiter = new SemaphoreSlim(16);
        await Task.WhenAll(targets.Select(async p =>
        {
            await limiter.WaitAsync();
            try
            {
                var work = Task.Run(() => LibraryScanner.FindLockFiles(p.ProjectDir));
                if (await Task.WhenAny(work, Task.Delay(LockProbeTimeout)) != work) return;
                var locks = await work;
                if (locks == null) return;
                p.LockFiles = locks;
                p.LockCount = locks.Count;
                p.IsLocked = locks.Count > 0;
                p.LockStateKnown = true;
            }
            catch { }
            finally { limiter.Release(); }
        }));
    }

    private LibraryItem? FindItem(string id) { lock (_gate) return _libraries.FirstOrDefault(l => l.Id == id); }

    private void SetScanning(bool value)
    {
        IsScanning = value;
        ScanStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── libraries ────────────────────────────────────────────────────────────────

    public async Task<(bool Success, string Message)> AddLibraryAsync(string path)
    {
        string norm = SepPaths.Normalize(path);
        if (norm.Length == 0) return (false, string.Format(Strings.Lib_AddFailed, Strings.Scan_NotFound));
        string id = SepPaths.GenerateId("lib", norm);
        if (Data.Libraries.Any(l => l.Id == id)) return (false, Strings.Lib_AlreadyAdded);

        var result = await LibraryScanner.ScanAsync(norm, LibraryScanTimeout);
        if (!result.Ok) return (false, string.Format(Strings.Lib_AddFailed, result.Reason));

        bool isProject = result.Kind == LibraryKind.Project;
        string name = isProject
            ? (File.Exists(norm) ? LibraryScanner.ProjectName(norm) : Path.GetFileName(norm.TrimEnd('\\')))
            : Path.GetFileName(norm.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = norm;

        var rec = new LibraryRecord
        {
            Id = id,
            Name = name,
            Path = norm,
            Type = isProject ? "project" : "collection",
            Protocol = SepPaths.Protocol(norm),
            Source = "user",
            LastScan = SepPaths.NowIso(),
            LastError = null,
        };
        lock (_gate)
        {
            Data.Libraries.Add(rec);
            foreach (var p in result.Projects)
            {
                p.LibId = id;
                p.DiscoveredAt = rec.LastScan;
                Data.AllProjectsCache.Add(p);
            }
        }
        RebuildModels();
        Save();
        _ = ProbeLocksAsync(new HashSet<string> { id });
        return (true, string.Format(Strings.Lib_Added, name, result.Projects.Count));
    }

    public (bool Success, string Message) RemoveLibrary(string libraryId)
    {
        LibraryRecord? rec;
        lock (_gate)
        {
            rec = Data.Libraries.FirstOrDefault(l => l.Id == libraryId);
            if (rec == null) return (false, Strings.Scan_NotFound);
            Data.Libraries.Remove(rec);
            string norm = SepPaths.Normalize(rec.Path);
            Data.AllProjectsCache.RemoveAll(p => p.LibId == libraryId ||
                (p.LibId == null && string.Equals(p.LibPath, norm, StringComparison.OrdinalIgnoreCase)));
        }
        RebuildModels();
        Save();
        return (true, string.Format(Strings.Lib_Removed, rec.Name));
    }

    // ── my projects / active project ─────────────────────────────────────────────

    public bool ToggleMyProject(ProjectItem project)
    {
        bool nowMine;
        lock (_gate)
        {
            int removed = Data.MyProjects.RemoveAll(m => m.Id == project.Id ||
                string.Equals(SepPaths.Normalize(m.BatPath), project.BatPath, StringComparison.OrdinalIgnoreCase));
            nowMine = removed == 0;
            if (nowMine)
            {
                Data.MyProjects.Add(new MyProjectRecord
                {
                    Id = project.Id,
                    Name = project.Name,
                    BatPath = project.BatPath,
                    LibId = project.LibraryId,
                    Source = "user",
                    AddedAt = SepPaths.NowIso(),
                });
            }
        }
        project.IsFavorite = nowMine;
        Save();
        return nowMine;
    }

    public void SetLastLaunched(ProjectItem project, string mode)
    {
        Data.Settings.LastLaunched = project.Name;
        Data.Settings.LastMode = mode;
        lock (_gate)
        {
            foreach (var p in _projects) p.IsActive = ReferenceEquals(p, project) || string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase);
        }
        Save();
    }

    public void SetLastLaunchedLibrary(LibraryItem library)
    {
        Data.Settings.LastLaunched = library.Name;
        Data.Settings.LastMode = "library";
        lock (_gate) { foreach (var p in _projects) p.IsActive = false; }
        Save();
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                _store.SaveData(Data);
                _store.SavePaths(Paths);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Save failed: {ex.Message}");
        }
    }
}
