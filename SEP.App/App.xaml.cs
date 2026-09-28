using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Localization;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;
using SEP.App.ViewModels;
using SEP.App.Views;
using SEP.App.Views.Pages;

namespace SEP.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Started by an installer (--apply-plugins): errors end the process with a result file, never a dialog.</summary>
    public static bool Headless { get; } = PluginCli.IsHeadless(Environment.GetCommandLineArgs());
    /// <summary>
    /// Next to the exe in portable mode (UI checks read it there), otherwise %LOCALAPPDATA%\SEP: an installed exe may
    /// live in a read-only or shared folder, and a log beside it would be lost or mixed between users.
    /// </summary>
    public static string LogPath { get; } = ResolveLogPath();
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object LogGate = new();

    private static string ResolveLogPath()
    {
        if (SepDataStore.IsPortable) return Path.Combine(SepDataStore.ExeDir, "sep_debug.log");
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SEP");
        try { Directory.CreateDirectory(dir); } catch { return Path.Combine(Path.GetTempPath(), "sep_debug.log"); }
        return Path.Combine(dir, "sep_debug.log");
    }

    public static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxLogBytes)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);   // keep one previous log, never grow without bound
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
            catch { }
        }
    }

    private SingleInstance? _instance;

    public App()
    {
        Log($"App Constructor enter (SEP {SepInstaller.CurrentVersion}, {Environment.OSVersion.VersionString}, {(Environment.Is64BitProcess ? "x64" : "x86")})");

        // Background work is fire-and-forget in several places; a fault there must end up in the log, not vanish.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"[Unobserved task] {e.Exception.GetBaseException().GetType().Name}: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };

        // evars*.bat / custom_evars.bat / projects.ini are read and written as GBK; .NET only ships the
        // Unicode encodings unless the code-page provider is registered, so without this line every
        // Encoding.GetEncoding("GBK") call throws and project switching / creation fails.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            string msg = $"[AppDomain Unhandled] {ex?.GetType().Name}: {ex?.Message}\r\n{ex?.StackTrace}\r\nInner: {ex?.InnerException?.Message}\r\n{ex?.InnerException?.StackTrace}";
            Log(msg);
            if (Headless) { PluginCli.WriteFailure(Environment.GetCommandLineArgs(), $"SEP failed: {ex?.Message}"); Environment.Exit(3); }
            MessageBox.Show(string.Format(Strings.App_FatalBody, ex?.Message), Strings.App_FatalTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            string msg = $"[Dispatcher Unhandled] {e.Exception?.GetType().Name}: {e.Exception?.Message}\r\n{e.Exception?.StackTrace}\r\nInner: {e.Exception?.InnerException?.Message}\r\n{e.Exception?.InnerException?.StackTrace}";
            Log(msg);
            if (Headless) { PluginCli.WriteFailure(Environment.GetCommandLineArgs(), $"SEP failed: {e.Exception?.Message}"); Environment.Exit(3); }
            e.Handled = true;
            // A fault that repeats on every layout pass must not bury the user under identical dialogs.
            string key = $"{e.Exception?.GetType().Name}:{e.Exception?.Message}";
            if (_errorDialogOpen || (key == _lastUiError && DateTime.UtcNow - _lastUiErrorAt < TimeSpan.FromSeconds(30))) return;
            _lastUiError = key;
            _lastUiErrorAt = DateTime.UtcNow;
            _errorDialogOpen = true;
            try { MessageBox.Show(string.Format(Strings.App_UiErrorBody, e.Exception?.Message), Strings.App_UiErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _errorDialogOpen = false; }
        };
    }

    private bool _errorDialogOpen;
    private string? _lastUiError;
    private DateTime _lastUiErrorAt;

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        // stop the heartbeat and drop this device's session files so nobody sees a ghost session
        try { (Services as IDisposable)?.Dispose(); } catch (Exception ex) { Log($"OnExit: {ex.Message}"); }
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup enter");
        base.OnStartup(e);

        // --install / --uninstall: per-user setup of this exe (Start menu, desktop, Apps & features); no main window.
        if (SepInstaller.TryHandle(e.Args, out int setupExit))
        {
            Shutdown(setupExit);
            return;
        }

        // One window per data folder: two copies would overwrite each other's e3d_projects.json and custom_evars.bat.
        // A second start just brings the running window to the front.
        if (!Headless)
        {
            bool handOver = e.Args.Any(a => a.Equals("--wait-instance", StringComparison.OrdinalIgnoreCase));
            _instance = SingleInstance.TryAcquire(SepDataStore.ResolveDataDir(), () => Dispatcher.BeginInvoke(BringToFront), handOver);
            if (_instance == null)
            {
                Log("Another SEP window is already running for this data folder; activated it and exiting");
                Shutdown(0);
                return;
            }
        }

        try
        {
            Log("Configuring DI");
            var services = new ServiceCollection();

            services.AddSingleton<SepDataStore>();
            services.AddSingleton<AdminCredentialStore>();
            services.AddSingleton<AdminListingCache>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IProjectCatalog, ProjectCatalog>();
            services.AddSingleton<IE3dProjectService, E3dProjectService>();
            services.AddSingleton<IE3dLauncherService, E3dLauncherService>();
            services.AddSingleton<IE3dDiagService, E3dDiagService>();
            services.AddSingleton<IE3dAdminBridge, E3dAdminBridge>();
            services.AddSingleton<SessionService>();
            services.AddSingleton<ProjectUsageService>();
            services.AddSingleton<E3dToolsService>();
            services.AddSingleton<PluginService>();
            services.AddSingleton<PluginDiscoveryService>();
            services.AddSingleton<ToastService>();
            services.AddSingleton<ProjectActions>();
            services.AddSingleton<LiveEntryAdapter>();

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<OverviewViewModel>();
            services.AddSingleton<ProjectsViewModel>();
            services.AddSingleton<MyProjectsViewModel>();
            services.AddSingleton<PluginsViewModel>();
            services.AddSingleton<ToolsViewModel>();
            services.AddSingleton<LiveViewModel>();
            services.AddSingleton<UserAdminViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<CreateProjectDialogViewModel>();

            services.AddSingleton<OverviewPage>();
            services.AddSingleton<ProjectsPage>();
            services.AddSingleton<MyProjectsPage>();
            services.AddSingleton<PluginsPage>();
            services.AddSingleton<UserAdminPage>();
            services.AddSingleton<ToolsPage>();
            services.AddSingleton<LivePage>();
            services.AddSingleton<SettingsPage>();

            services.AddSingleton<MainWindow>();

            Services = services.BuildServiceProvider();
            Log("DI built successfully");

            // UI language and theme must be in effect before any view-model or window is created.
            var catalog = Services.GetRequiredService<IProjectCatalog>();
            string language = catalog.Data.Settings.Language;
            Loc.Instance.Apply(language);
            ThemeService.LogBackdrop();
            Services.GetRequiredService<ThemeService>().Apply(catalog.Data.Settings.Theme);
            Log($"UI language: setting={language} culture={Loc.Instance.Culture.Name}; data dir={Services.GetRequiredService<SepDataStore>().DataDir}; libraries={catalog.Libraries.Count} projects={catalog.Projects.Count}");

            // A new PC has no e3d_paths.json yet: find the E3D installation so launching works without a trip to Settings.
            if (E3dLocator.FillMissing(catalog.Paths, catalog.Data.Settings))
            {
                Log($"E3D auto-detected: install={catalog.Paths.InstallDir} evars={catalog.Paths.EvarsBat} projects={catalog.Paths.ProjectsDir}");
                catalog.Save();
            }

            // --apply-plugins=<request.json>: headless plug-in setup for installers; no window (see PluginCli)
            string? applyRequest = PluginCli.RequestPath(e.Args);
            if (applyRequest != null)
            {
                Shutdown(PluginCli.Run(Services, applyRequest));
                return;
            }

            // Auto-detect & safely adopt legacy hand-written plugin and project configs in custom_evars.bat
            try
            {
                var plugins = Services.GetRequiredService<PluginService>();
                var report = plugins.AdoptLegacyConfigs();
                if (report.Changed)
                {
                    catalog.Data.Notifications.Insert(0, new NotificationRecord
                    {
                        Id = SepPaths.GenerateId("notif", DateTime.UtcNow.Ticks.ToString()),
                        Level = "info",
                        Title = Strings.Plugins_AdoptedTitle,
                        Message = report.Message,
                        CreatedAt = SepPaths.NowIso(),
                    });
                    catalog.Save();
                    Log($"[AutoAdopt] {report.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"[AutoAdopt Error] {ex.Message}");
            }

            Log("Resolving MainWindow");
            var mainWindow = Services.GetRequiredService<MainWindow>();
            Log("MainWindow resolved, calling Show()");
            mainWindow.Show();
            Log("MainWindow.Show() called successfully");

            // --shot-dir=<folder>: UI-check screenshots rendered from the visual tree (see Diagnostics/ScreenshotHook)
            string? shotDir = e.Args.FirstOrDefault(a => a.StartsWith("--shot-dir=", StringComparison.OrdinalIgnoreCase))?["--shot-dir=".Length..].Trim('"');
            if (!string.IsNullOrEmpty(shotDir)) Diagnostics.ScreenshotHook.Start(shotDir);
        }
        catch (Exception ex)
        {
            Log($"OnStartup Exception: {ex.GetType().Name} - {ex.Message}\r\n{ex.StackTrace}\r\nInner: {ex.InnerException?.Message}\r\n{ex.InnerException?.StackTrace}");
            if (Headless)
            {
                PluginCli.WriteFailure(e.Args, $"SEP could not start: {ex.Message}");
                Shutdown(3);
                return;
            }
            MessageBox.Show(string.Format(Strings.App_StartupFailed, ex.Message, ex.InnerException?.Message), Strings.App_FatalTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Restores and activates the main window (asked for by a second start of SEP).</summary>
    private void BringToFront()
    {
        var window = MainWindow;
        if (window == null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;    // Windows only lets a background process take focus this way
        window.Topmost = false;
        window.Focus();
    }
}
