using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PyConnectKiosk;

public partial class App : Application
{
    static App()
    {
        // Catches anything thrown by static initializers / background threads
        // BEFORE WPF's normal exception path can see it. Writes to a crash log
        // next to the .exe so a silent exit is never truly silent.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("UnhandledException", args.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch UI-thread exceptions too
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);

        try
        {
            // Load runtime config from appsettings.json next to the .exe
            AppSettings.LoadFromDisk();

            var window = new AdminWindow();
            MainWindow = window;
            window.Show();
        }
        catch (System.Exception ex)
        {
            LogCrash("OnStartup", ex);
            // Show actual error so it does not silently close
            MessageBox.Show(
                "Startup error: " + ex.Message + "\n\n" + ex.StackTrace,
                "Admin Portal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "adminportal_crash.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: " +
                       $"{ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n" +
                       (ex?.InnerException != null
                           ? $"  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n"
                           : "") +
                       new string('-', 80) + "\n";
            File.AppendAllText(path, line);
        }
        catch { /* nothing more we can do */ }
    }
}