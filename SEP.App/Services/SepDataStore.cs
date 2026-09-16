using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>
/// Reads and writes the two shared config files with the same location rules as the Python tool
/// (e3d_util.get_user_data_dir): %APPDATA%\SEP, or the executable's folder in portable mode
/// (a ".portable" file next to the exe, or SEP_PORTABLE=1). Writes are atomic (temp file + replace),
/// skipped when nothing changed, and keep one .bak of the previous content.
/// </summary>
public sealed class SepDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep Chinese readable, like ensure_ascii=False
    };

    public string DataDir { get; }
    public string ProjectsFile => Path.Combine(DataDir, "e3d_projects.json");
    public string PathsFile => Path.Combine(DataDir, "e3d_paths.json");

    private string _lastProjectsJson = string.Empty;
    private string _lastPathsJson = string.Empty;

    public SepDataStore(string? dataDir = null)
    {
        DataDir = dataDir ?? ResolveDataDir();
        try { Directory.CreateDirectory(DataDir); } catch { }
    }

    public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ResolveDataDir()
    {
        if (Environment.GetEnvironmentVariable("SEP_PORTABLE") == "1" || File.Exists(Path.Combine(ExeDir, ".portable")))
            return ExeDir;
        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrEmpty(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sep")
            : Path.Combine(appData, "SEP");
    }

    public SepData LoadData()
    {
        var data = Load<SepData>(ProjectsFile, out _lastProjectsJson);
        data.Version = Math.Max(data.Version, 3);
        data.Settings.Language = Localization.Loc.Normalize(data.Settings.Language);
        return data;
    }

    public E3dPathsConfig LoadPaths() => Load<E3dPathsConfig>(PathsFile, out _lastPathsJson);

    public void SaveData(SepData data) => Save(ProjectsFile, data, ref _lastProjectsJson);

    public void SavePaths(E3dPathsConfig paths) => Save(PathsFile, paths, ref _lastPathsJson);

    private static T Load<T>(string path, out string lastJson) where T : new()
    {
        T result = new();
        if (File.Exists(path))
        {
            try
            {
                result = JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions) ?? new T();
            }
            catch (Exception ex)
            {
                // Unreadable file: keep a copy so the next save cannot silently replace the user's data with defaults.
                App.Log($"Config {Path.GetFileName(path)} could not be read ({ex.Message}); backing it up before continuing with defaults");
                try { File.Copy(path, path + ".corrupt.bak", overwrite: true); } catch { }
            }
        }
        lastJson = JsonSerializer.Serialize(result, JsonOptions);
        return result;
    }

    private static void Save<T>(string path, T value, ref string lastJson)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        if (json == lastJson) return;

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }
        lastJson = json;
    }
}
