using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using SEP.App.Resources;

namespace SEP.App.Services;

/// <summary>
/// Per-user, no-admin setup of the single-file SEP.exe:
///   SEP.exe --install [--quiet]    copy to %LOCALAPPDATA%\Programs\SEP, Start menu + desktop shortcuts,
///                                  an "Apps &amp; features" entry, then start the installed copy
///   SEP.exe --uninstall [--quiet]  remove all of that again; the data in %APPDATA%\SEP is kept
/// Running --install again over an existing installation updates it in place (the running SEP is asked to close).
/// </summary>
public static class SepInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SEP";
    private const string ExeName = "SEP.exe";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SEP");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExeName);

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Only the published single-file exe can be copied on its own; a dev build needs SEP.dll and friends.</summary>
    public static bool IsSingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "SEP.dll"));

    public static bool IsRunningInstalledCopy =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe that external launchers (E3D ribbon, shortcuts) should point at: the installed copy when there is one.</summary>
    public static string StableExePath => IsInstalled ? InstalledExe : CurrentExe;

    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SEP.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SEP.lnk");

    /// <summary>Handles --install / --uninstall. Returns false when neither switch is present.</summary>
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        bool install = args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        if (!install && !uninstall) return false;
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (install)
            {
                var (ok, message) = Install(launchAfter: !quiet);
                exitCode = ok ? 0 : 1;
                if (!quiet || !ok) Show(message, ok);
            }
            else
            {
                if (!quiet && MessageBox.Show(Strings.Setup_UninstallConfirm, Strings.Setup_Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return true;
                var (ok, message) = Uninstall();
                exitCode = ok ? 0 : 1;
                if (!quiet) Show(message, ok);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Setup failed: {ex}");
            exitCode = 1;
            if (!quiet) Show(string.Format(Strings.Setup_Failed, ex.Message), false);
        }
        return true;
    }

    private static void Show(string message, bool ok) =>
        MessageBox.Show(message, Strings.Setup_Title, MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

    /// <summary>Copies this exe into the per-user program folder and registers it. Safe to repeat (update).</summary>
    public static (bool Ok, string Message) Install(bool launchAfter)
    {
        Directory.CreateDirectory(InstallDir);
        string source = CurrentExe;

        if (!IsRunningInstalledCopy && !IsSingleFile)
            return (false, Strings.Setup_NotSingleFile);

        if (!IsRunningInstalledCopy)
        {
            // An update: the running SEP holds its exe open, so ask it to close first.
            CloseRunningInstalledCopies();
            string tmp = InstalledExe + ".new";
            File.Copy(source, tmp, overwrite: true);
            if (!Retry(() => File.Move(tmp, InstalledExe, overwrite: true)))
            {
                try { File.Delete(tmp); } catch { }
                return (false, Strings.Setup_InUse);
            }
            // a pdb next to a dev build is useful for crash stacks; copy it along when present
            string pdb = Path.ChangeExtension(source, ".pdb");
            if (File.Exists(pdb)) try { File.Copy(pdb, Path.Combine(InstallDir, "SEP.pdb"), overwrite: true); } catch { }
        }
        Unblock(InstalledExe);

        CreateShortcut(StartMenuLink, InstalledExe);
        CreateShortcut(DesktopLink, InstalledExe);
        Register();
        RetargetAutoStart(InstalledExe);
        App.Log($"Installed SEP {CurrentVersion} to {InstallDir}");

        if (launchAfter && !IsRunningInstalledCopy)
        {
            try { Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir }); }
            catch (Exception ex) { App.Log($"Start after install failed: {ex.Message}"); }
            return (true, string.Format(Strings.Setup_InstalledStarting, CurrentVersion, InstallDir));
        }
        return (true, string.Format(Strings.Setup_Installed, CurrentVersion, InstallDir));
    }

    public static (bool Ok, string Message) Uninstall()
    {
        foreach (var link in new[] { StartMenuLink, DesktopLink })
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        RetargetAutoStart(null);
        CloseRunningInstalledCopies();

        if (IsRunningInstalledCopy)
        {
            // Windows cannot delete a running exe: remove the folder once this process has exited.
            string script = $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        else
        {
            if (Directory.Exists(InstallDir) && !Retry(() => Directory.Delete(InstallDir, recursive: true)))
                return (false, Strings.Setup_InUse);
        }
        App.Log("Uninstalled SEP");
        return (true, Strings.Setup_Uninstalled);
    }

    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "SEP - AVEVA E3D Project Manager");
        key.SetValue("DisplayVersion", CurrentVersion);
        key.SetValue("Publisher", "SEP");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", InstalledExe + ",0");
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { }
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    /// <summary>"Start with Windows" (Settings) keeps working after install/uninstall: it follows the exe, or is removed.</summary>
    private static void RetargetAutoStart(string? exe)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("SEP") == null) return;
            if (exe == null) run.DeleteValue("SEP", throwOnMissingValue: false);
            else run.SetValue("SEP", $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            App.Log($"Autostart update failed: {ex.Message}");
        }
    }

    /// <summary>Windows Script Host shortcut; late-bound so no COM interop assembly is needed.</summary>
    private static void CreateShortcut(string linkPath, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "SEP - AVEVA E3D Project Manager";
            link.Save();
        }
        catch (Exception ex)
        {
            App.Log($"Shortcut {linkPath} failed: {ex.Message}");
        }
    }

    /// <summary>Drops the "downloaded from the internet" mark so SmartScreen does not block every start.</summary>
    private static void Unblock(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { }
    }

    /// <summary>Other SEP processes started from the install folder (not this one) are asked to close, then ended.</summary>
    private static void CloseRunningInstalledCopies()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("SEP"))
        {
            using (p)
            {
                try
                {
                    if (p.Id == me) continue;
                    string? path = p.MainModule?.FileName;
                    if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.CloseMainWindow() && p.WaitForExit(8000)) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    App.Log($"Could not close running SEP ({p.Id}): {ex.Message}");
                }
            }
        }
    }

    private static bool Retry(Action action)
    {
        for (int i = 0; i < 10; i++)
        {
            try { action(); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        return false;
    }
}
