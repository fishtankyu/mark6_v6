using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PyConnectKiosk;

// ── One log entry ─────────────────────────────────────────────────────────────

public class LogEntry
{
    // ISO timestamp: "2026-09-03 17:12:45"
    public string Timestamp { get; set; } = "";

    // "CodeGeneration" or "AdminAction"
    public string Category  { get; set; } = "";

    // Human-readable summary shown in the admin log tab
    public string Message   { get; set; } = "";
}

// ── Static logger — call from anywhere ───────────────────────────────────────

public static class ActivityLog
{
    // Use the shared DataFiles resolver (mark6\data\activity_log.json next to the .exe)
    // instead of a hard-coded developer path.
    private static string LogFile => DataFiles.ActivityLogJson;

    // ── Write a new entry ─────────────────────────────────────────────────────

    public static void Write(string category, string message)
    {
        try
        {
            // Load existing entries (or start fresh)
            var entries = Load();

            entries.Add(new LogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Category  = category,
                Message   = message
            });

            // Keep only last 1 000 entries so the file doesn't grow forever
            if (entries.Count > 1000)
                entries = entries.GetRange(entries.Count - 1000, 1000);

            // Save back
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(LogFile, JsonSerializer.Serialize(entries, opts));
        }
        catch
        {
            // Logging must never crash the application
        }
    }

    // ── Convenience helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Log a code generation event from the kiosk with full rack and code details.
    /// Example: Escort 'John' generated codes for user 'Alice' | A01(65-25070122)x2, A02(65-25110187)x2
    /// </summary>
    public static void CodeGenerated(string escort, string user, List<OtcResult> results)
    {
        // Build a summary string: RackTag(LockId)xCodeCount for each rack
        var details = results
            .Select(r => r.Success
                ? $"{r.Rack.Tag}({r.Rack.LockId})x{r.Codes.Count}"
                : $"{r.Rack.Tag}(FAILED)")
            .ToList();

        int totalCodes = results.Where(r => r.Success).Sum(r => r.Codes.Count);
        string summary = $"Escort '{escort}' → User '{user}' | {string.Join(", ", details)} | {totalCodes} code(s) total";
        Write("CodeGeneration", summary);
    }

    /// <summary>Log an action taken inside the Admin Portal.</summary>
    public static void AdminAction(string message)
        => Write("AdminAction", message);

    // ── Read all entries (newest first) ──────────────────────────────────────

    public static List<LogEntry> Load()
    {
        try
        {
            if (!File.Exists(LogFile)) return new List<LogEntry>();
            var json = File.ReadAllText(LogFile);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<LogEntry>>(json, opts);
            list?.Reverse();           // newest first for display
            return list ?? new List<LogEntry>();
        }
        catch
        {
            return new List<LogEntry>();
        }
    }
}