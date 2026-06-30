using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace PyConnectKiosk;

// ── Kiosk screen states ───────────────────────────────────────────────────────

public enum KioskState
{
    Idle,
    EscortScan,     // Step 1 — escort face scan
    NricEntry,      // Step 2 — user types last-4 NRIC
    UserScan,       // Step 3 — user face scan
    RackSelect,     // Step 4 — choose rack to draw  (draw mode only)
    Generating,     // Awaiting PYCONNECT response   (draw mode only)
    DisplayCodes,   // Show / print OTC codes        (draw mode only)
    ReturnConfirm,  // Show held key + open slot     (return mode only)
    Error
}

// ── Session mode ──────────────────────────────────────────────────────────────

public enum SessionMode { Draw, Return }

// ── Lock state ────────────────────────────────────────────────────────────────

public enum LockState { Locked, Unlocked, Error, Unknown }

// ── Domain records ────────────────────────────────────────────────────────────

/// <summary>Physical rack / lock in the catalogue. Stored in data/racks.json.</summary>
public record RackItem(string Tag, string LockId, string DisplayName, string Row);

/// <summary>Face-recognition result.</summary>
public record FaceMatch(string Name, double Confidence);

/// <summary>Registered user loaded from users.json.</summary>
public record UserProfile(
    string Name,
    List<string> AllowedRacks,
    string LastFourNric,
    string Role = "User"
);

/// <summary>Result for one rack from PyConnect.</summary>
public record OtcResult(
    RackItem Rack,
    bool Success,
    List<string> Codes,
    string ErrorCode
)
{
    public string PrimaryCode => Codes.Count > 0 ? Codes[0] : "";
}

/// <summary>
/// One physical cabinet slot in the TZ locker.
/// Stored in data/slots.json — updated on every draw and return.
/// </summary>
public class SlotItem
{
    public int SlotNumber { get; set; }
    public string TzSerialNumber { get; set; } = "";
    public string? CurrentPyKey { get; set; }
    public bool IsEmpty { get; set; } = true;
    public LockState CurrentState { get; set; } = LockState.Unknown;

    [JsonIgnore]
    public bool IsReady => !string.IsNullOrWhiteSpace(TzSerialNumber);
    [JsonIgnore]
    public bool CanAcceptReturn => IsEmpty && IsReady;
    [JsonIgnore]
    public bool HasKey => !IsEmpty && CurrentPyKey != null && IsReady;
}