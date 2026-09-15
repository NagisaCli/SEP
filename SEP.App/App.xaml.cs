using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Services;
using SEP.App.ViewModels;
using SEP.App.Views;
using SEP.App.Views.Pages;
using Wpf.Ui.Appearance;

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

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            string msg = $"[AppDomain Unhandled] {ex?.GetType().Name}: {ex?.Message}\r\n{ex?.StackTrace}\r\nInner: {ex?.InnerException?.Message}\r\n{ex?.InnerException?.StackTrace}";
            Log(msg);
            MessageBox.Show($"[SEP 启动致命错误]\n{ex?.Message}\n\n详细信息已写入 sep_debug.log", "SEP 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            string msg = $"[Dispatcher Unhandled] {e.Exception?.GetType().Name}: {e.Exception?.Message}\r\n{e.Exception?.StackTrace}\r\nInner: {e.Exception?.InnerException?.Message}\r\n{e.Exception?.InnerException?.StackTrace}";
            Log(msg);
            MessageBox.Show($"[SEP 界面异常]\n{e.Exception?.Message}\n\n详细信息已写入 sep_debug.log", "SEP 异常", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup enter");
        base.OnStartup(e);

        try
        {
            Log("Applying Dark Theme");
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            Log("Theme applied");
        }
        catch (Exception ex)
        {
            Log($"Apply theme failed: {ex.Message}");
        }

        try
        {
            Log("Configuring DI");
            var services = new ServiceCollection();

            services.AddSingleton<IE3dProjectService, E3dProjectService>();
            services.AddSingleton<IE3dLauncherService, E3dLauncherService>();
            services.AddSingleton<IE3dDiagService, E3dDiagService>();
            services.AddSingleton<IE3dAdminBridge, E3dAdminBridge>();

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<ProjectsViewModel>();
            services.AddTransient<UserAdminViewModel>();
            services.AddTransient<DiagnosticsViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<CreateProjectDialogViewModel>();

            services.AddTransient<ProjectsPage>();
            services.AddTransient<UserAdminPage>();
            services.AddTransient<DiagnosticsPage>();
            services.AddTransient<SettingsPage>();

            services.AddSingleton<MainWindow>();

            Services = services.BuildServiceProvider();
            Log("DI built successfully");

            Log("Resolving MainWindow");
            var mainWindow = Services.GetRequiredService<MainWindow>();
            Log("MainWindow resolved, calling Show()");
            mainWindow.Show();
            Log("MainWindow.Show() called successfully");
        }
        catch (Exception ex)
        {
            Log($"OnStartup Exception: {ex.GetType().Name} - {ex.Message}\r\n{ex.StackTrace}\r\nInner: {ex.InnerException?.Message}\r\n{ex.InnerException?.StackTrace}");
            MessageBox.Show($"启动失败: {ex.Message}\n{ex.InnerException?.Message}", "SEP 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
