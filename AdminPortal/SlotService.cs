using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PyConnectKiosk;

public static class SlotService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true
    };

    // ── READ ──────────────────────────────────────────────────────────────────

    public static List<SlotItem> GetAll()
    {
        var path = DataFiles.SlotsJson;

        if (!File.Exists(path))
        {
            var defaults = BuildDefaultSlots();
            SaveAll(defaults);
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<SlotItem>>(json, _jsonOpts) ?? new();
    }

    public static SlotItem? FindSlotWithKey(string pyKey) =>
        GetAll().FirstOrDefault(s =>
            !s.IsEmpty &&
            string.Equals(s.CurrentPyKey, pyKey, StringComparison.OrdinalIgnoreCase));

    public static SlotItem? FindEmptySlot() =>
        GetAll().FirstOrDefault(s => s.CanAcceptReturn);

    public static SlotItem? BySlotNumber(int slotNumber) =>
        GetAll().FirstOrDefault(s => s.SlotNumber == slotNumber);

    public static SlotItem? BySerial(string tzSerial) =>
        GetAll().FirstOrDefault(s =>
            string.Equals(s.TzSerialNumber, tzSerial, StringComparison.OrdinalIgnoreCase));

    public static List<SlotItem> GetOccupiedSlots() =>
        GetAll().Where(s => s.HasKey).ToList();

    public static List<SlotItem> GetEmptySlots() =>
        GetAll().Where(s => s.CanAcceptReturn).ToList();

    // ── WRITE ─────────────────────────────────────────────────────────────────

    public static void ClearSlot(int slotNumber)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);
        if (slot == null) throw new InvalidOperationException($"Slot {slotNumber} not found.");
        slot.CurrentPyKey = null;
        slot.IsEmpty      = true;
        SaveAll(slots);
    }

    public static void OccupySlot(int slotNumber, string pyKey)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);
        if (slot == null) throw new InvalidOperationException($"Slot {slotNumber} not found.");
        slot.CurrentPyKey = pyKey;
        slot.IsEmpty      = false;
        SaveAll(slots);
    }

    // ── ADMIN ─────────────────────────────────────────────────────────────────

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

    public static void AdminSetSlotKey(int slotNumber, string? pyKey)
    {
        var slots = GetAll();
        var slot  = slots.FirstOrDefault(s => s.SlotNumber == slotNumber);
        if (slot == null) throw new InvalidOperationException($"Slot {slotNumber} not found.");
        slot.CurrentPyKey = pyKey;
        slot.IsEmpty      = pyKey == null;
        SaveAll(slots);
    }

    // ── SAVE ──────────────────────────────────────────────────────────────────

    public static void SaveAll(List<SlotItem> slots)
    {
        var path = DataFiles.SlotsJson;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(slots, _jsonOpts));
    }

    // ── SEED ──────────────────────────────────────────────────────────────────

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
                TzSerialNumber = "",
                CurrentPyKey   = rack.LockId,
                IsEmpty        = false
            });
        }

        return result;
    }
}