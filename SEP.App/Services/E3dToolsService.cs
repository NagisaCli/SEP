using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

/// <summary>
/// The toolbox behind the Tools page — the port of e3d_diag.py: health check and safe repair of the E3D
/// configuration files (evars.init / custom_evars.bat dead paths, offline network mounts, unsupported syntax),
/// USERDATA clean-up, and network / share diagnosis of a library path.
/// </summary>
public sealed class E3dToolsService
{
    private const string ManagedStartMarker = ">>> SEP MANAGED PROJECTS";
    private const string ManagedEndMarker = "<<< SEP MANAGED PROJECTS";
    private const string PluginsStartMarker = ">>> SEP MANAGED PLUGINS";
    private const string PluginsEndMarker = "<<< SEP MANAGED PLUGINS";
    private static readonly TimeSpan PathProbe = TimeSpan.FromSeconds(1.5);
    private static readonly Regex SetLineRe = new(@"^\s*set\s+([a-zA-Z0-9_]+)\s*=\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuotedSetRe = new(@"^\s*set\s+""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IfExistVarRe = new(@"if\s+(?:not\s+)?exist\s+""%([^%]+)%""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NetUseRe = new(@"net\s+use\s+(?:[a-zA-Z]:|\*)\s+(\\\\[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CallRe = new(@"(?:if\s+exist\s+[""']?([^""'\r\n]+)[""']?\s+)?call\s+[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ErrorCodeRe = new(@"(?:System error|发生系统错误|系统错误)\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] PmlVars = { "pmlui", "pmllib", "pmlnet", "caf_addins_path", "pml_ui", "pml_lib" };
    private static readonly string[] SkipCallTargets = { "projects.bat", "custom_evars.bat", "avevacatalogue", "evarsavevacatalogue.bat" };

    private readonly IProjectCatalog _catalog;

    public E3dToolsService(IProjectCatalog catalog)
    {
        _catalog = catalog;
    }

    // ── locations ────────────────────────────────────────────────────────────────

    public string InstallDir
    {
        get
        {
            string d = SepPaths.Normalize(_catalog.Paths.InstallDir);
            if (d.Length == 0 && !string.IsNullOrEmpty(_catalog.Paths.EvarsBat)) d = Path.GetDirectoryName(_catalog.Paths.EvarsBat) ?? string.Empty;
            return d.Length == 0 ? @"D:\AVEVA\Everything3D3.1" : d;
        }
    }

    public string EvarsInitPath => !string.IsNullOrEmpty(_catalog.Paths.EvarsInit) ? _catalog.Paths.EvarsInit : Path.Combine(InstallDir, "evars.init");
    public string EvarsBatPath => !string.IsNullOrEmpty(_catalog.Paths.EvarsBat) ? _catalog.Paths.EvarsBat : Path.Combine(InstallDir, "evars.bat");
    public string CustomEvarsPath => Path.Combine(_catalog.LocalProjectsDir, "custom_evars.bat");

    /// <summary>E3D USERDATA folder: the install drive first, then the usual drives.</summary>
    public string UserDataDir
    {
        get
        {
            var candidates = new List<string>();
            string drive = Path.GetPathRoot(InstallDir) ?? string.Empty;
            if (drive.Length > 0) candidates.Add(Path.Combine(drive, "AVEVA", "USERDATA"));
            candidates.AddRange(new[] { @"D:\AVEVA\USERDATA", @"C:\AVEVA\USERDATA", @"E:\AVEVA\USERDATA" });
            return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        }
    }

    // ── E3D configuration health ─────────────────────────────────────────────────

    public Task<DiagReport> DiagnoseConfigAsync() => Task.Run(DiagnoseConfig);

    private DiagReport DiagnoseConfig()
    {
        var checks = new List<DiagCheck>();
        string installDir = InstallDir, evarsBat = EvarsBatPath, evarsInit = EvarsInitPath, custom = CustomEvarsPath;
        string projectsDir = _catalog.LocalProjectsDir;

        // 1. core files
        bool installOk = Directory.Exists(installDir);
        bool evarsOk = File.Exists(evarsBat) && File.Exists(evarsInit);
        checks.Add(new DiagCheck
        {
            Id = "e3d_core_files",
            Name = Strings.Tools_CheckCoreFiles,
            Status = installOk && evarsOk ? CheckStatus.Ok : CheckStatus.Fail,
            Detail = installOk && evarsOk ? string.Format(Strings.Tools_CheckCoreFilesOk, installDir) : string.Format(Strings.Tools_CheckCoreFilesMissing, installDir),
        });

        // 2. evars.init PML / environment paths
        var initLines = new List<FlaggedLine>();
        if (File.Exists(evarsInit))
        {
            try
            {
                var (text, _) = SepPaths.ReadTextSmart(evarsInit);
                int no = 0;
                foreach (var rawLine in text.Split('\n'))
                {
                    no++;
                    string raw = rawLine.Trim();
                    if (IsComment(raw)) continue;
                    foreach (var p in PathsOfSetLine(raw, pmlOnly: true))
                    {
                        var (ok, reason) = CheckPath(p);
                        if (!ok) initLines.Add(new FlaggedLine(no, raw, p, reason, false));
                    }
                }
            }
            catch (Exception ex)
            {
                initLines.Add(new FlaggedLine(0, string.Empty, evarsInit, ex.Message, false));
            }
        }
        checks.Add(new DiagCheck
        {
            Id = "evars_init_paths",
            Name = Strings.Tools_CheckEvarsInit,
            Status = initLines.Count > 0 ? CheckStatus.Warn : CheckStatus.Ok,
            Detail = initLines.Count > 0 ? string.Format(Strings.Tools_CheckEvarsInitDead, initLines.Count) : Strings.Tools_CheckEvarsInitOk,
            Lines = initLines,
            Fix = initLines.Count > 0 ? ConfigCleanFix() : null,
        });

        // 3. custom_evars.bat
        var invalid = new List<FlaggedLine>();
        var timeouts = new List<FlaggedLine>();
        if (File.Exists(custom))
        {
            try
            {
                var (text, _) = SepPaths.ReadTextSmart(custom);
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                bool inManaged = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string raw = lines[i].Trim();
                    int no = i + 1;
                    if (raw.Contains(ManagedStartMarker) || raw.Contains(PluginsStartMarker)) { inManaged = true; continue; }
                    if (raw.Contains(ManagedEndMarker) || raw.Contains(PluginsEndMarker)) { inManaged = false; continue; }
                    if (IsComment(raw)) continue;

                    if (raw is "(" or ")" || raw.EndsWith('(') || raw.StartsWith(')'))
                    {
                        invalid.Add(new FlaggedLine(no, raw, string.Empty, Strings.Tools_ReasonParentheses, inManaged));
                        continue;
                    }
                    if (QuotedSetRe.IsMatch(raw))
                    {
                        invalid.Add(new FlaggedLine(no, raw, string.Empty, Strings.Tools_ReasonQuotedSet, inManaged));
                        continue;
                    }
                    var mv = IfExistVarRe.Match(raw);
                    if (mv.Success)
                    {
                        string var = mv.Groups[1].Value;
                        bool defined = lines.Take(i).Any(l => Regex.IsMatch(l, @"^\s*set\s+" + Regex.Escape(var) + "=", RegexOptions.IgnoreCase))
                                       || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(var));
                        if (!defined)
                        {
                            invalid.Add(new FlaggedLine(no, raw, string.Empty, string.Format(Strings.Tools_ReasonUndefinedVar, var), inManaged));
                            continue;
                        }
                    }
                    foreach (var p in PathsOfSetLine(raw, pmlOnly: false))
                    {
                        var (ok, reason) = CheckPath(p);
                        if (!ok) invalid.Add(new FlaggedLine(no, raw, p, reason, inManaged));
                    }
                    var mn = NetUseRe.Match(raw);
                    if (mn.Success && !IsUncReachable(mn.Groups[1].Value))
                        timeouts.Add(new FlaggedLine(no, raw, mn.Groups[1].Value, Strings.Tools_ReasonOfflineMount, inManaged));

                    if (!inManaged)
                    {
                        var mc = CallRe.Match(raw);
                        if (mc.Success)
                        {
                            string target = SepPaths.Normalize(ExpandEvars((mc.Groups[1].Success ? mc.Groups[1].Value : mc.Groups[2].Value).Trim(), projectsDir, installDir));
                            string low = target.ToLowerInvariant();
                            if (low.EndsWith(".bat") && !SkipCallTargets.Any(low.Contains))
                            {
                                var (ok, reason) = CheckPath(target);
                                if (!ok) (target.StartsWith(@"\\") ? timeouts : invalid).Add(new FlaggedLine(no, raw, target, reason, false));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                invalid.Add(new FlaggedLine(0, string.Empty, custom, ex.Message, false));
            }
        }
        var customDetails = new List<string>();
        if (invalid.Count > 0) customDetails.Add(string.Format(Strings.Tools_CheckCustomDead, invalid.Count));
        if (timeouts.Count > 0) customDetails.Add(string.Format(Strings.Tools_CheckCustomOffline, timeouts.Count));
        checks.Add(new DiagCheck
        {
            Id = "custom_evars_health",
            Name = Strings.Tools_CheckCustomEvars,
            Status = customDetails.Count > 0 ? CheckStatus.Warn : CheckStatus.Ok,
            Detail = customDetails.Count > 0 ? string.Join("; ", customDetails) : (File.Exists(custom) ? Strings.Tools_CheckCustomOk : Strings.Tools_CheckCustomAbsent),
            Lines = invalid.Concat(timeouts).OrderBy(l => l.LineNumber).ToList(),
            Fix = customDetails.Count > 0 ? ConfigCleanFix() : null,
        });

        // 4. my projects reachability
        var offline = new List<FlaggedLine>();
        foreach (var p in _catalog.MyProjects)
        {
            var (ok, reason) = CheckPath(p.BatPath);
            if (!ok) offline.Add(new FlaggedLine(0, p.Name, p.BatPath, reason, true));
        }
        checks.Add(new DiagCheck
        {
            Id = "my_projects_health",
            Name = Strings.Tools_CheckMyProjects,
            Status = offline.Count > 0 ? CheckStatus.Warn : CheckStatus.Ok,
            Detail = offline.Count > 0 ? string.Format(Strings.Tools_CheckMyProjectsOffline, offline.Count) : string.Format(Strings.Tools_CheckMyProjectsOk, _catalog.MyProjects.Count),
            Lines = offline,
            Fix = offline.Count > 0 ? ConfigCleanFix() : null,
        });

        return Finish(checks, installDir, "local");
    }

    private static DiagFix ConfigCleanFix() => new("e3d_config_clean", Strings.Tools_FixConfigTitle, new[] { Strings.Tools_FixConfigStep }, Array.Empty<string>(), false);

    /// <summary>One-click safe repair: backs up, comments out dead/offline lines, drops offline projects from the managed block.</summary>
    public Task<ToolResult> FixConfigAsync() => Task.Run(FixConfig);

    private ToolResult FixConfig()
    {
        var changes = new List<string>();
        string evarsInit = EvarsInitPath, custom = CustomEvarsPath, projectsDir = _catalog.LocalProjectsDir, installDir = InstallDir;

        // 1. evars.init
        if (File.Exists(evarsInit))
        {
            try
            {
                var (text, enc) = SepPaths.ReadTextSmart(evarsInit);
                var outLines = new List<string>();
                bool modified = false;
                foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
                {
                    string raw = line.Trim();
                    if (!IsComment(raw) && PathsOfSetLine(raw, pmlOnly: true).Any(p => !CheckPath(p).Ok))
                    {
                        outLines.Add("rem [SEP DISABLED - PATH NOT FOUND] " + line);
                        changes.Add($"evars.init: {Strings.Tools_ChangeDisabledDead} -> {raw}");
                        modified = true;
                    }
                    else outLines.Add(line);
                }
                if (modified) WriteWithBackup(evarsInit, outLines, enc);
            }
            catch (Exception ex)
            {
                return ToolResult.Failure(string.Format(Strings.Tools_FixFailed, "evars.init", ex.Message), changes);
            }
        }

        // 2. custom_evars.bat (outside the managed block)
        if (File.Exists(custom))
        {
            try
            {
                var (text, enc) = SepPaths.ReadTextSmart(custom);
                var outLines = new List<string>();
                bool modified = false, inManaged = false;
                foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
                {
                    string raw = line.Trim();
                    if (raw.Contains(ManagedStartMarker) || raw.Contains(PluginsStartMarker)) { inManaged = true; outLines.Add(line); continue; }
                    if (raw.Contains(ManagedEndMarker) || raw.Contains(PluginsEndMarker)) { inManaged = false; outLines.Add(line); continue; }
                    if (inManaged || IsComment(raw)) { outLines.Add(line); continue; }

                    if (raw is "(" or ")" || raw.EndsWith('(') || raw.StartsWith(')'))
                    {
                        outLines.Add("rem [SEP DISABLED - UNSUPPORTED SYNTAX] " + line);
                        changes.Add($"custom_evars.bat: {Strings.Tools_ChangeDisabledSyntax} -> {raw}");
                        modified = true; continue;
                    }
                    var mv = IfExistVarRe.Match(raw);
                    if (mv.Success)
                    {
                        string var = mv.Groups[1].Value;
                        bool defined = outLines.Any(l => Regex.IsMatch(l, @"^\s*set\s+" + Regex.Escape(var) + "=", RegexOptions.IgnoreCase))
                                       || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(var));
                        if (!defined)
                        {
                            outLines.Add("rem [SEP DISABLED - UNDEFINED VAR] " + line);
                            changes.Add($"custom_evars.bat: {Strings.Tools_ChangeDisabledVar} -> {raw}");
                            modified = true; continue;
                        }
                    }
                    var mq = QuotedSetRe.Match(raw);
                    if (mq.Success)
                    {
                        string unquoted = "set " + mq.Groups[1].Value;
                        outLines.Add(unquoted);
                        changes.Add($"custom_evars.bat: {Strings.Tools_ChangeFixedQuotes} -> {unquoted}");
                        modified = true; continue;
                    }
                    if (PathsOfSetLine(raw, pmlOnly: false).Any(p => !CheckPath(p).Ok))
                    {
                        outLines.Add("rem [SEP DISABLED - PATH NOT FOUND] " + line);
                        changes.Add($"custom_evars.bat: {Strings.Tools_ChangeDisabledDead} -> {raw}");
                        modified = true; continue;
                    }
                    var mn = NetUseRe.Match(raw);
                    if (mn.Success && !IsUncReachable(mn.Groups[1].Value))
                    {
                        outLines.Add("rem [SEP DISABLED - UNREACHABLE UNC] " + line);
                        changes.Add($"custom_evars.bat: {Strings.Tools_ChangeDisabledMount} -> {raw}");
                        modified = true; continue;
                    }
                    var mc = CallRe.Match(raw);
                    if (mc.Success)
                    {
                        string target = SepPaths.Normalize(ExpandEvars((mc.Groups[1].Success ? mc.Groups[1].Value : mc.Groups[2].Value).Trim(), projectsDir, installDir));
                        string low = target.ToLowerInvariant();
                        if (low.EndsWith(".bat") && !SkipCallTargets.Any(low.Contains) && !CheckPath(target).Ok)
                        {
                            outLines.Add("rem [SEP DISABLED - UNREACHABLE PROJECT] " + line);
                            changes.Add($"custom_evars.bat: {Strings.Tools_ChangeDisabledProject} -> {raw}");
                            modified = true; continue;
                        }
                    }
                    outLines.Add(line);
                }
                if (modified) WriteWithBackup(custom, outLines, enc);

                // 3. managed block: keep only reachable projects
                var managed = ReadManaged(custom);
                var valid = managed.Where(p => CheckPath(p).Ok).ToList();
                if (valid.Count != managed.Count)
                {
                    WriteManaged(custom, valid);
                    foreach (var r in managed.Except(valid)) changes.Add($"{Strings.Tools_ChangeManagedRemoved} -> {r}");
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Failure(string.Format(Strings.Tools_FixFailed, "custom_evars.bat", ex.Message), changes);
            }
        }

        return ToolResult.Success(changes.Count > 0 ? string.Format(Strings.Tools_FixDone, changes.Count) : Strings.Tools_FixNothing, changes);
    }

    private static void WriteWithBackup(string path, IEnumerable<string> lines, Encoding enc)
    {
        string bak = path + ".sep.bak";
        if (!File.Exists(bak)) File.Copy(path, bak);
        File.WriteAllText(path, string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n", enc);
    }

    private static readonly Regex ManagedBlockRe = new(@"(?ms)^[ \t]*:: >>> SEP MANAGED PROJECTS \(do not edit\) >>>.*?:: <<< SEP MANAGED PROJECTS <<<[ \t]*\r?\n?", RegexOptions.Compiled);
    private static readonly Regex ManagedCallRe = new(@"call\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> ReadManaged(string custom)
    {
        var (text, _) = SepPaths.ReadTextSmart(custom);
        var m = ManagedBlockRe.Match(text);
        if (!m.Success) return new List<string>();
        return m.Value.Split('\n').Select(l => ManagedCallRe.Match(l)).Where(x => x.Success).Select(x => x.Groups[1].Value).ToList();
    }

    private static void WriteManaged(string custom, List<string> bats)
    {
        var (text, enc) = SepPaths.ReadTextSmart(custom);
        var lines = new List<string> { ":: >>> SEP MANAGED PROJECTS (do not edit) >>>" };
        lines.AddRange(bats.Select(p => $"if exist \"{p}\" call \"{p}\""));
        lines.Add(":: <<< SEP MANAGED PROJECTS <<<");
        string block = string.Join("\r\n", lines) + "\r\n";
        text = ManagedBlockRe.Replace(text, string.Empty).TrimEnd('\r', '\n') + "\r\n\r\n" + block;
        File.WriteAllText(custom, text, enc);
    }

    // ── USERDATA clean-up ────────────────────────────────────────────────────────

    public Task<ToolResult> CleanUserDataAsync() => Task.Run(() =>
    {
        string dir = UserDataDir;
        if (!Directory.Exists(dir)) return ToolResult.Failure(string.Format(Strings.Tools_UserDataMissing, dir));
        var cleaned = new List<string>();
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string low = Path.GetFileName(file).ToLowerInvariant();
                bool junk = low.StartsWith("session_") || low.EndsWith(".lok") || low.EndsWith(".tmp") || low.EndsWith(".temp")
                            || low.EndsWith(".dmp") || low.EndsWith(".crash") || low == "avevaabalog.txt";
                if (!junk) continue;
                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);
                    cleaned.Add(Path.GetRelativePath(dir, file));
                    bytes += size;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Failure(string.Format(Strings.Tools_UserDataError, ex.Message), cleaned);
        }
        return ToolResult.Success(cleaned.Count > 0 ? string.Format(Strings.Tools_UserDataDone, cleaned.Count, bytes / 1024) : Strings.Tools_UserDataClean, cleaned);
    });

    // ── library / share diagnosis ────────────────────────────────────────────────

    public Task<DiagReport> DiagnoseLibraryAsync(string path) => Task.Run(() =>
    {
        string norm = SepPaths.Normalize(path);
        if (norm.Length == 0)
            return Finish(new List<DiagCheck> { new() { Id = "empty", Name = Strings.Tools_NetPath, Status = CheckStatus.Fail, Detail = Strings.Scan_NotFound } }, string.Empty, "local");
        return SepPaths.IsUnc(norm) ? DiagnoseUnc(norm) : DiagnoseLocal(norm);
    });

    private DiagReport DiagnoseLocal(string norm)
    {
        var checks = new List<DiagCheck>();
        bool exists = Bounded(() => File.Exists(norm) || Directory.Exists(norm), TimeSpan.FromSeconds(6));
        checks.Add(new DiagCheck { Id = "exists", Name = Strings.Tools_NetExists, Status = exists ? CheckStatus.Ok : CheckStatus.Fail, Detail = exists ? norm : Strings.Scan_NotFound });
        if (!exists) return Finish(checks, norm, "local");

        bool readable = Bounded(() => { try { if (Directory.Exists(norm)) Directory.EnumerateFileSystemEntries(norm).FirstOrDefault(); else File.OpenRead(norm).Dispose(); return true; } catch { return false; } }, TimeSpan.FromSeconds(6));
        checks.Add(new DiagCheck
        {
            Id = "readable", Name = Strings.Tools_NetReadable, Status = readable ? CheckStatus.Ok : CheckStatus.Fail,
            Detail = readable ? Strings.Tools_NetReadableOk : Strings.Tools_NetReadableNo,
            Fix = readable ? null : new DiagFix("local_permission", Strings.Tools_FixPermTitle, new[] { Strings.Tools_FixPermStep }, new[] { $"icacls \"{norm}\"" }, false),
        });
        bool writable = Directory.Exists(norm) && Bounded(() =>
        {
            try { string probe = Path.Combine(norm, ".sep_write_probe"); File.WriteAllText(probe, ""); File.Delete(probe); return true; } catch { return false; }
        }, TimeSpan.FromSeconds(6));
        checks.Add(new DiagCheck
        {
            Id = "writable", Name = Strings.Tools_NetWritable, Status = writable ? CheckStatus.Ok : CheckStatus.Warn,
            Detail = writable ? Strings.Tools_NetWritableOk : Strings.Tools_NetWritableNo,
        });
        AddProjectsCheck(checks, norm, reachable: true);
        return Finish(checks, norm, "local");
    }

    private DiagReport DiagnoseUnc(string norm)
    {
        var checks = new List<DiagCheck>();
        string host = SepPaths.UncHost(norm) ?? string.Empty;
        string share = ShareRoot(norm);

        bool exists = Bounded(() => Directory.Exists(norm) || File.Exists(norm), TimeSpan.FromSeconds(8));
        checks.Add(new DiagCheck { Id = "exists", Name = Strings.Tools_NetShareReachable, Status = exists ? CheckStatus.Ok : CheckStatus.Fail, Detail = exists ? norm : Strings.Tools_NetShareUnreachable });

        bool hostOk = host.Length > 0 && Bounded(() => { try { return System.Net.Dns.GetHostAddresses(host).Length > 0; } catch { return false; } }, TimeSpan.FromSeconds(3));
        checks.Add(new DiagCheck
        {
            Id = "host", Name = Strings.Tools_NetHost, Status = hostOk ? CheckStatus.Ok : CheckStatus.Fail,
            Detail = host.Length == 0 ? Strings.Tools_NetHostNone : string.Format(hostOk ? Strings.Tools_NetHostOk : Strings.Tools_NetHostFail, host),
            Fix = hostOk ? null : new DiagFix("host_dns", Strings.Tools_FixDnsTitle, new[] { string.Format(Strings.Tools_FixDnsStep, host) }, new[] { $"ping -n 1 {host}", $"net view \\\\{host}" }, false),
        });

        bool portOk = false;
        string portDetail;
        if (hostOk)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, 445);
                portOk = connect.Wait(3000) && client.Connected;
                portDetail = portOk ? string.Format(Strings.Tools_NetPortOk, host) : string.Format(Strings.Tools_NetPortFail, host, Strings.Tools_NetErrTimeout);
            }
            catch (Exception ex) { portDetail = string.Format(Strings.Tools_NetPortFail, host, ex.GetBaseException().Message); }
        }
        else portDetail = Strings.Tools_NetPortSkipped;
        checks.Add(new DiagCheck
        {
            Id = "port445", Name = Strings.Tools_NetPort, Status = portOk ? CheckStatus.Ok : (hostOk ? CheckStatus.Fail : CheckStatus.Skip), Detail = portDetail,
            Fix = hostOk && !portOk ? new DiagFix("smb_port", Strings.Tools_FixPortTitle, new[] { Strings.Tools_FixPortStep1, Strings.Tools_FixPortStep2 }, new[] { "netstat -an | findstr :445" }, false) : null,
        });

        string svc = SmbServiceStatus();
        checks.Add(new DiagCheck
        {
            Id = "smb_service", Name = Strings.Tools_NetService,
            Status = svc == "running" ? CheckStatus.Ok : svc == "unknown" ? CheckStatus.Warn : CheckStatus.Fail,
            Detail = svc switch { "running" => Strings.Tools_NetServiceRunning, "stopped" => Strings.Tools_NetServiceStopped, _ => Strings.Tools_NetServiceUnknown },
            Fix = svc == "stopped" ? new DiagFix("start_smb_service", Strings.Tools_FixServiceTitle, new[] { Strings.Tools_FixServiceStep }, new[] { "sc start lanmanworkstation" }, true) : null,
        });

        bool shareOk = false;
        int? errNum = null;
        string shareDetail;
        if (hostOk && portOk)
        {
            shareOk = exists || Bounded(() => Directory.Exists(share), TimeSpan.FromSeconds(8));
            if (shareOk) shareDetail = string.Format(Strings.Tools_NetShareOk, share);
            else
            {
                var (code, output) = RunHidden("net", $"use {share} /user:\"\" \"\"", 8);
                var m = ErrorCodeRe.Match(output);
                errNum = m.Success ? int.Parse(m.Groups[1].Value) : code;
                if (code == 0)
                {
                    shareOk = Bounded(() => Directory.Exists(share), TimeSpan.FromSeconds(8));
                    shareDetail = shareOk ? Strings.Tools_NetShareConnected : Strings.Tools_NetShareNoRead;
                }
                else shareDetail = NetErrorText(errNum);
            }
        }
        else shareDetail = Strings.Tools_NetShareSkipped;

        bool? guest = GuestAuthSetting();
        bool authLike = errNum is 5 or 86 or 1326 or 1331 or 1396;
        DiagFix? guestFix = (authLike || (!shareOk && hostOk && portOk && guest != true))
            ? new DiagFix("guest_auth", Strings.Tools_FixGuestTitle, new[] { Strings.Tools_FixGuestStep1, Strings.Tools_FixGuestStep2 },
                new[] { @"reg add ""HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"" /v AllowInsecureGuestAuth /t REG_DWORD /d 1 /f" }, true)
            : null;
        DiagFix? shareFix = errNum switch
        {
            71 => new DiagFix("session_limit", Strings.Tools_FixSessionsTitle, new[] { string.Format(Strings.Tools_FixSessionsStep, host) }, new[] { "net session /delete /y" }, true),
            1219 => new DiagFix("smb_reconnect", Strings.Tools_FixReconnectTitle, new[] { string.Format(Strings.Tools_FixReconnectStep, host) }, new[] { $"net use {share} /delete /y", "net use * /delete /y" }, false, share),
            _ => guestFix,
        };
        checks.Add(new DiagCheck
        {
            Id = "share", Name = Strings.Tools_NetShare, Status = shareOk ? CheckStatus.Ok : (hostOk && portOk ? CheckStatus.Fail : CheckStatus.Skip), Detail = shareDetail, Fix = shareFix,
        });

        CheckStatus guestStatus = guest == null ? CheckStatus.Skip : guest == true ? CheckStatus.Ok : (shareOk ? CheckStatus.Warn : CheckStatus.Fail);
        checks.Add(new DiagCheck
        {
            Id = "guest_policy", Name = Strings.Tools_NetGuest, Status = guestStatus,
            Detail = guest == null ? Strings.Tools_NetGuestUnknown : guest == true ? Strings.Tools_NetGuestOn : Strings.Tools_NetGuestOff,
            Fix = guestStatus == CheckStatus.Fail ? guestFix : null,
        });

        AddProjectsCheck(checks, norm, shareOk);
        return Finish(checks, norm, "unc");
    }

    private void AddProjectsCheck(List<DiagCheck> checks, string norm, bool reachable)
    {
        int count = 0;
        if (reachable)
        {
            try { count = LibraryScanner.ScanAsync(norm, TimeSpan.FromSeconds(12)).GetAwaiter().GetResult().Projects.Count; } catch { }
        }
        checks.Add(new DiagCheck
        {
            Id = "projects", Name = Strings.Tools_NetProjects,
            Status = count > 0 ? CheckStatus.Ok : (reachable ? CheckStatus.Fail : CheckStatus.Skip),
            Detail = count > 0 ? string.Format(Strings.Lib_ProjectsCount, count) : (reachable ? Strings.Scan_NoProjects : Strings.Tools_NetProjectsSkipped),
            Fix = reachable && count == 0 ? new DiagFix("no_projects", Strings.Tools_FixStructureTitle, new[] { Strings.Tools_FixStructureStep }, new[] { $"dir /b /s \"{norm}\\evars*.bat\"" }, false) : null,
        });
    }

    private static DiagReport Finish(List<DiagCheck> checks, string path, string protocol)
    {
        var fixes = new List<DiagFix>();
        foreach (var c in checks)
            if (c.Fix != null && fixes.All(f => f.Id != c.Fix.Id)) fixes.Add(c.Fix);
        int problems = checks.Count(c => c.Status is CheckStatus.Warn or CheckStatus.Fail);
        return new DiagReport { Ok = problems == 0, Path = path, Protocol = protocol, Checks = checks, Fixes = fixes, Problems = problems };
    }

    /// <summary>Runs one of the built-in fixes by id (the ones with commands the app can execute itself).</summary>
    public Task<ToolResult> ApplyFixAsync(DiagFix fix) => Task.Run(() =>
    {
        switch (fix.Id)
        {
            case "e3d_config_clean":
                return FixConfig();
            case "guest_auth":
            {
                if (GuestAuthSetting() == true) return ToolResult.Success(Strings.Tools_GuestAlready);
                // Registry writes under HKLM need elevation: run reg.exe through the UAC prompt.
                try
                {
                    var psi = new ProcessStartInfo("reg.exe", @"add HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters /v AllowInsecureGuestAuth /t REG_DWORD /d 1 /f")
                    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(15000);
                    return GuestAuthSetting() == true ? ToolResult.Success(Strings.Tools_GuestEnabled) : ToolResult.Failure(Strings.Tools_GuestFailed, needsAdmin: true);
                }
                catch (Exception ex)
                {
                    return ToolResult.Failure(string.Format(Strings.Tools_GuestFailedWith, ex.Message), needsAdmin: true);
                }
            }
            case "smb_reconnect":
            {
                if (string.IsNullOrEmpty(fix.Path)) return ToolResult.Failure(Strings.Tools_ReconnectUncOnly);
                var (code, output) = RunHidden("net", $"use {fix.Path} /delete /y", 10);
                if (code != 0 && code != 2) return ToolResult.Failure(Strings.Tools_ReconnectFailed, output.Split('\n'));
                return ToolResult.Success(string.Format(Strings.Tools_ReconnectDone, fix.Path));
            }
            default:
                return ToolResult.Failure(string.Format(Strings.Tools_FixManual, fix.Title), fix.Commands);
        }
    });

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static bool IsComment(string raw) => raw.Length == 0 || raw.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("::");

    private static IEnumerable<string> PathsOfSetLine(string line, bool pmlOnly)
    {
        var m = SetLineRe.Match(line);
        if (!m.Success) yield break;
        string var = m.Groups[1].Value.ToLowerInvariant();
        if (pmlOnly && !PmlVars.Contains(var)) yield break;
        foreach (var part in m.Groups[2].Value.Split(';'))
        {
            string p = part.Trim().Trim('"', '\'');
            if (p.Length == 0 || Regex.IsMatch(p, @"^%[a-zA-Z0-9_()]+%$")) continue;
            if (Regex.IsMatch(p, @"^[a-zA-Z]:[\\/]") || p.StartsWith(@"\\")) yield return SepPaths.Normalize(p);
        }
    }

    private static string ExpandEvars(string path, string projectsDir, string installDir)
    {
        string r = path;
        if (projectsDir.Length > 0) r = Regex.Replace(r, @"%projects_dir%\\?", projectsDir.TrimEnd('\\', '/') + "\\", RegexOptions.IgnoreCase);
        if (installDir.Length > 0)
        {
            r = Regex.Replace(r, @"%aveva_design_exe%\\?", installDir.TrimEnd('\\', '/') + "\\", RegexOptions.IgnoreCase);
            r = Regex.Replace(r, @"%eveva_design_exe%\\?", installDir.TrimEnd('\\', '/') + "\\", RegexOptions.IgnoreCase);
        }
        return Environment.ExpandEnvironmentVariables(r);
    }

    private static (bool Ok, string Reason) CheckPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return (false, Strings.Scan_NotFound);
        if (p.StartsWith(@"\\"))
            return IsUncReachable(p) ? (true, Strings.Tools_ReasonUncOnline) : (false, Strings.Tools_ReasonUncOffline);
        bool exists = Bounded(() => File.Exists(p) || Directory.Exists(p), PathProbe);
        return exists ? (true, Strings.Tools_ReasonLocalExists) : (false, Strings.Tools_ReasonLocalMissing);
    }

    private static bool IsUncReachable(string unc)
    {
        string? host = SepPaths.UncHost(unc);
        if (string.IsNullOrEmpty(host)) return false;
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(host, 445).Wait(PathProbe)) return false;
        }
        catch { return false; }
        return Bounded(() => File.Exists(unc) || Directory.Exists(unc), PathProbe);
    }

    private static bool Bounded(Func<bool> probe, TimeSpan timeout)
    {
        try
        {
            var work = Task.Run(probe);
            return work.Wait(timeout) && work.Result;
        }
        catch { return false; }
    }

    private static string ShareRoot(string norm)
    {
        var parts = norm.TrimStart('\\').Split('\\');
        return parts.Length >= 2 ? @"\\" + parts[0] + "\\" + parts[1] : norm;
    }

    private static (int Code, string Output) RunHidden(string file, string args, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutSeconds * 1000)) { try { p.Kill(); } catch { } return (-1, Strings.Scan_Timeout); }
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-2, ex.Message);
        }
    }

    private static string SmbServiceStatus()
    {
        var (code, output) = RunHidden("sc", "query lanmanworkstation", 6);
        if (code == 0 && Regex.IsMatch(output, @"STATE\s*:\s*\d+\s+RUNNING", RegexOptions.IgnoreCase)) return "running";
        return code == 0 ? "stopped" : "unknown";
    }

    private static bool? GuestAuthSetting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters");
            var value = key?.GetValue("AllowInsecureGuestAuth");
            return value is int i ? i == 1 : (bool?)null;
        }
        catch { return null; }
    }

    private static string NetErrorText(int? code) => code switch
    {
        null or -1 or -2 => Strings.Tools_NetErrTimeout,
        5 => Strings.Tools_NetErr5,
        53 => Strings.Tools_NetErr53,
        67 => Strings.Tools_NetErr67,
        71 => Strings.Tools_NetErr71,
        86 or 1326 => Strings.Tools_NetErr86,
        1219 => Strings.Tools_NetErr1219,
        1231 => Strings.Tools_NetErr1231,
        1331 => Strings.Tools_NetErr1331,
        _ => string.Format(Strings.Tools_NetErrOther, code),
    };
}
