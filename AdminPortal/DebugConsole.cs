using System.Diagnostics;
using System.Windows;

namespace PyConnectKiosk;

/// <summary>
/// Toggleable debug output (Admin Portal copy). Controlled by
/// <see cref="DebugSettings.ShowPopups"/> in appsettings.json.
/// </summary>
public static class DebugConsole
{
    public static void Show(string title, string message)
    {
        Debug.WriteLine($"[{title}] {message}");
        if (DebugSettings.ShowPopups)
        {
            try
            {
                MessageBox.Show(message, title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }
    }

    public static void Log(string title, string message)
        => Debug.WriteLine($"[{title}] {message}");
}
