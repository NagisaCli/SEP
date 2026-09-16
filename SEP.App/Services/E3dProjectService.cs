using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public class E3dProjectService : IE3dProjectService
{
    private readonly string _baseDir;
    private readonly string _pathsJsonPath;
    private readonly string _projectsJsonPath;

    public E3dPathsConfig PathsConfig { get; private set; } = new();
    public E3dProjectsConfig ProjectsConfig { get; private set; } = new();

    public E3dProjectService(string? baseDir = null)
    {
        _baseDir = baseDir ?? FindProjectBaseDir();
        _pathsJsonPath = Path.Combine(_baseDir, "e3d_paths.json");
        _projectsJsonPath = Path.Combine(_baseDir, "e3d_projects.json");

        LoadInitialConfigs();
    }

    private static string FindProjectBaseDir()
    {
        // BaseDirectory ends with a separator; without trimming it, GetParent() first returns the same
        // folder and the walk stops one level short (bin/Debug builds then never reach the repo root).
        string curr = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 5; i++)
        {
            if (File.Exists(Path.Combine(curr, "e3d_paths.json")) || File.Exists(Path.Combine(curr, "e3d_projects.json")))
            {
                return curr;
            }
            string? parent = Directory.GetParent(curr)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == curr) break;
            curr = parent;
        }
        // Nothing found: keep the config next to the executable (created on first save).
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Serialized form of each config as last loaded or written; a save that changes nothing skips the disk.
    private string _lastPathsJson = string.Empty;
    private string _lastProjectsJson = string.Empty;

    private void LoadInitialConfigs()
    {
        PathsConfig = LoadConfig<E3dPathsConfig>(_pathsJsonPath);
        _lastPathsJson = JsonSerializer.Serialize(PathsConfig, JsonOptions);

        ProjectsConfig = LoadConfig<E3dProjectsConfig>(_projectsJsonPath);
        _lastProjectsJson = JsonSerializer.Serialize(ProjectsConfig, JsonOptions);
    }

    private static T LoadConfig<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T();
        }
        catch (Exception ex)
        {
            // Unreadable file: keep a copy so the next save cannot silently replace the user's data with defaults.
            App.Log($"Config {Path.GetFileName(path)} could not be read ({ex.Message}); backing it up before continuing with defaults");
            try { File.Copy(path, path + ".corrupt.bak", overwrite: true); } catch { }
            return new T();
        }
    }

    /// <summary>Persists both config files (e3d_projects.json and e3d_paths.json), each only when it changed.</summary>
    public async Task SaveConfigAsync()
    {
        try
        {
            string projectsJson = JsonSerializer.Serialize(ProjectsConfig, JsonOptions);
            if (projectsJson != _lastProjectsJson)
            {
                await File.WriteAllTextAsync(_projectsJsonPath, projectsJson, Encoding.UTF8);
                _lastProjectsJson = projectsJson;
            }

            string pathsJson = JsonSerializer.Serialize(PathsConfig, JsonOptions);
            if (pathsJson != _lastPathsJson)
            {
                await File.WriteAllTextAsync(_pathsJsonPath, pathsJson, Encoding.UTF8);
                _lastPathsJson = pathsJson;
            }
        }
        catch { }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Up, DateTime At)> _hostCache = new(StringComparer.OrdinalIgnoreCase);

    // A host that answered stays "up" for 5 min; one that did not is retried after 30 s, so a share that
    // comes back (VPN reconnect) shows up on the next refresh instead of staying offline all session.
    private static readonly TimeSpan HostUpTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HostDownTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UncAccessTimeout = TimeSpan.FromSeconds(5);

    private static bool IsHostReachable(string host, int timeoutMs = 800)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (_hostCache.TryGetValue(host, out var cached) &&
            DateTime.UtcNow - cached.At < (cached.Up ? HostUpTtl : HostDownTtl))
        {
            return cached.Up;
        }

        bool up = false;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect(host, 445, null, null);
            if (result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMs)))
            {
                client.EndConnect(result);
                up = true;
            }
        }
        catch { }

        _hostCache[host] = (up, DateTime.UtcNow);
        return up;
    }

    /// <summary>Runs a blocking file-system call with a deadline; a call that overruns keeps running on its
    /// thread-pool thread but the caller gets <paramref name="fallback"/> (an SMB share that stops answering
    /// would otherwise block the scan for the full Windows timeout).</summary>
    private static T WithTimeout<T>(Func<T> action, TimeSpan timeout, T fallback, out bool timedOut)
    {
        var task = Task.Run(action);
        if (task.Wait(timeout)) { timedOut = false; return task.Result; }
        timedOut = true;
        return fallback;
    }

    // Last full scan, reused by pages that only read (health check, launch) so switching pages is instant.
    private List<ProjectItem>? _lastScan;
    private DateTime _lastScanAt;
    private static readonly TimeSpan ScanTtl = TimeSpan.FromSeconds(60);

    public async Task<List<ProjectItem>> LoadAllProjectsAsync(bool forceRescan = false)
    {
        if (!forceRescan && _lastScan != null && DateTime.UtcNow - _lastScanAt < ScanTtl)
        {
            RefreshFlags(_lastScan);
            return _lastScan;
        }

        var items = await ScanProjectsAsync();
        _lastScan = items;
        _lastScanAt = DateTime.UtcNow;
        return items;
    }

    private void RefreshFlags(List<ProjectItem> items)
    {
        foreach (var item in items)
        {
            item.IsFavorite = ProjectsConfig.Favorites.Contains(item.Code, StringComparer.OrdinalIgnoreCase);
            item.IsActive = !string.IsNullOrEmpty(ProjectsConfig.LastActiveProject) &&
                            item.Code.Equals(ProjectsConfig.LastActiveProject, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<List<ProjectItem>> ScanProjectsAsync()
    {
        var items = new List<ProjectItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Load from e3d_projects.json mapped projects in parallel
        var tasks = ProjectsConfig.Projects.Select(async kv =>
        {
            string code = kv.Key.Trim().ToUpperInvariant();
            string path = kv.Value.Trim();
            if (string.IsNullOrWhiteSpace(path)) return null;
            return await InspectProjectInternalAsync(code, path);
        });

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            if (r == null) continue;
            items.Add(r);
            try { seenPaths.Add(Path.GetFullPath(r.Path)); } catch { }
        }

        // 2. Scan default projects_dir if configured and exists
        string? projectsDir = PathsConfig.ProjectsDir;
        if (!string.IsNullOrEmpty(projectsDir) && Directory.Exists(projectsDir))
        {
            await Task.Run(() =>
            {
                try
                {
                    foreach (var subDir in Directory.GetDirectories(projectsDir))
                    {
                        string fullPath = Path.GetFullPath(subDir);
                        if (seenPaths.Contains(fullPath)) continue;

                        string dirName = Path.GetFileName(subDir);
                        if (dirName.StartsWith(".") || dirName.Equals("000", StringComparison.OrdinalIgnoreCase)) continue;

                        // Check if it has 000 folder or evars
                        bool isE3d = Directory.GetDirectories(subDir, "*000").Length > 0 ||
                                     Directory.GetFiles(subDir, "evars*.bat").Length > 0;

                        if (isE3d)
                        {
                            string code = dirName.ToUpperInvariant();
                            var p = InspectProjectSync(code, subDir);
                            items.Add(p);
                            seenPaths.Add(fullPath);
                        }
                    }
                }
                catch { }
            });
        }

        // 3. Update favorite and active states
        RefreshFlags(items);

        return items.OrderByDescending(x => x.IsFavorite)
                    .ThenByDescending(x => x.IsActive)
                    .ThenBy(x => x.Code)
                    .ToList();
    }

    public Task<ProjectItem?> InspectProjectAsync(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            return Task.FromResult<ProjectItem?>(null);

        string code = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToUpperInvariant();
        return Task.FromResult<ProjectItem?>(InspectProjectSync(code, projectPath));
    }

    private Task<ProjectItem> InspectProjectInternalAsync(string code, string path)
    {
        return Task.Run(() => InspectProjectSync(code, path));
    }

    private ProjectItem InspectProjectSync(string code, string path)
    {
        var item = new ProjectItem
        {
            Code = code,
            Name = code,
            Path = path,
            IsUnc = path.StartsWith(@"\\")
        };

        // Network shares: a quick TCP probe first (an unreachable host would otherwise cost the full
        // SMB timeout), then every folder access under a deadline.
        var deadline = item.IsUnc ? UncAccessTimeout : TimeSpan.FromSeconds(30);
        if (item.IsUnc)
        {
            string withoutPrefix = path.TrimStart('\\');
            int slashIdx = withoutPrefix.IndexOf('\\');
            string host = slashIdx > 0 ? withoutPrefix.Substring(0, slashIdx) : withoutPrefix;
            if (!IsHostReachable(host))
            {
                item.Exists = false;
                item.Availability = ProjectAvailability.HostOffline;
                return item;
            }
        }

        bool exists = WithTimeout(() => Directory.Exists(path), deadline, false, out bool timedOut);
        if (timedOut)
        {
            item.Exists = false;
            item.Availability = ProjectAvailability.Unresponsive;
            return item;
        }
        if (!exists)
        {
            item.Exists = false;
            item.Availability = ProjectAvailability.NotMounted;
            return item;
        }

        var metrics = WithTimeout(() => CollectMetrics(path), deadline, null, out timedOut);
        if (metrics == null)
        {
            item.Availability = timedOut ? ProjectAvailability.Unresponsive : ProjectAvailability.MetricsUnavailable;
            return item;
        }

        item.LockFiles = metrics.Value.LockFiles;
        item.LockCount = metrics.Value.LockFiles.Count;
        item.IsLocked = metrics.Value.LockFiles.Count > 0;
        item.FileCount = metrics.Value.FileCount;
        double mb = metrics.Value.TotalBytes / (1024.0 * 1024.0);
        item.SizeHuman = mb > 1024 ? $"{(mb / 1024.0):F1} GB" : $"{mb:F1} MB";

        return item;
    }

    /// <summary>Lock files inside the *000 folders plus a shallow size/count of the project root; null on error.</summary>
    private static (List<string> LockFiles, int FileCount, long TotalBytes)? CollectMetrics(string path)
    {
        try
        {
            var lckFiles = new List<string>();
            foreach (var d in Directory.GetDirectories(path, "*000", SearchOption.TopDirectoryOnly))
            {
                foreach (var f in Directory.GetFiles(d, "*.lck", SearchOption.TopDirectoryOnly))
                {
                    lckFiles.Add(Path.GetFileName(f));
                }
            }

            int fileCount = 0;
            long totalBytes = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly))
            {
                fileCount++;
                try { totalBytes += new FileInfo(f).Length; } catch { }
            }
            return (lckFiles, fileCount, totalBytes);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ToggleFavoriteAsync(string projectCode)
    {
        if (ProjectsConfig.Favorites.Contains(projectCode, StringComparer.OrdinalIgnoreCase))
        {
            ProjectsConfig.Favorites.RemoveAll(x => x.Equals(projectCode, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            ProjectsConfig.Favorites.Add(projectCode.ToUpperInvariant());
        }

        await SaveConfigAsync();
        return ProjectsConfig.Favorites.Contains(projectCode, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> SetActiveProjectAsync(string projectCode)
    {
        ProjectsConfig.LastActiveProject = projectCode.ToUpperInvariant();
        await SaveConfigAsync();
        return true;
    }

    public async Task<(bool Success, string Message)> CreateProjectAsync(string code, string name, string rootDir, string? templateDir = null)
    {
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        name = string.IsNullOrWhiteSpace(name) ? code : name.Trim();

        if (code.Length < 2 || code.Length > 5 || !Regex.IsMatch(code, "^[A-Z0-9]+$"))
        {
            return (false, Strings.Create_InvalidCode);
        }

        string root = string.IsNullOrWhiteSpace(rootDir) ? (PathsConfig.ProjectsDir ?? @"D:\AVEVA\Projects\E3D3.1") : rootDir;
        string targetDir = Path.Combine(root, code);

        return await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(targetDir);
                string lc = code.ToLowerInvariant();

                string[] subdirs = {
                    $"{lc}000", $"{lc}iso", $"{lc}dwg", $"{lc}mac", $"{lc}pic",
                    $"{lc}dflts", $"{lc}dia", $"{lc}etm", $"{lc}gcd", $"{lc}info",
                    $"{lc}psi", $"{lc}ste", $"{lc}tpl"
                };

                foreach (var sub in subdirs)
                {
                    Directory.CreateDirectory(Path.Combine(targetDir, sub));
                }

                // Clone from template if provided
                if (!string.IsNullOrEmpty(templateDir) && Directory.Exists(templateDir))
                {
                    foreach (var srcSub in Directory.GetDirectories(templateDir))
                    {
                        string subName = Path.GetFileName(srcSub);
                        if (subName.ToLowerInvariant().EndsWith("000"))
                        {
                            string dest000 = Path.Combine(targetDir, $"{lc}000");
                            CopyDirectory(srcSub, dest000);
                        }
                    }
                }

                // Generate evars{code}.bat
                string evarsBat = Path.Combine(targetDir, $"evars{code}.bat");
                string evarsContent = $@"rem   AVEVA Everything3D Project Environment: {code}
rem   Generated by SEP (Smart E3D Platform)
rem ------------------------------------------------------------
SET {code}000=%projects_dir%{code}\\{lc}000
SET {code}ISO=%projects_dir%{code}\\{lc}iso
SET {code}MAC=%projects_dir%{code}\\{lc}mac
SET {code}PIC=%projects_dir%{code}\\{lc}pic
SET {code}DFLTS=%projects_dir%{code}\\{lc}dflts
SET {code}STE=%projects_dir%{code}\\{lc}ste
SET {code}TPL=%projects_dir%{code}\\{lc}tpl
SET {code}DIA=%projects_dir%{code}\\{lc}dia
SET {code}INFO=%projects_dir%{code}\\{lc}info
SET {code}PSI=%projects_dir%{code}\\{lc}psi
SET {code}GCD=%projects_dir%{code}\\{lc}gcd
SET {code}DATA=%projects_dir%{code}\\{lc}dflts\\Data\\
SET {code}DWG=%projects_dir%{code}\\{lc}dwg
SET {code}ETM=%projects_dir%{code}\\{lc}etm
SET {code}000ID={code}
";
                File.WriteAllText(evarsBat, evarsContent, Encoding.GetEncoding("GBK"));

                // Register to ProjectInfo.xml & projects.ini
                RegisterToProjectInfoXml(root, code, name, targetDir);
                RegisterToProjectsIni(code, name, targetDir);

                // Add to e3d_projects.json
                ProjectsConfig.Projects[code] = targetDir;
                SaveConfigAsync().Wait();

                return (true, string.Format(Strings.Create_Success, code));
            }
            catch (Exception ex)
            {
                return (false, string.Format(Strings.Create_Failed, ex.Message));
            }
        });
    }

    public async Task<(bool Success, string Message)> DecommissionProjectAsync(string projectPath, string archiveDir, bool doArchive, bool doDelete)
    {
        if (!Directory.Exists(projectPath))
            return (false, Strings.Decommission_DirMissing);

        string code = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToUpperInvariant();

        return await Task.Run(() =>
        {
            try
            {
                // Check locks
                var locks = Directory.GetFiles(projectPath, "*.lck", SearchOption.AllDirectories);
                if (locks.Length > 0)
                {
                    return (false, string.Format(Strings.Decommission_LocksFound, locks.Length));
                }

                // Archive
                if (doArchive)
                {
                    Directory.CreateDirectory(archiveDir);
                    string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string zipFile = Path.Combine(archiveDir, $"{code}_Backup_{ts}.zip");
                    ZipFile.CreateFromDirectory(projectPath, zipFile);
                }

                // Unregister
                string parentDir = Path.GetDirectoryName(projectPath) ?? "";
                UnregisterFromProjectInfoXml(parentDir, code);
                UnregisterFromProjectsIni(code);

                // Remove from e3d_projects.json
                ProjectsConfig.Projects.Remove(code);
                ProjectsConfig.Favorites.RemoveAll(x => x.Equals(code, StringComparison.OrdinalIgnoreCase));
                SaveConfigAsync().Wait();

                // Delete physical files if requested
                if (doDelete)
                {
                    Directory.Delete(projectPath, true);
                }

                return (true, string.Format(Strings.Decommission_Success, code));
            }
            catch (Exception ex)
            {
                return (false, string.Format(Strings.Decommission_Failed, ex.Message));
            }
        });
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
        {
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        }
        foreach (var d in Directory.GetDirectories(src))
        {
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }

    private static void RegisterToProjectInfoXml(string projectsDir, string code, string name, string address)
    {
        try
        {
            string xmlPath = Path.Combine(projectsDir, "ProjectInfo.xml");
            if (!File.Exists(xmlPath)) return;

            var doc = XDocument.Load(xmlPath);
            var root = doc.Root;
            if (root == null) return;

            XNamespace ns = root.GetDefaultNamespace();

            // Remove existing project element if exists
            var existing = root.Elements(ns + "Project")
                .FirstOrDefault(p => (string?)p.Element(ns + "Code") == code || (string?)p.Element(ns + "Project") == code);

            if (existing != null)
            {
                existing.Element(ns + "Address")?.SetValue(address);
                existing.Element(ns + "Name")?.SetValue(name);
                existing.Element(ns + "Description")?.SetValue(name);
            }
            else
            {
                var newProj = new XElement(ns + "Project",
                    new XElement(ns + "Project", code),
                    new XElement(ns + "Code", code),
                    new XElement(ns + "Address", address),
                    new XElement(ns + "Number", code),
                    new XElement(ns + "Name", name),
                    new XElement(ns + "Description", name),
                    new XElement(ns + "Message", ""),
                    new XElement(ns + "ApplicationType", "false")
                );
                root.Add(newProj);
            }

            doc.Save(xmlPath);
        }
        catch { }
    }

    private static void UnregisterFromProjectInfoXml(string projectsDir, string code)
    {
        try
        {
            string xmlPath = Path.Combine(projectsDir, "ProjectInfo.xml");
            if (!File.Exists(xmlPath)) return;

            var doc = XDocument.Load(xmlPath);
            var root = doc.Root;
            if (root == null) return;

            XNamespace ns = root.GetDefaultNamespace();
            var targets = root.Elements(ns + "Project")
                .Where(p => string.Equals((string?)p.Element(ns + "Code"), code, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals((string?)p.Element(ns + "Project"), code, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var t in targets) t.Remove();
            doc.Save(xmlPath);
        }
        catch { }
    }

    private static void RegisterToProjectsIni(string code, string name, string address)
    {
        try
        {
            string? iniPath = FindProjectsIniPath();
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) return;

            string content = File.ReadAllText(iniPath, Encoding.GetEncoding("GBK"));
            string sectionHeader = $"[{code}]";

            if (content.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Update
                string pattern = $@"\[{code}\][^\[]*";
                string replacement = $"[{code}]\r\nPATH = {address}\r\nNAME = {name}\r\nDESCRIPTION = {name}\r\n\r\n";
                content = Regex.Replace(content, pattern, replacement, RegexOptions.IgnoreCase);
            }
            else
            {
                content += $"\r\n\r\n[{code}]\r\nPATH = {address}\r\nNAME = {name}\r\nDESCRIPTION = {name}\r\n";
            }

            File.WriteAllText(iniPath, content, Encoding.GetEncoding("GBK"));
        }
        catch { }
    }

    private static void UnregisterFromProjectsIni(string code)
    {
        try
        {
            string? iniPath = FindProjectsIniPath();
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) return;

            string content = File.ReadAllText(iniPath, Encoding.GetEncoding("GBK"));
            string pattern = $@"\[{code}\][^\[]*";
            content = Regex.Replace(content, pattern, "", RegexOptions.IgnoreCase);
            File.WriteAllText(iniPath, content, Encoding.GetEncoding("GBK"));
        }
        catch { }
    }

    private static string? FindProjectsIniPath()
    {
        string[] envs = { "AVEVA_DESIGN_PROJECT_DIRS", "E3D_PROJECT_DIRS", "PDMSWK" };
        foreach (var env in envs)
        {
            string? val = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(val))
            {
                string cand = Path.Combine(val, "projects.ini");
                if (File.Exists(cand)) return cand;
            }
        }
        string[] defaults = { @"D:\AVEVA\Projects\projects.ini", @"C:\AVEVA\Projects\projects.ini" };
        return defaults.FirstOrDefault(File.Exists);
    }
}
