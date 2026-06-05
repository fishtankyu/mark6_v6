using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PyConnectKiosk;

public static class UserService
{
    // ── Simple in-memory cache — call Reload() after any save ─────────────────
    private static List<UserProfile>? _cache;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Return all registered users.</summary>
    public static List<UserProfile> GetAll() => Load();

    /// <summary>Find a user by exact name (case-insensitive).</summary>
    public static UserProfile? FindByName(string name)
        => Load().FirstOrDefault(u =>
               u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Find a user by their last-4 NRIC (case-insensitive).
    /// Used on the kiosk NRIC entry step to identify the visitor
    /// before their face is scanned.
    /// </summary>
    public static UserProfile? FindByNric(string nric)
        => Load().FirstOrDefault(u =>
               u.LastFourNric.Equals(nric.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Return the racks this user is allowed to access.</summary>
    public static List<RackItem> GetAllowedRacks(UserProfile user)
        => user.AllowedRacks
               .Select(tag => RackCatalogue.ByTag(tag))
               .Where(r => r != null)
               .Cast<RackItem>()
               .ToList();

    /// <summary>Invalidate cache so the next call re-reads the JSON file.</summary>
    public static void Reload() => _cache = null;

    // ── Load users.json → list of UserProfile ─────────────────────────────────

    private static List<UserProfile> Load()
    {
        if (_cache != null) return _cache;

        // Create an empty file if it doesn't exist yet
        if (!File.Exists(DataFiles.UsersJson))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataFiles.UsersJson)!);
            File.WriteAllText(DataFiles.UsersJson, "[]");
            _cache = new List<UserProfile>();
            return _cache;
        }

        var json = File.ReadAllText(DataFiles.UsersJson).Trim();

        // Empty file — treat as no users registered yet
        if (string.IsNullOrWhiteSpace(json))
        {
            _cache = new List<UserProfile>();
            return _cache;
        }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var raw  = JsonSerializer.Deserialize<List<RawUser>>(json, opts)
                   ?? new List<RawUser>();

        _cache = raw.Select(r => new UserProfile(
            Name         : r.Name          ?? "",
            AllowedRacks : r.Allowed_Racks  ?? new List<string>(),
            LastFourNric : r.Last_Four_Nric ?? ""
        )).ToList();

        return _cache;
    }

    // ── Raw JSON shape (matches the file on disk) ─────────────────────────────

    /// <summary>
    /// Internal DTO that mirrors the JSON structure.
    /// Keep snake_case to match the saved file format.
    /// </summary>
    private class RawUser
    {
        public string?       Name           { get; set; }
        public List<string>? Allowed_Racks  { get; set; }

        // Last 4 characters of the user's NRIC — e.g. "022F"
        public string?       Last_Four_Nric { get; set; }
    }
}