using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    /// <summary>Same palette as e3d_store.CATEGORY_COLORS, assigned round-robin to new categories.</summary>
    private static readonly string[] CategoryColors =
    {
        "#4f8cff", "#2dd4a7", "#f5b85c", "#ff5d6c", "#b07bff", "#36b6e8", "#ff8f6b", "#8bd66b", "#e86b9a", "#9aa8ff",
    };

    private readonly SepDataStore _store;
    private readonly object _gate = new();
    private Task? _runningScan;
    private DateTime _lastFullScan = DateTime.MinValue;
    private List<LibraryItem> _libraries = new();
    private List<ProjectItem> _projects = new();
    private List<ProjectItem> _myProjects = new();
    private List<CategoryInfo> _categories = new();
    private List<string> _allTags = new();

    public SepData Data { get; }
    public E3dPathsConfig Paths { get; }
    public IReadOnlyList<LibraryItem> Libraries => _libraries;
    public IReadOnlyList<ProjectItem> Projects => _projects;
    public IReadOnlyList<ProjectItem> MyProjects => _myProjects;
    public IReadOnlyList<CategoryInfo> Categories => _categories;
    public IReadOnlyList<string> AllTags => _allTags;
    public IReadOnlyList<NotificationRecord> ActiveNotifications => Data.Notifications.Where(n => !n.Dismissed).ToList();
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
        CleanCategories();
        RebuildModels();
    }

    // ── model building (cache → UI items) ────────────────────────────────────────

    /// <summary>
    /// Maps the cached records to UI items. Existing <see cref="ProjectItem"/> instances are updated in place when
    /// the same project is still there, so cards keep their lock/session state and the UI does not flicker.
    /// </summary>
    private void RebuildModels()
    {
        List<LibraryItem> libs;
        List<ProjectItem> projs;
        lock (_gate)
        {
            var previousLibs = _libraries.ToDictionary(l => l.Id, l => l);
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
                if (previousLibs.TryGetValue(r.Id, out var old))
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
            bool singleMode = Data.Settings.LastMode is "single" or "temp" or "";
            var previous = _projects.ToDictionary(p => p.Id, p => p);
            var categories = _categories.ToDictionary(c => c.Id, c => c);

            projs = new List<ProjectItem>();
            foreach (var r in Data.AllProjectsCache)
            {
                LibraryItem? lib = null;
                if (r.LibId != null) libById.TryGetValue(r.LibId, out lib);
                lib ??= libByPath.GetValueOrDefault(r.LibPath);
                if (lib == null) continue;   // orphaned cache entry (library removed)

                if (!previous.TryGetValue(r.Id, out var item)
                    || !string.Equals(item.BatPath, r.BatPath, StringComparison.OrdinalIgnoreCase)
                    || item.LibraryId != lib.Id)
                {
                    item = new ProjectItem
                    {
                        Id = r.Id,
                        Name = r.Name,
                        BatPath = r.BatPath,
                        ProjectDir = r.ProjectDir ?? (Path.GetDirectoryName(r.BatPath) ?? r.LibPath),
                        LibraryId = lib.Id,
                        LibraryName = lib.Name,
                        IsUnc = SepPaths.IsUnc(r.BatPath),
                        DiscoveredAt = SepPaths.ParseIso(r.DiscoveredAt),
                    };
                }
                item.Code = r.Code;
                ApplyMeta(item, categories);
                item.IsFavorite = mineIds.Contains(r.Id) || mineBats.Contains(r.BatPath);
                item.IsActive = singleMode && active.Length > 0 && string.Equals(r.Name, active, StringComparison.OrdinalIgnoreCase);
                item.IsCached = lib.LastError != null;
                projs.Add(item);
            }
            foreach (var lib in libs)
                lib.ProjectCount = projs.Count(p => p.LibraryId == lib.Id);

            _libraries = libs;
            _projects = projs;
            _myProjects = Data.MyProjects
                .Select(m => projs.FirstOrDefault(p => p.Id == m.Id) ?? projs.FirstOrDefault(p => string.Equals(p.BatPath, SepPaths.Normalize(m.BatPath), StringComparison.OrdinalIgnoreCase)))
                .Where(p => p != null).Select(p => p!).Distinct().ToList();
            _allTags = Data.ProjectMeta.Values.SelectMany(m => m.Tags ?? new List<string>())
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key).ToList();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyMeta(ProjectItem item, Dictionary<string, CategoryInfo> categories)
    {
        Data.ProjectMeta.TryGetValue(item.Id, out var meta);
        item.DisplayName = meta?.Name;
        item.Owner = meta?.Owner;
        item.Tags = meta?.Tags?.ToList() ?? new List<string>();
        item.Description = meta?.Description;
        item.Notes = meta?.Notes;
        item.Status = meta?.Status is { Length: > 0 } st && IProjectCatalog.StatusOptions.Contains(st) ? st : string.Empty;
        item.Category = meta?.CategoryId is { Length: > 0 } cid && categories.TryGetValue(cid, out var cat) ? cat : null;
        item.MetaUpdatedAt = SepPaths.ParseIso(meta?.UpdatedAt);
    }

    private void CleanCategories()
    {
        Data.Categories = Data.Categories.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToList();
        for (int i = 0; i < Data.Categories.Count; i++)
        {
            var c = Data.Categories[i];
            if (string.IsNullOrWhiteSpace(c.Name)) c.Name = Strings.Category_Unnamed;
            if (string.IsNullOrWhiteSpace(c.Color)) c.Color = CategoryColors[i % CategoryColors.Length];
        }
        _categories = Data.Categories.Select(c => new CategoryInfo(c.Id, c.Name, c.Color)).ToList();
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
        using var limiter = new SemaphoreSlim(4);
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
            nowMine = RemoveMine(project) == 0;
            if (nowMine) AddMine(project);
        }
        project.IsFavorite = nowMine;
        RefreshMyProjects();
        Save();
        return nowMine;
    }

    public void SetMyProjects(IEnumerable<ProjectItem> projects, bool mine)
    {
        lock (_gate)
        {
            foreach (var p in projects)
            {
                RemoveMine(p);
                if (mine) AddMine(p);
                p.IsFavorite = mine;
            }
        }
        RefreshMyProjects();
        Save();
    }

    public void ClearMyProjects()
    {
        lock (_gate)
        {
            Data.MyProjects.Clear();
            foreach (var p in _projects) p.IsFavorite = false;
        }
        RefreshMyProjects();
        Save();
    }

    private int RemoveMine(ProjectItem project) =>
        Data.MyProjects.RemoveAll(m => m.Id == project.Id ||
            string.Equals(SepPaths.Normalize(m.BatPath), project.BatPath, StringComparison.OrdinalIgnoreCase));

    private void AddMine(ProjectItem project) =>
        Data.MyProjects.Add(new MyProjectRecord
        {
            Id = project.Id,
            Name = project.Name,
            BatPath = project.BatPath,
            LibId = project.LibraryId,
            Source = "user",
            AddedAt = SepPaths.NowIso(),
        });

    private void RefreshMyProjects()
    {
        lock (_gate)
        {
            _myProjects = Data.MyProjects
                .Select(m => _projects.FirstOrDefault(p => p.Id == m.Id))
                .Where(p => p != null).Select(p => p!).Distinct().ToList();
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetLastLaunchedLibrary(LibraryItem library)
    {
        Data.Settings.LastLaunched = library.Name;
        Data.Settings.LastMode = "library";
        lock (_gate) { foreach (var p in _projects) p.IsActive = false; }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetLastLaunchedAll()
    {
        Data.Settings.LastLaunched = "全部";   // the literal the Python UI writes for this mode
        Data.Settings.LastMode = "all";
        lock (_gate) { foreach (var p in _projects) p.IsActive = false; }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── metadata (mirrors e3d_store.update_project_meta) ─────────────────────────

    public void UpdateProjectMeta(ProjectItem project, ProjectMetaUpdate update)
    {
        lock (_gate) ApplyUpdate(project, update);
        RefreshTags();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateProjectsMeta(IEnumerable<ProjectItem> projects, ProjectMetaUpdate update)
    {
        lock (_gate) foreach (var p in projects) ApplyUpdate(p, update);
        RefreshTags();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyUpdate(ProjectItem project, ProjectMetaUpdate update)
    {
        if (!Data.ProjectMeta.TryGetValue(project.Id, out var meta))
        {
            meta = new ProjectMetaRecord();
            Data.ProjectMeta[project.Id] = meta;
        }
        if (update.DisplayName != null) meta.Name = Truncate(Squash(update.DisplayName), 60);
        if (update.CategoryId != null) meta.CategoryId = _categories.Any(c => c.Id == update.CategoryId) ? update.CategoryId : string.Empty;
        if (update.Tags != null) meta.Tags = NormalizeTags(update.Tags);
        if (update.Description != null) meta.Description = Truncate(update.Description.Trim(), 2000);
        if (update.Notes != null) meta.Notes = Truncate(update.Notes.Trim(), 2000);
        if (update.Status != null) meta.Status = IProjectCatalog.StatusOptions.Contains(update.Status) ? update.Status : string.Empty;
        if (update.Owner != null) meta.Owner = Truncate(update.Owner.Trim(), 100);
        meta.UpdatedAt = SepPaths.NowIso();
        ApplyMeta(project, _categories.ToDictionary(c => c.Id, c => c));
    }

    private void RefreshTags()
    {
        lock (_gate)
        {
            _allTags = Data.ProjectMeta.Values.SelectMany(m => m.Tags ?? new List<string>())
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key).ToList();
        }
    }

    /// <summary>e3d_store.normalize_tags: collapse whitespace, drop empties/duplicates, at most 20 tags of 20 chars.</summary>
    private static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in tags)
        {
            string t = Squash(raw);
            if (t.Length == 0 || t.Length > 20 || !seen.Add(t)) continue;
            result.Add(t);
            if (result.Count >= 20) break;
        }
        return result;
    }

    private static string Squash(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    public CategoryInfo AddCategory(string name, string? color = null)
    {
        name = Truncate(Squash(name), 30);
        if (name.Length == 0) throw new ArgumentException(Strings.Category_NameRequired);
        string id = SepPaths.GenerateId("cat", name.ToLowerInvariant());
        CategoryInfo info;
        lock (_gate)
        {
            var existing = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (existing != null) return new CategoryInfo(existing.Id, existing.Name, existing.Color);
            var rec = new CategoryRecord { Id = id, Name = name, Color = color ?? CategoryColors[Data.Categories.Count % CategoryColors.Length] };
            Data.Categories.Add(rec);
            info = new CategoryInfo(rec.Id, rec.Name, rec.Color);
            _categories = Data.Categories.Select(c => new CategoryInfo(c.Id, c.Name, c.Color)).ToList();
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    public (bool Success, string Message) UpdateCategory(string id, string? name, string? color)
    {
        lock (_gate)
        {
            var rec = Data.Categories.FirstOrDefault(c => c.Id == id);
            if (rec == null) return (false, Strings.Category_NotFound);
            if (name != null)
            {
                string n = Truncate(Squash(name), 30);
                if (n.Length > 0)
                {
                    if (Data.Categories.Any(o => o != rec && o.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                        return (false, string.Format(Strings.Category_Duplicate, n));
                    rec.Name = n;
                }
            }
            if (!string.IsNullOrWhiteSpace(color)) rec.Color = color;
            _categories = Data.Categories.Select(c => new CategoryInfo(c.Id, c.Name, c.Color)).ToList();
            var map = _categories.ToDictionary(c => c.Id, c => c);
            foreach (var p in _projects) ApplyMeta(p, map);
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return (true, Strings.Category_Saved);
    }

    public bool RemoveCategory(string id)
    {
        lock (_gate)
        {
            int removed = Data.Categories.RemoveAll(c => c.Id == id);
            if (removed == 0) return false;
            foreach (var meta in Data.ProjectMeta.Values.Where(m => m.CategoryId == id))
            {
                meta.CategoryId = string.Empty;
                meta.UpdatedAt = SepPaths.NowIso();
            }
            _categories = Data.Categories.Select(c => new CategoryInfo(c.Id, c.Name, c.Color)).ToList();
            var map = _categories.ToDictionary(c => c.Id, c => c);
            foreach (var p in _projects) ApplyMeta(p, map);
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ── notifications (mirrors e3d_store.add_device_notification) ────────────────

    public NotificationRecord AddNotification(string level, string title, string message, string? actionLabel = null, string? actionUrl = null)
    {
        NotificationRecord item;
        lock (_gate)
        {
            var open = Data.Notifications.FirstOrDefault(n => n.Title == title && !n.Dismissed);
            if (open != null)
            {
                open.Message = message;
                open.UpdatedAt = SepPaths.NowIso();
                item = open;
            }
            else
            {
                item = new NotificationRecord
                {
                    Id = SepPaths.GenerateId("notif", $"{title}_{message}_{SepPaths.NowIso()}"),
                    Level = level,
                    Title = title,
                    Message = message,
                    ActionLabel = actionLabel,
                    ActionUrl = actionUrl,
                    CreatedAt = SepPaths.NowIso(),
                };
                Data.Notifications.Add(item);
                if (Data.Notifications.Count > 30) Data.Notifications.RemoveRange(0, Data.Notifications.Count - 30);
            }
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public void DismissNotification(string idOrAll)
    {
        lock (_gate)
        {
            foreach (var n in Data.Notifications)
                if (idOrAll == "all" || n.Id == idOrAll) n.Dismissed = true;
        }
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ImportData(SepData data)
    {
        lock (_gate)
        {
            Data.Version = Math.Max(data.Version, 3);
            Data.Settings = data.Settings;
            Data.Categories = data.Categories;
            Data.ProjectMeta = data.ProjectMeta;
            Data.Libraries = data.Libraries;
            Data.MyProjects = data.MyProjects;
            Data.AllProjectsCache = data.AllProjectsCache;
            Data.Notifications = data.Notifications;
            Data.Extra = data.Extra;
            if (string.IsNullOrWhiteSpace(Data.Settings.LocalProjectsDir)) Data.Settings.LocalProjectsDir = LocalProjectsDir;
            _lastFullScan = DateTime.MinValue;
        }
        CleanCategories();
        RebuildModels();
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

    public string? GetPluginDisplayName(string pluginName)
    {
        lock (_gate)
        {
            return Data.PluginMeta.TryGetValue(pluginName, out var meta) ? meta.DisplayName : null;
        }
    }

    public void SetPluginDisplayName(string pluginName, string? displayName)
    {
        lock (_gate)
        {
            if (!Data.PluginMeta.TryGetValue(pluginName, out var meta))
            {
                meta = new PluginMetaRecord();
                Data.PluginMeta[pluginName] = meta;
            }
            meta.DisplayName = displayName;
            _store.SaveData(Data);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
