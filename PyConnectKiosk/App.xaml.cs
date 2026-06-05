using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PyConnectKiosk;

public partial class App : Application
{
    private WebAdminServer? _webAdmin;

    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("UnhandledException", args.ExceptionObject as Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);

        try
        {
            // Load runtime config (IPs, ports, PINs, thresholds) from
            // appsettings.json next to the .exe. Falls back to baked-in defaults
            // if the file is missing or malformed.
            AppSettings.LoadFromDisk();

            _webAdmin = new WebAdminServer(KioskSettings.WebAdminPort);
            try
            {
                _webAdmin.Start();
                System.Diagnostics.Debug.WriteLine(
                    $"[WebAdmin] Listening on http://+:{KioskSettings.WebAdminPort}/");
            }
            catch (Exception ex)
            {
                // Non-fatal — kiosk still works without web admin
                System.Diagnostics.Debug.WriteLine($"[WebAdmin] Failed to start: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            MessageBox.Show(
                "Kiosk startup error: " + ex.Message + "\n\n" + ex.StackTrace,
                "PyConnect Kiosk Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _webAdmin?.Stop();
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "kiosk_crash.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: " +
                       $"{ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n" +
                       (ex?.InnerException != null
                           ? $"  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n"
                           : "") +
                       new string('-', 80) + "\n";
            File.AppendAllText(path, line);
        }
        catch { }
    }
}
