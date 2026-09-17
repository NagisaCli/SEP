using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Localization;
using SEP.App.Resources;
using SEP.App.Services;
using SEP.App.ViewModels;
using SEP.App.Views;
using SEP.App.Views.Pages;

namespace SEP.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sep_debug.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    public App()
    {
        Log("App Constructor enter");

        // evars*.bat / custom_evars.bat / projects.ini are read and written as GBK; .NET only ships the
        // Unicode encodings unless the code-page provider is registered, so without this line every
        // Encoding.GetEncoding("GBK") call throws and project switching / creation fails.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            string msg = $"[AppDomain Unhandled] {ex?.GetType().Name}: {ex?.Message}\r\n{ex?.StackTrace}\r\nInner: {ex?.InnerException?.Message}\r\n{ex?.InnerException?.StackTrace}";
            Log(msg);
            MessageBox.Show(string.Format(Strings.App_FatalBody, ex?.Message), Strings.App_FatalTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            string msg = $"[Dispatcher Unhandled] {e.Exception?.GetType().Name}: {e.Exception?.Message}\r\n{e.Exception?.StackTrace}\r\nInner: {e.Exception?.InnerException?.Message}\r\n{e.Exception?.InnerException?.StackTrace}";
            Log(msg);
            MessageBox.Show(string.Format(Strings.App_UiErrorBody, e.Exception?.Message), Strings.App_UiErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // stop the heartbeat and drop this device's session files so nobody sees a ghost session
        try { (Services as IDisposable)?.Dispose(); } catch (Exception ex) { Log($"OnExit: {ex.Message}"); }
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup enter");
        base.OnStartup(e);

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
            services.AddSingleton<E3dToolsService>();
            services.AddSingleton<PluginService>();
            services.AddSingleton<ToastService>();
            services.AddSingleton<ProjectActions>();

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<OverviewViewModel>();
            services.AddSingleton<ProjectsViewModel>();
            services.AddSingleton<MyProjectsViewModel>();
            services.AddSingleton<PluginsViewModel>();
            services.AddSingleton<ToolsViewModel>();
            services.AddTransient<UserAdminViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<CreateProjectDialogViewModel>();

            services.AddTransient<OverviewPage>();
            services.AddTransient<ProjectsPage>();
            services.AddTransient<MyProjectsPage>();
            services.AddTransient<PluginsPage>();
            services.AddTransient<UserAdminPage>();
            services.AddTransient<ToolsPage>();
            services.AddTransient<SettingsPage>();

            services.AddSingleton<MainWindow>();

            Services = services.BuildServiceProvider();
            Log("DI built successfully");

            // UI language and theme must be in effect before any view-model or window is created.
            var catalog = Services.GetRequiredService<IProjectCatalog>();
            string language = catalog.Data.Settings.Language;
            Loc.Instance.Apply(language);
            Services.GetRequiredService<ThemeService>().Apply(catalog.Data.Settings.Theme);
            Log($"UI language: setting={language} culture={Loc.Instance.Culture.Name}; data dir={Services.GetRequiredService<SepDataStore>().DataDir}; libraries={catalog.Libraries.Count} projects={catalog.Projects.Count}");

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
            MessageBox.Show(string.Format(Strings.App_StartupFailed, ex.Message, ex.InnerException?.Message), Strings.App_FatalTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
