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

// ─── PYCONNECT ────────────────────────────────────────────────────────────────
public static class PyConnectSettings
{
    public static string Host             { get; internal set; } = "192.168.1.4";
    public static int    Port             { get; internal set; } = 13131;
    public static string ChecksumPassword { get; internal set; } = "admin123";
    public static int    TimeoutMs        { get; internal set; } = 15000; // Increased default to 15s

    public static readonly List<string> PyKeyPool = new List<string>
    {
        "13071023",
        "13070870"
    };
}

// ─── FACE RECOGNITION ────────────────────────────────────────────────────────
public static class FaceSettings
{
    public static string EscortFacesDir  => Path.Combine(AppRoot.Path, "faces", "escorts");
    public static string UserFacesDir    => Path.Combine(AppRoot.Path, "faces", "users");

    public static string EscortFacePath(string username) =>
        Path.Combine(EscortFacesDir, $"{username}.jpg");
    public static string UserFacePath(string username) =>
        Path.Combine(UserFacesDir, $"{username}.jpg");

    // ---> ADDED THE MISSING HAAR CASCADE PATH <---
    public static string HaarCascadePath => Path.Combine(AppRoot.Path, "haarcascade_frontalface_default.xml");

    public static double Threshold       { get; internal set; } = 100.0; 
    public static int ScanTimeoutSec     { get; internal set; } = 30;
    public static int ConfirmFrames      { get; internal set; } = 3;
    public static int CameraIndex        { get; internal set; } = 0;
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

// ─── KIOSK SETTINGS ──────────────────────────────────────────────────────────
public static class KioskSettings
{
    public static int    CodeDisplaySec { get; internal set; } = 30;
    public static int    ErrorResetSec  { get; internal set; } = 5;
    public static string AdminPin       { get; internal set; } = "1234";
    public static string WebAdminPin    { get; internal set; } = "1234";
    public static int    WebAdminPort   { get; internal set; } = 8181;
}

// ─── PRINTER SETTINGS ────────────────────────────────────────────────────────
public static class PrinterSettings
{
    public static string PrinterName    { get; internal set; } = "Microsoft Print to PDF";
    public static int    PaperWidthMm   { get; internal set; } = 80;
    public static string DataCentreName { get; internal set; } = "Data Centre";
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

            if (root.TryGetProperty("PyConnect", out var py))
            {
                if (py.TryGetProperty("Host", out var v))             PyConnectSettings.Host = v.GetString() ?? PyConnectSettings.Host;
                if (py.TryGetProperty("Port", out v))                 PyConnectSettings.Port = v.GetInt32();
                if (py.TryGetProperty("ChecksumPassword", out v))     PyConnectSettings.ChecksumPassword = v.GetString() ?? PyConnectSettings.ChecksumPassword;
                if (py.TryGetProperty("TimeoutMs", out v))            PyConnectSettings.TimeoutMs = v.GetInt32();
            }

            if (root.TryGetProperty("Face", out var face))
            {
                if (face.TryGetProperty("Threshold", out var v))      FaceSettings.Threshold = v.GetDouble();
                if (face.TryGetProperty("ScanTimeoutSec", out v))     FaceSettings.ScanTimeoutSec = v.GetInt32();
                if (face.TryGetProperty("ConfirmFrames", out v))      FaceSettings.ConfirmFrames = v.GetInt32();
                if (face.TryGetProperty("CameraIndex", out v))        FaceSettings.CameraIndex = v.GetInt32();
            }

            if (root.TryGetProperty("Locker", out var lk))
            {
                if (lk.TryGetProperty("BrokerHost",        out var v)) LockerSettings.BrokerHost        = v.GetString() ?? LockerSettings.BrokerHost;
                if (lk.TryGetProperty("BrokerPort",        out v))     LockerSettings.BrokerPort        = v.GetInt32();
                if (lk.TryGetProperty("TzDmInstanceId",    out v))     LockerSettings.TzDmInstanceId    = v.GetString() ?? LockerSettings.TzDmInstanceId;
                if (lk.TryGetProperty("CommandTimeoutSec", out v))     LockerSettings.CommandTimeoutSec = v.GetInt32();
            }

            if (root.TryGetProperty("Kiosk", out var ki))
            {
                if (ki.TryGetProperty("CodeDisplaySec", out var v)) KioskSettings.CodeDisplaySec = v.GetInt32();
                if (ki.TryGetProperty("ErrorResetSec",  out v))     KioskSettings.ErrorResetSec  = v.GetInt32();
                if (ki.TryGetProperty("AdminPin",       out v))     KioskSettings.AdminPin       = v.GetString() ?? KioskSettings.AdminPin;
                if (ki.TryGetProperty("WebAdminPin",    out v))     KioskSettings.WebAdminPin    = v.GetString() ?? KioskSettings.WebAdminPin;
                if (ki.TryGetProperty("WebAdminPort",   out v))     KioskSettings.WebAdminPort   = v.GetInt32();
            }

            if (root.TryGetProperty("Printer", out var pr))
            {
                if (pr.TryGetProperty("PrinterName",    out var v)) PrinterSettings.PrinterName    = v.GetString() ?? PrinterSettings.PrinterName;
                if (pr.TryGetProperty("PaperWidthMm",   out v))     PrinterSettings.PaperWidthMm   = v.GetInt32();
                if (pr.TryGetProperty("DataCentreName", out v))     PrinterSettings.DataCentreName = v.GetString() ?? PrinterSettings.DataCentreName;
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