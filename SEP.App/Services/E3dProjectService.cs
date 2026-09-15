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
        string curr = AppDomain.CurrentDomain.BaseDirectory;
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
        return @"c:\Muvsera\Projects\SEP";
    }

    private void LoadInitialConfigs()
    {
        try
        {
            if (File.Exists(_pathsJsonPath))
            {
                string json = File.ReadAllText(_pathsJsonPath, Encoding.UTF8);
                PathsConfig = JsonSerializer.Deserialize<E3dPathsConfig>(json) ?? new();
            }
        }
        catch { }

        try
        {
            if (File.Exists(_projectsJsonPath))
            {
                string json = File.ReadAllText(_projectsJsonPath, Encoding.UTF8);
                ProjectsConfig = JsonSerializer.Deserialize<E3dProjectsConfig>(json) ?? new();
            }
        }
        catch { }
    }

    public async Task SaveConfigAsync()
    {
        try
        {
            string json = JsonSerializer.Serialize(ProjectsConfig, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_projectsJsonPath, json, Encoding.UTF8);
        }
        catch { }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _hostCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsHostReachable(string host, int timeoutMs = 400)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (_hostCache.TryGetValue(host, out bool cached)) return cached;

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect(host, 445, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMs));
            if (!success)
            {
                _hostCache[host] = false;
                return false;
            }
            client.EndConnect(result);
            _hostCache[host] = true;
            return true;
        }
        catch
        {
            _hostCache[host] = false;
            return false;
        }
    }

    public async Task<List<ProjectItem>> LoadAllProjectsAsync(bool forceRescan = false)
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
        foreach (var item in items)
        {
            item.IsFavorite = ProjectsConfig.Favorites.Contains(item.Code, StringComparer.OrdinalIgnoreCase);
            item.IsActive = !string.IsNullOrEmpty(ProjectsConfig.LastActiveProject) &&
                            item.Code.Equals(ProjectsConfig.LastActiveProject, StringComparison.OrdinalIgnoreCase);
        }

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
            Source = path.StartsWith(@"\\") ? "网络UNC" : "本地"
        };

        // Fast UNC reachability test to avoid 25s Windows SMB freeze
        if (path.StartsWith(@"\\"))
        {
            string withoutPrefix = path.TrimStart('\\');
            int slashIdx = withoutPrefix.IndexOf('\\');
            string host = slashIdx > 0 ? withoutPrefix.Substring(0, slashIdx) : withoutPrefix;
            if (!IsHostReachable(host, 400))
            {
                item.Exists = false;
                item.SizeHuman = "网络离线";
                return item;
            }
        }

        if (!Directory.Exists(path))
        {
            item.Exists = false;
            item.SizeHuman = "未挂载/离线";
            return item;
        }

        try
        {
            var lckFiles = new List<string>();
            int fileCount = 0;
            long totalBytes = 0;

            // Look inside *000 directories for .lck files and file metrics
            foreach (var d in Directory.GetDirectories(path, "*000", SearchOption.TopDirectoryOnly))
            {
                foreach (var f in Directory.GetFiles(d, "*.lck", SearchOption.TopDirectoryOnly))
                {
                    lckFiles.Add(Path.GetFileName(f));
                }
            }

            // Quick metrics scan (capped depth to avoid freezing on slow UNC)
            foreach (var f in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly))
            {
                fileCount++;
                try { totalBytes += new FileInfo(f).Length; } catch { }
            }

            item.LockFiles = lckFiles;
            item.LockCount = lckFiles.Count;
            item.IsLocked = lckFiles.Count > 0;
            item.FileCount = fileCount;

            double mb = totalBytes / (1024.0 * 1024.0);
            item.SizeHuman = mb > 1024 ? $"{(mb / 1024.0):F1} GB" : $"{mb:F1} MB";
        }
        catch
        {
            item.SizeHuman = "就绪";
        }

        return item;
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
            return (false, "项目代号必须为 2~5 位英文字母或数字（例如 PRJ, APS, M01）。");
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

                return (true, $"项目 [{code}] 规范化创建成功并已注入 E3D 注册表！");
            }
            catch (Exception ex)
            {
                return (false, $"创建失败: {ex.Message}");
            }
        });
    }

    public async Task<(bool Success, string Message)> DecommissionProjectAsync(string projectPath, string archiveDir, bool doArchive, bool doDelete)
    {
        if (!Directory.Exists(projectPath))
            return (false, "项目物理目录不存在。");

        string code = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToUpperInvariant();

        return await Task.Run(() =>
        {
            try
            {
                // Check locks
                var locks = Directory.GetFiles(projectPath, "*.lck", SearchOption.AllDirectories);
                if (locks.Length > 0)
                {
                    return (false, $"检测到项目存在 {locks.Length} 个活跃锁文件，已安全拦截下线操作！");
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

                return (true, $"项目 [{code}] 已成功安全下线与冷备归档！");
            }
            catch (Exception ex)
            {
                return (false, $"下线失败: {ex.Message}");
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
