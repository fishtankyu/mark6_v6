using System.Diagnostics;
using System.Windows;

namespace PyConnectKiosk;

/// <summary>
/// Toggleable debug output. Behaviour controlled by
/// <see cref="DebugSettings.ShowPopups"/> in appsettings.json.
///
/// • Always: writes to the Debug Output stream (visible in Visual Studio's
///   "Output → Debug" window, or via SysInternals DebugView in production).
/// • When ShowPopups = true: also shows a MessageBox popup.
///
/// Usage:
///     DebugConsole.Show("Locker", $"MQTT connected to {host}");
///     DebugConsole.Show("PyConnect", $"XML being sent:\n{xml}");
/// </summary>
public static class DebugConsole
{
    public static void Show(string title, string message)
    {
        // Always written — costs almost nothing, useful for post-mortem
        Debug.WriteLine($"[{title}] {message}");

        // Optional popup — gated by config
        if (DebugSettings.ShowPopups)
        {
            try
            {
                MessageBox.Show(message, title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
                // Swallow — never let a debug call crash the app
            }
        }
    }

    /// <summary>
    /// Pure log-only — never shows a popup. Use this when you want a trace
    /// that's visible only via Debug Output regardless of the popup flag.
    /// </summary>
    public static void Log(string title, string message)
        => Debug.WriteLine($"[{title}] {message}");
}
