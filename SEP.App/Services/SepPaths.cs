using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SEP.App.Services;

/// <summary>Path normalisation, ids and batch-file helpers shared with the Python tool's conventions (e3d_util.py).</summary>
public static class SepPaths
{
    /// <summary>
    /// Mirrors e3d_util.normalize_path: trims quotes/whitespace, smb://host/share → \\host\share,
    /// file:// → local or UNC, expands %VARS%, forward slashes → backslashes, no trailing backslash
    /// (except drive roots). http(s) URLs are returned untouched.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return string.Empty;
        string low = p.ToLowerInvariant();

        if (low.StartsWith("http://") || low.StartsWith("https://")) return p;

        if (low.StartsWith("smb://"))
        {
            string rest = p[6..].Replace('/', '\\');
            return StripTrailing(@"\\" + rest);
        }
        if (low.StartsWith("file://"))
        {
            string rest = p[7..];
            if (rest.StartsWith('/')) rest = rest[1..];
            rest = rest.Replace('/', '\\');
            return Regex.IsMatch(rest, @"^[A-Za-z]:\\") ? StripTrailing(rest) : @"\\" + StripTrailing(rest);
        }

        p = Environment.ExpandEnvironmentVariables(p);
        if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        p = p.Replace('/', '\\');
        return StripTrailing(p);
    }

    private static string StripTrailing(string p)
    {
        p = p.Trim();
        if (p.Length == 0) return string.Empty;
        if (Regex.IsMatch(p, @"^[A-Za-z]:\\?$")) return p.TrimEnd('\\') + "\\";   // drive root keeps its slash
        if (p.Trim('\\').Length == 0) return p.Length >= 2 ? @"\\" : p;
        while (p.Length > 2 && p.EndsWith('\\')) p = p[..^1];
        return p;
    }

    public static bool IsUrl(string p) => p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public static bool IsUnc(string p) => p.StartsWith(@"\\");
    public static string Protocol(string p) => IsUrl(p) ? "url" : IsUnc(p) ? "unc" : "local";

    /// <summary>Host name of a UNC path (\\host\share\...).</summary>
    public static string? UncHost(string p)
    {
        if (!IsUnc(p)) return null;
        string rest = p.TrimStart('\\');
        int i = rest.IndexOf('\\');
        return i > 0 ? rest[..i] : rest;
    }

    /// <summary>Mirrors e3d_util.gen_id: prefix + "_" + sha1(lower-cased key)[:12].</summary>
    public static string GenerateId(string prefix, params string[] parts)
    {
        string key = string.Join("|", Array.ConvertAll(parts, s => (s ?? string.Empty).ToLowerInvariant()));
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return prefix + "_" + Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }

    /// <summary>ISO timestamp without fractions, like e3d_util.now_iso().</summary>
    public static string NowIso() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    public static DateTime? ParseIso(string? s) => DateTime.TryParse(s, out var d) ? d : null;

    // ── batch files: UTF-8 (with/without BOM) or GBK, which is what AVEVA writes ──

    public static Encoding Gbk => Encoding.GetEncoding("GBK");

    public static Encoding DetectEncoding(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8;
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Gbk;
        }
    }

    public static (string Text, Encoding Encoding) ReadTextSmart(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var enc = DetectEncoding(data);
        return (enc.GetString(data), enc);
    }

    private static readonly Regex CodeRe = new(@"(?im)^\s*set\s+""?([A-Za-z][A-Za-z0-9]{1,4})000\s*=", RegexOptions.Compiled);

    /// <summary>The AVEVA project code from an evars file: the XXX of its "set XXX000=" line, upper-cased; null if absent.</summary>
    public static string? ReadProjectCode(string batPath)
    {
        try
        {
            using var fs = new FileStream(batPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(fs.Length, 64 * 1024)];
            int n = fs.Read(buf, 0, buf.Length);
            string text = DetectEncoding(buf).GetString(buf, 0, n);
            var m = CodeRe.Match(text);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }
        catch
        {
            return null;
        }
    }
}
