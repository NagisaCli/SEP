using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEP.App.Services;

/// <summary>Administrator (FREE user) login for one AVEVA project.</summary>
public sealed record AdminCredential(string User, string Password)
{
    public static readonly AdminCredential Default = new("SYSTEM", "XXXXXX");
}

/// <summary>
/// Per-project administrator credentials in sep_admin_creds.json next to the other SEP data files — the file the
/// Python tool (e3d_useradmin.py) keeps as {"CODE": {"admin_user": …, "admin_password": …}}. Entries written by
/// this client hold the password DPAPI-protected for the current Windows user ("admin_password_dpapi"); plain-text
/// entries from the Python tool are still read.
/// </summary>
public sealed class AdminCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SEP.AdminCredentials.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    public AdminCredentialStore(SepDataStore store)
    {
        _path = Path.Combine(store.DataDir, "sep_admin_creds.json");
    }

    public string FilePath => _path;

    /// <summary>Saved credential for the project code, or null when none is stored.</summary>
    public AdminCredential? Get(string projectCode)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) return null;
        lock (_gate)
        {
            var entries = Load();
            if (!entries.TryGetValue(projectCode.ToUpperInvariant(), out var e)) return null;
            string? password = e.Password;
            if (!string.IsNullOrEmpty(e.PasswordDpapi))
            {
                password = Unprotect(e.PasswordDpapi);
                if (password == null) return null;   // saved on another Windows account/machine
            }
            if (string.IsNullOrWhiteSpace(e.User) || password == null) return null;
            return new AdminCredential(e.User, password);
        }
    }

    public bool Has(string projectCode) => Get(projectCode) != null;

    /// <summary>Credential to use for the project: the saved one, else AVEVA's SYSTEM/XXXXXX default.</summary>
    public AdminCredential Resolve(string projectCode) => Get(projectCode) ?? AdminCredential.Default;

    public void Set(string projectCode, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) throw new ArgumentException("project code required", nameof(projectCode));
        lock (_gate)
        {
            var entries = Load();
            entries[projectCode.ToUpperInvariant()] = new Entry
            {
                User = user.Trim(),
                PasswordDpapi = Protect(password),
                Password = null,
                UpdatedAt = SepPaths.NowIso(),
            };
            Save(entries);
        }
    }

    public void Remove(string projectCode)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) return;
        lock (_gate)
        {
            var entries = Load();
            if (entries.Remove(projectCode.ToUpperInvariant())) Save(entries);
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_entries != null) return _entries;
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path, Encoding.UTF8), JsonOptions);
                if (raw != null)
                    foreach (var kv in raw) result[kv.Key.ToUpperInvariant()] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            App.Log($"sep_admin_creds.json could not be read: {ex.Message}");
        }
        _entries = result;
        return result;
    }

    private void Save(Dictionary<string, Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log($"sep_admin_creds.json could not be written: {ex.Message}");
            throw;
        }
    }

    private static string Protect(string plain)
    {
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string? Unprotect(string base64)
    {
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }

    private sealed class Entry
    {
        [JsonPropertyName("admin_user")] public string? User { get; set; }
        [JsonPropertyName("admin_password"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Password { get; set; }
        [JsonPropertyName("admin_password_dpapi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PasswordDpapi { get; set; }
        [JsonPropertyName("updated_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UpdatedAt { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
