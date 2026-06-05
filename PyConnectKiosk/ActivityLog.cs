using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PyConnectKiosk;

// ── One log entry ─────────────────────────────────────────────────────────────

public class LogEntry
{
    public string Timestamp { get; set; } = "";
    public string Category  { get; set; } = "";
    public string Message   { get; set; } = "";

    // Pipe-separated structured data for KeyDrawn / KeyReturned entries:
    // Format: "userName|rackTag|pyKey|slotNumber"
    // Empty for all other categories.
    public string Data      { get; set; } = "";
}

// ── Static logger ─────────────────────────────────────────────────────────────

public static class ActivityLog
{
    // ── Write ─────────────────────────────────────────────────────────────────

    public static void Write(string category, string message, string data = "")
    {
        try
        {
            var entries = Load(newestFirst: false);

            entries.Add(new LogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Category  = category,
                Message   = message,
                Data      = data
            });

            if (entries.Count > 1000)
                entries = entries.GetRange(entries.Count - 1000, 1000);

            var path = DataFiles.ActivityLogJson;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(entries, opts));
        }
        catch { /* logging must never crash the app */ }
    }

    // ── Convenience helpers ───────────────────────────────────────────────────

    /// <summary>Log OTC code generation (draw session).</summary>
    public static void CodeGenerated(string escort, string user, List<OtcResult> results)
    {
        var details = results
            .Select(r => r.Success
                ? $"{r.Rack.Tag}({r.Rack.LockId})x{r.Codes.Count}"
                : $"{r.Rack.Tag}(FAILED)")
            .ToList();

        int totalCodes = results.Where(r => r.Success).Sum(r => r.Codes.Count);
        string summary = $"Escort '{escort}' → User '{user}' | {string.Join(", ", details)} | {totalCodes} code(s) total";
        Write("CodeGeneration", summary);
    }

    /// <summary>
    /// Log a key draw — called after cabinet slot opens and user takes the key.
    /// Data format: "userName|rackTag|pyKey|slotNumber"
    /// </summary>
    public static void KeyDrawn(string escort, string user, string rackTag, string pyKey, int slotNumber)
    {
        string msg  = $"Escort '{escort}' → User '{user}' drew key for Rack {rackTag} from Slot {slotNumber}";
        string data = $"{user}|{rackTag}|{pyKey}|{slotNumber}";
        Write("KeyDrawn", msg, data);
    }

    /// <summary>
    /// Log a key return — called after user drops key into cabinet slot.
    /// Data format: "userName|rackTag|pyKey|slotNumber"
    /// </summary>
    public static void KeyReturned(string escort, string user, string rackTag, string pyKey, int slotNumber)
    {
        string msg  = $"Escort '{escort}' → User '{user}' returned key for Rack {rackTag} to Slot {slotNumber}";
        string data = $"{user}|{rackTag}|{pyKey}|{slotNumber}";
        Write("KeyReturned", msg, data);
    }

    /// <summary>Log an admin portal action.</summary>
    public static void AdminAction(string message)
        => Write("AdminAction", message);

    // ── Query: what key does a user currently hold? ───────────────────────────

    /// <summary>
    /// Returns the rack key currently held by a user, or null if none.
    /// Logic: scan log newest-first; find the first KeyDrawn or KeyReturned
    /// entry for this user. If it's KeyDrawn → they still hold the key.
    /// If it's KeyReturned → they've already returned it.
    /// Returns (rackTag, pyKey, slotNumber) or null.
    /// </summary>
    public static (string RackTag, string PyKey, int SlotNumber)? GetHeldKey(string userName)
    {
        try
        {
            var entries = Load(newestFirst: true);

            foreach (var entry in entries)
            {
                if (entry.Category != "KeyDrawn" && entry.Category != "KeyReturned")
                    continue;

                var parts = entry.Data.Split('|');
                if (parts.Length < 4) continue;

                if (!parts[0].Equals(userName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // First matching entry (newest) — tells us current state
                if (entry.Category == "KeyDrawn")
                {
                    int.TryParse(parts[3], out int slot);
                    return (RackTag: parts[1], PyKey: parts[2], SlotNumber: slot);
                }
                else // KeyReturned — already returned
                {
                    return null;
                }
            }
        }
        catch { /* ignore */ }

        return null;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Load all entries. newestFirst=true for display; false for appending.</summary>
    public static List<LogEntry> Load(bool newestFirst = true)
    {
        try
        {
            var path = DataFiles.ActivityLogJson;
            if (!File.Exists(path)) return new List<LogEntry>();

            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<LogEntry>>(json, opts) ?? new();

            if (newestFirst) list.Reverse();
            return list;
        }
        catch
        {
            return new List<LogEntry>();
        }
    }
}