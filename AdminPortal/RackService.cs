using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PyConnectKiosk;

/// <summary>
/// Manages the rack catalogue from data/racks.json.
/// Replaces the hardcoded RackCatalogue in AppConfig.cs so admins
/// can add, edit and delete racks from the Admin Portal UI without
/// touching source code.
/// </summary>
public static class RackService
{
    // Use the shared DataFiles resolver (mark6\data\racks.json next to the .exe)
    // instead of a hard-coded developer path. Must be a property, not a const,
    // because DataFiles.RacksJson is resolved at runtime.
    private static string RacksFile => DataFiles.RacksJson;

    // In-memory cache — call Reload() after any save
    private static List<RackItem>? _cache;

    // ── Public API ────────────────────────────────────────────────────────────

    public static List<RackItem> GetAll() => Load();

    public static RackItem? ByTag(string tag)
        => Load().FirstOrDefault(r => r.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    public static void Reload() => _cache = null;

    // ── Save a new list back to racks.json ────────────────────────────────────

    public static void Save(List<RackItem> racks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RacksFile)!);
        var raw  = racks.Select(r => new RawRack(r.Tag, r.LockId, r.DisplayName, r.Row));
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(RacksFile, JsonSerializer.Serialize(raw, opts));
        _cache = null;   // force reload on next access
    }

    // ── Load from JSON — seeds default racks if file doesn't exist ────────────

    private static List<RackItem> Load()
    {
        if (_cache != null) return _cache;

        // Seed from AppConfig defaults if racks.json doesn't exist yet
        if (!File.Exists(RacksFile))
        {
            _cache = new List<RackItem>(RackCatalogue.Defaults);
            Save(_cache);   // persist the defaults so admin can edit them
            return _cache;
        }

        var json = File.ReadAllText(RacksFile).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            _cache = new List<RackItem>();
            return _cache;
        }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var raw  = JsonSerializer.Deserialize<List<RawRack>>(json, opts) ?? new List<RawRack>();

        _cache = raw.Select(r => new RackItem(
            r.Tag         ?? "",
            r.LockId      ?? "",
            r.DisplayName ?? "",
            r.Row         ?? ""
        )).ToList();

        return _cache;
    }

    // ── JSON shape ────────────────────────────────────────────────────────────

    private record RawRack(string? Tag, string? LockId, string? DisplayName, string? Row);
}