using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PyConnectKiosk;

// ─── PATH RESOLUTION ──────────────────────────────────────────────────────────
internal static class AppRoot
{
    public static readonly string Path = AppContext.BaseDirectory;
}

// ─── FACE RECOGNITION ────────────────────────────────────────────────────────
public static class FaceSettings
{
    public static string EscortFacesDir => Path.Combine(AppRoot.Path, "faces", "escorts");
    public static string UserFacesDir   => Path.Combine(AppRoot.Path, "faces", "users");

    public static string EscortFacePath(string username) =>
        Path.Combine(EscortFacesDir, $"{username}.jpg");
    public static string UserFacePath(string username) =>
        Path.Combine(UserFacesDir, $"{username}.jpg");
}

// ─── DATA FILES ──────────────────────────────────────────────────────────────
public static class DataFiles
{
    public static string UsersJson       => Path.Combine(AppRoot.Path, "data", "users.json");
    public static string RacksJson       => Path.Combine(AppRoot.Path, "data", "racks.json");
    public static string SlotsJson       => Path.Combine(AppRoot.Path, "data", "slots.json");
    public static string ActivityLogJson => Path.Combine(AppRoot.Path, "data", "activity_log.json");
}

// ─── MQTT / TZ DM LOCKER ─────────────────────────────────────────────────────
public static class LockerSettings
{
    public static string BrokerHost        { get; internal set; } = "192.168.1.2";
    public static int    BrokerPort        { get; internal set; } = 1883;
    public static string TzDmInstanceId    { get; internal set; } = "nxtgendmsitsyd";
    public static int    CommandTimeoutSec { get; internal set; } = 5;
}

// ─── ADMIN PORTAL UI ─────────────────────────────────────────────────────────
public static class AdminSettings
{
    public static string AdminPin     { get; internal set; } = "1234";
    public static int    WebAdminPort { get; internal set; } = 8181;
}

// ─── DEBUG / DIAGNOSTIC ──────────────────────────────────────────────────────
public static class DebugSettings
{
    public static bool ShowPopups { get; internal set; } = false;
}

// ─── RACK CATALOGUE ──────────────────────────────────────────────────────────
public static class RackCatalogue
{
    public static readonly List<RackItem> Defaults = new List<RackItem>
    {
        new RackItem("A01", "13071023", "Rack A01 - ISS Rack",  "Row A"),
        new RackItem("A02", "13070870", "Rack A02 - Test Rack", "Row B"),
    };

    public static List<RackItem>  All              => RackService.GetAll();
    public static RackItem?       ByTag(string tag) => RackService.ByTag(tag);
}

// ─── RUNTIME CONFIG LOADER ───────────────────────────────────────────────────
// Reads appsettings.json next to the .exe (shared with kiosk). Missing file or
// missing keys = keep the defaults — never crashes.
public static class AppSettings
{
    public static string ConfigPath => Path.Combine(AppRoot.Path, "appsettings.json");

    public static void LoadFromDisk()
    {
        if (!File.Exists(ConfigPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("Locker", out var lk))
            {
                if (lk.TryGetProperty("BrokerHost",        out var v)) LockerSettings.BrokerHost        = v.GetString() ?? LockerSettings.BrokerHost;
                if (lk.TryGetProperty("BrokerPort",        out v))     LockerSettings.BrokerPort        = v.GetInt32();
                if (lk.TryGetProperty("TzDmInstanceId",    out v))     LockerSettings.TzDmInstanceId    = v.GetString() ?? LockerSettings.TzDmInstanceId;
                if (lk.TryGetProperty("CommandTimeoutSec", out v))     LockerSettings.CommandTimeoutSec = v.GetInt32();
            }

            if (root.TryGetProperty("Kiosk", out var ki))
            {
                if (ki.TryGetProperty("AdminPin",     out var v)) AdminSettings.AdminPin     = v.GetString() ?? AdminSettings.AdminPin;
                if (ki.TryGetProperty("WebAdminPort", out v))     AdminSettings.WebAdminPort = v.GetInt32();
            }

            if (root.TryGetProperty("Debug", out var dbg))
            {
                if (dbg.TryGetProperty("ShowPopups", out var v)) DebugSettings.ShowPopups = v.GetBoolean();
            }

            System.Diagnostics.Debug.WriteLine($"[AppSettings] Loaded config from {ConfigPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] Failed to load {ConfigPath}: {ex.Message}");
        }
    }
}
