using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>
/// Finds the local AVEVA E3D installation on any PC, so a fresh machine works without editing Settings first:
/// Apps &amp; features entries first, then the usual install folders on every fixed drive. Only local disks are
/// probed, so this never waits on a network share.
/// </summary>
public static class E3dLocator
{
    private static readonly Regex ProjectsDirRe = new(@"(?im)^\s*(?:set\s+)?""?projects_dir=([^\r\n""]*)", RegexOptions.Compiled);
    private static readonly Lazy<string?> Detected = new(DetectInstallDir);

    /// <summary>The detected E3D install folder (contains evars.bat), or null.</summary>
    public static string? InstallDir => Detected.Value;

    /// <summary>Configured install folder, else the detected one, else the AVEVA default on D:.</summary>
    public static string InstallDirOrDefault(string? configured)
    {
        string c = SepPaths.Normalize(configured);
        if (c.Length > 0) return c;
        return InstallDir ?? @"D:\AVEVA\Everything3D3.1";
    }

    /// <summary>The AVEVA root next to the install (…\AVEVA), used for USERDATA / Plugins / Projects defaults.</summary>
    public static string? AvevaRoot(string? installDir)
    {
        string dir = SepPaths.Normalize(installDir);
        if (dir.Length == 0) dir = InstallDir ?? string.Empty;
        if (dir.Length == 0) return null;
        return Path.GetDirectoryName(dir.TrimEnd('\\'));
    }

    /// <summary>
    /// Fills whatever is missing (or points at a folder that does not exist on this PC) in the paths and settings.
    /// Returns true when something changed and should be saved.
    /// </summary>
    public static bool FillMissing(E3dPathsConfig paths, SepSettings settings)
    {
        bool changed = false;
        try
        {
            string install = SepPaths.Normalize(paths.InstallDir);
            if (install.Length == 0 || !File.Exists(Path.Combine(install, "evars.bat")))
            {
                // keep a configured evars.bat's folder when it is valid, otherwise detect
                string fromEvars = SepPaths.Normalize(Path.GetDirectoryName(paths.EvarsBat ?? string.Empty));
                string? found = fromEvars.Length > 0 && File.Exists(Path.Combine(fromEvars, "evars.bat")) ? fromEvars : InstallDir;
                if (found != null && !string.Equals(found, install, StringComparison.OrdinalIgnoreCase))
                {
                    paths.InstallDir = found;
                    install = found;
                    changed = true;
                }
            }
            if (install.Length == 0 || !Directory.Exists(install)) return changed;

            if (string.IsNullOrWhiteSpace(paths.EvarsBat) || !File.Exists(paths.EvarsBat))
            {
                string bat = Path.Combine(install, "evars.bat");
                if (File.Exists(bat)) { paths.EvarsBat = bat; changed = true; }
            }
            if (string.IsNullOrWhiteSpace(paths.EvarsInit) || !File.Exists(paths.EvarsInit))
            {
                string init = Path.Combine(install, "evars.init");
                if (File.Exists(init)) { paths.EvarsInit = init; changed = true; }
            }
            if (string.IsNullOrWhiteSpace(paths.E3dVersion))
            {
                paths.E3dVersion = VersionLabel(install);
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(paths.ProjectsDir) || !Directory.Exists(paths.ProjectsDir))
            {
                string? projects = ProjectsDirFromEvars(paths.EvarsBat) ?? DefaultProjectsDir(install);
                if (projects != null && !string.Equals(projects, paths.ProjectsDir, StringComparison.OrdinalIgnoreCase))
                {
                    paths.ProjectsDir = projects;
                    changed = true;
                }
            }
            // The local library SEP writes custom_evars.bat into: only replace a value that does not exist here
            // (typically the D:\ default on a PC whose E3D lives on C:\).
            string local = SepPaths.Normalize(settings.LocalProjectsDir);
            string detectedProjects = SepPaths.Normalize(paths.ProjectsDir);
            if ((local.Length == 0 || !Directory.Exists(local)) && detectedProjects.Length > 0 && Directory.Exists(detectedProjects)
                && !string.Equals(local, detectedProjects, StringComparison.OrdinalIgnoreCase))
            {
                settings.LocalProjectsDir = detectedProjects;
                changed = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"E3D detection failed: {ex.Message}");
        }
        return changed;
    }

    /// <summary>projects_dir= from evars.bat, when it names an existing local folder.</summary>
    public static string? ProjectsDirFromEvars(string? evarsBat)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(evarsBat) || !File.Exists(evarsBat)) return null;
            var (text, _) = SepPaths.ReadTextSmart(evarsBat);
            foreach (Match m in ProjectsDirRe.Matches(text))
            {
                string value = SepPaths.Normalize(m.Groups[1].Value);
                if (value.Length > 0 && !value.Contains('%') && Directory.Exists(value)) return value;
            }
        }
        catch { }
        return null;
    }

    private static string? DefaultProjectsDir(string install)
    {
        string? root = AvevaRoot(install);
        if (root == null) return null;
        string version = Regex.Match(Path.GetFileName(install.TrimEnd('\\')), @"\d+(\.\d+)*").Value;
        foreach (var cand in new[] { Path.Combine(root, "Projects", "E3D" + version), Path.Combine(root, "Projects") })
            if (Directory.Exists(cand)) return cand;
        return null;
    }

    private static string VersionLabel(string install)
    {
        string version = Regex.Match(Path.GetFileName(install.TrimEnd('\\')), @"\d+(\.\d+)*").Value;
        return version.Length > 0 ? $"AVEVA Everything3D {version}" : "AVEVA Everything3D";
    }

    private static string? DetectInstallDir()
    {
        var found = new List<string>();
        try { found.AddRange(FromRegistry()); } catch (Exception ex) { App.Log($"E3D registry probe failed: {ex.Message}"); }
        try { found.AddRange(FromDrives()); } catch (Exception ex) { App.Log($"E3D drive probe failed: {ex.Message}"); }

        return found
            .Select(SepPaths.Normalize)
            .Where(d => d.Length > 0 && File.Exists(Path.Combine(d, "evars.bat")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // 3.1 is what SEP's launch and plug-in logic is built for; otherwise the newest version wins
            .OrderByDescending(d => Path.GetFileName(d).Contains("3.1", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => ParseVersion(Path.GetFileName(d)))
            .FirstOrDefault();
    }

    private static Version ParseVersion(string name)
    {
        var m = Regex.Match(name, @"\d+(\.\d+)+|\d+");
        if (!m.Success) return new Version(0, 0);
        string v = m.Value.Contains('.') ? m.Value : m.Value + ".0";
        return Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);
    }

    private static IEnumerable<string> FromRegistry()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var hives = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        };
        foreach (var (hive, view) in hives)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(key);
            if (uninstall == null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                string? location = null;
                try
                {
                    using var app = uninstall.OpenSubKey(name);
                    string display = app?.GetValue("DisplayName") as string ?? string.Empty;
                    if (!display.Contains("Everything3D", StringComparison.OrdinalIgnoreCase) &&
                        !display.Contains("Everything 3D", StringComparison.OrdinalIgnoreCase)) continue;
                    location = app?.GetValue("InstallLocation") as string;
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(location)) yield return location;
            }
        }
    }

    private static IEnumerable<string> FromDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            string root = drive.RootDirectory.FullName;
            foreach (var parent in new[] { Path.Combine(root, "AVEVA"), Path.Combine(root, "AVEVA", "Design"),
                                           Path.Combine(root, "Program Files (x86)", "AVEVA"), Path.Combine(root, "Program Files", "AVEVA") })
            {
                IEnumerable<string> dirs;
                try { dirs = Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, "Everything3D*").ToList() : Enumerable.Empty<string>(); }
                catch { continue; }
                foreach (var d in dirs) yield return d;
            }
        }
    }
}
