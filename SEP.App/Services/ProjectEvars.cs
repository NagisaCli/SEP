using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SEP.App.Services;

/// <summary>
/// Reads a project's evarsXXX.bat into the environment AVEVA tools need to find that project (XXX000=…,
/// XXXDFLTS=… and friends), with %projects_dir% and %~dp0 resolved — the port of e3d_useradmin.parse_bat_evars.
/// </summary>
public static class ProjectEvars
{
    private static readonly Regex SetLine = new(@"^\s*set\s+([A-Za-z0-9_]+)\s*=\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeVar = new(@"^([A-Za-z0-9]{2,5})000$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Environment variables defined by the bat file, plus the AVEVA project code when a XXX000 variable is present.</summary>
    public static (string? Code, Dictionary<string, string> Variables) Parse(string batPath, string? libraryPath = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? code = null;
        if (string.IsNullOrWhiteSpace(batPath) || !File.Exists(batPath)) return (code, vars);

        string projectDir = Path.GetDirectoryName(batPath) ?? string.Empty;
        string baseDir = string.IsNullOrWhiteSpace(libraryPath) ? (Path.GetDirectoryName(projectDir) ?? projectDir) : libraryPath;
        string baseClean = baseDir.TrimEnd('\\', '/');
        string projClean = projectDir.TrimEnd('\\', '/');

        string text;
        try { text = SepPaths.ReadTextSmart(batPath).Text; }
        catch { return (code, vars); }

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::")) continue;
            var m = SetLine.Match(line);
            if (!m.Success) continue;

            string key = m.Groups[1].Value.Trim();
            string value = m.Groups[2].Value.Trim();
            value = ReplaceToken(value, "%projects_dir%", baseClean);
            value = ReplaceToken(value, "%~dp0", projClean);
            vars[key] = value;

            var c = CodeVar.Match(key);
            if (c.Success) code = c.Groups[1].Value.ToUpperInvariant();
        }
        return (code, vars);
    }

    /// <summary>Replaces %token%\ and %token% (case-insensitively) by the directory, keeping exactly one separator.</summary>
    private static string ReplaceToken(string value, string token, string dir)
    {
        foreach (string variant in new[] { token + "\\", token + "/", token })
        {
            int idx;
            while ((idx = value.IndexOf(variant, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                string repl = variant.EndsWith('\\') || variant.EndsWith('/') ? dir + "\\" : dir;
                value = value[..idx] + repl + value[(idx + variant.Length)..];
            }
        }
        return value;
    }
}
