using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace PyConnectKiosk;

public enum LockState { Locked, Unlocked, Error, Unknown }

public record RackItem(string Tag, string LockId, string DisplayName, string Row);
public record FaceMatch(string Name, double Confidence);
public record UserProfile(
    string Name, List<string> AllowedRacks, string LastFourNric, string Role = "User");
public record OtcResult(RackItem Rack, bool Success, List<string> Codes, string ErrorCode)
{
    public string PrimaryCode => Codes.Count > 0 ? Codes[0] : "";
}
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