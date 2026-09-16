using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using E3dAdmin.Models;

namespace SEP.App.Services;

/// <summary>
/// Last known users and teams per project, so the Users page can show something instantly while ADMIN
/// (a 4–6 s adm.exe run) refreshes in the background. Stored in sep_user_cache.json with the layout the
/// Python tool uses ({"CODE": {"users": [...], "count": n, "updated_at": iso}}) plus a "teams" list.
/// </summary>
public sealed class AdminListingCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    public AdminListingCache(SepDataStore store)
    {
        _path = Path.Combine(store.DataDir, "sep_user_cache.json");
    }

    public sealed record Snapshot(List<UserInfo> Users, List<TeamInfo> Teams, DateTime? UpdatedAt);

    public Snapshot? Get(string projectCode)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(projectCode.ToUpperInvariant(), out var e) || e.Users == null) return null;
            var users = new List<UserInfo>();
            foreach (var u in e.Users)
                users.Add(new UserInfo { Name = u.Name ?? "", Security = u.Security ?? "General", Description = u.Description ?? "", Teams = u.Teams ?? new() });
            var teams = new List<TeamInfo>();
            foreach (var t in e.Teams ?? new())
                teams.Add(new TeamInfo { Name = t.Name ?? "", Description = t.Description ?? "", Users = t.Users ?? new() });
            return new Snapshot(users, teams, SepPaths.ParseIso(e.UpdatedAt));
        }
    }

    public void Set(string projectCode, List<UserInfo> users, List<TeamInfo> teams)
    {
        lock (_gate)
        {
            var entries = Load();
            entries[projectCode.ToUpperInvariant()] = new Entry
            {
                Users = users.ConvertAll(u => new UserEntry { Name = u.Name, Security = u.Security, Description = u.Description, Teams = u.Teams }),
                Teams = teams.ConvertAll(t => new TeamEntry { Name = t.Name, Description = t.Description, Users = t.Users }),
                Count = users.Count,
                UpdatedAt = SepPaths.NowIso(),
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                App.Log($"sep_user_cache.json could not be written: {ex.Message}");
            }
        }
    }

    public void Invalidate(string projectCode)
    {
        lock (_gate)
        {
            if (Load().Remove(projectCode.ToUpperInvariant()))
            {
                try { File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions), new UTF8Encoding(false)); } catch { }
            }
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
                if (raw != null) foreach (var kv in raw) result[kv.Key.ToUpperInvariant()] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            App.Log($"sep_user_cache.json could not be read: {ex.Message}");
        }
        _entries = result;
        return result;
    }

    private sealed class Entry
    {
        public List<UserEntry>? Users { get; set; }
        public List<TeamEntry>? Teams { get; set; }
        public int Count { get; set; }
        public string? UpdatedAt { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class UserEntry
    {
        public string? Name { get; set; }
        public string? Security { get; set; }
        public string? Description { get; set; }
        public List<string>? Teams { get; set; }
    }

    private sealed class TeamEntry
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Users { get; set; }
    }
}
