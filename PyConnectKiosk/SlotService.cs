using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PyConnectKiosk;

/// <summary>
/// Manages the physical state of all TZ cabinet slots.
/// Persists to data/slots.json — updated on every draw and return.
///
/// Key design: rack keys FLOAT between slots over time.
///   - slots.json tracks where each key is RIGHT NOW
///   - racks.json is the static master list (never changes)
///   - activity_log.json records the full history
///
/// Draw flow:
///   1. User picks rack A01 (LockId = "65-25070122")
///   2. FindSlotWithKey("65-25070122")  → finds which slot currently has it
///   3. TzMqttLockerService.UnlockSlotAsync(slotNumber)
///   4. User takes key
///   5. ClearSlot(slotNumber)           → slot is now empty
///
/// Return flow:
///   1. System knows user holds rack A01 key (from activity log)
///   2. FindEmptySlot()                 → finds any available empty slot
///   3. TzMqttLockerService.UnlockSlotAsync(slotNumber)
///   4. User drops key in
///   5. OccupySlot(slotNumber, "65-25070122") → slot now has A01 key
/// </summary>
public static class SlotService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true
    };

    // ── READ ──────────────────────────────────────────────────────────────────

    /// <summary>Returns all slots from data/slots.json.</summary>
    public static List<SlotItem> GetAll()
    {
        var path = DataFiles.SlotsJson;

        if (!File.Exists(path))
        {
            // First run — seed empty slots from racks.json
            var defaults = BuildDefaultSlots();
            SaveAll(defaults);
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<SlotItem>>(json, _jsonOpts) ?? new();
    }

    /// <summary>
    /// Find which slot currently holds a specific rack key.
    /// Used during DRAW to locate where the key is.
    /// </summary>
    public static SlotItem? FindSlotWithKey(string pyKey) =>
        GetAll().FirstOrDefault(s =>
            !s.IsEmpty &&
            string.Equals(s.CurrentPyKey, pyKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Find any empty slot available to accept a returning key.
    /// Used during RETURN — picks the first available empty slot.
    /// </summary>
    public static SlotItem? FindEmptySlot() =>
        GetAll().FirstOrDefault(s => s.CanAcceptReturn);

    /// <summary>
    /// Return ALL slots that currently hold a physical key
    /// (non-empty, with a PyKey assigned, and mapped to a TZ serial).
    /// Used during DRAW with the floating-key model — the kiosk dispenses
    /// whichever key is currently available, not a specific rack's key.
    /// </summary>
    public static List<SlotItem> GetAvailableKeys() =>
        GetAll()
            .Where(s => !s.IsEmpty
                     && !string.IsNullOrWhiteSpace(s.CurrentPyKey)
                     && !string.IsNullOrWhiteSpace(s.TzSerialNumber))
            .OrderBy(s => s.SlotNumber)
            .ToList();

    /// <summary>Find slot by slot number.</summary>
    public static SlotItem? BySlotNumber(int slotNumber) =>
        GetAll().FirstOrDefault(s => s.SlotNumber == slotNumber);

    /// <summary>Find slot by TZ serial number.</summary>
    public static SlotItem? BySerial(string tzSerial) =>
        GetAll().FirstOrDefault(s =>
            string.Equals(s.TzSerialNumber, tzSerial, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns all slots that currently have a key (available to draw).</summary>
    public static List<SlotItem> GetOccupiedSlots() =>
        GetAll().Where(s => s.HasKey).ToList();

    /// <summary>Returns all empty slots (available for return).</summary>
    public static List<SlotItem> GetEmptySlots() =>
        GetAll().Where(s => s.CanAcceptReturn).ToList();

    // ── WRITE — called during draw / return flows ──────────────────────────────

    /// <summary>
    /// Called AFTER user takes key out during DRAW.
    /// Clears the slot — marks it empty, removes the PyKey reference.
    /// </summary>
    public static void ClearSlot(int slotNumber)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);

        if (slot == null)
            throw new InvalidOperationException($"Slot {slotNumber} not found.");

        slot.CurrentPyKey = null;
        slot.IsEmpty      = true;

        SaveAll(slots);
    }

    /// <summary>
    /// Called AFTER user drops key in during RETURN.
    /// Occupies the slot — marks it full, records which PyKey is now here.
    /// </summary>
    public static void OccupySlot(int slotNumber, string pyKey)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);

        if (slot == null)
            throw new InvalidOperationException($"Slot {slotNumber} not found.");

        if (!slot.IsEmpty)
            throw new InvalidOperationException(
                $"Slot {slotNumber} is already occupied by key {slot.CurrentPyKey}.");

        slot.CurrentPyKey = pyKey;
        slot.IsEmpty      = false;

        SaveAll(slots);
    }

    // ── ADMIN OPERATIONS ──────────────────────────────────────────────────────

    /// <summary>
    /// Assign a TZ serial number to a slot.
    /// Called from admin portal after TZ DM discovery.
    /// </summary>
    public static void AssignTzSerial(int slotNumber, string tzSerialNumber)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);

        if (slot == null)
        {
            slots.Add(new SlotItem
            {
                SlotNumber     = slotNumber,
                TzSerialNumber = tzSerialNumber,
                IsEmpty        = true
            });
        }
        else
        {
            slot.TzSerialNumber = tzSerialNumber;
        }

        SaveAll(slots);
    }

    /// <summary>
    /// Manually set which key is in a slot.
    /// Used by admin to initialise or correct the physical state.
    /// </summary>
    public static void AdminSetSlotKey(int slotNumber, string? pyKey)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);

        if (slot == null)
            throw new InvalidOperationException($"Slot {slotNumber} not found.");

        slot.CurrentPyKey = pyKey;
        slot.IsEmpty      = pyKey == null;

        SaveAll(slots);
    }

    // ── SAVE ──────────────────────────────────────────────────────────────────

    /// <summary>Save all slots to data/slots.json.</summary>
    public static void SaveAll(List<SlotItem> slots)
    {
        var path = DataFiles.SlotsJson;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(slots, _jsonOpts));
    }

    // ── SEED ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build default slots from racks.json on first run.
    /// Each rack gets one slot. TZ serial is blank until admin assigns it.
    /// PyKey is pre-filled from racks.json and slot starts occupied.
    /// </summary>
    private static List<SlotItem> BuildDefaultSlots()
    {
        var racks  = RackService.GetAll();
        var result = new List<SlotItem>();
        int i      = 1;

        foreach (var rack in racks)
        {
            result.Add(new SlotItem
            {
                SlotNumber     = i++,
                TzSerialNumber = "",             // blank — admin must assign after TZ discovery
                CurrentPyKey   = rack.LockId,   // pre-filled — assume key starts in cabinet
                IsEmpty        = false
            });
        }

        return result;
    }
}