using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Microsoft.Extensions.Logging;

namespace PyConnectKiosk;

// ─────────────────────────────────────────────────────────────────────────────
// MODELS
// ─────────────────────────────────────────────────────────────────────────────

record TzLicensePayload(
    bool    Valid,
    int     DevicesCount,
    string  Expiry,
    int     DiscoveredDevices,
    string? Error
);

record TzDiscoveredDevice(
    string  SerialNumber,
    string  Type,
    string  State,
    bool?   Sensor
);

record TzDiscoverPayload(
    string                   BoardType,
    List<TzDiscoveredDevice> DeviceList,
    int                      ServerId
);

record TzLockStatusPayload(
    string State,
    bool   Sensor,
    int    ServerId,
    string BoardId,
    string LockId
);

record TzCardSwipePayload(string BoardId, string CardNumber, string DeviceId);

// Confirmed working payload from MQTT Explorer:
// { "boardId":"0", "lockId":"A0 20 00 5C C5", "action":"open", "id":3 }
record TzSetLockPayload(string BoardId, string LockId, string Action, int Id);

record TzGetLockPayload(string BoardId, string LockId, int Id);


// ─────────────────────────────────────────────────────────────────────────────
// SERVICE
// ─────────────────────────────────────────────────────────────────────────────

public class TzMqttLockerService : IDisposable
{
    private readonly string _brokerHost;
    private readonly int    _brokerPort;
    private readonly string _tzDmInstanceId;

    private IMqttClient? _client;
    private readonly Dictionary<int, SlotItem>    _slotsByNumber = new();
    private readonly Dictionary<string, SlotItem> _slotsBySerial = new();
    private bool _tzDmConnected;
    private bool _licenseValid;
    private int  _messageIdCounter;

    private readonly ILogger<TzMqttLockerService> _logger;

    public event Action<int, LockState>? OnSlotStateChanged;
    public event Action<string, string>? OnCardSwiped;
    public event Action<bool>?           OnTzDmConnectionChanged;
    public event Action<bool, string?>?  OnLicenseStatus;

    private string T(string suffix) => $"tz/v1/{_tzDmInstanceId}/{suffix}";

    private string TopicConnected  => T("connected");
    private string TopicLicense    => T("license");
    private string TopicDiscovered => T("status/discover");
    private string TopicLockStatus => T("status/lock");
    private string TopicVersion    => T("status/version");
    private string TopicCardSwipe  => T("event/swipe");
    private string TopicDiscover   => T("discover");
    private string TopicGetLock    => T("get/lock");
    private string TopicSetLock    => T("set/lock");

    /// <summary>True when TZ DM is connected AND license is valid.</summary>
    public bool IsReady => _tzDmConnected && _licenseValid;

    // ── Debug flags — set to false once locker is confirmed working ───────────
    private const bool BypassIsReadyCheck = true;  // ← TEMPORARY: bypasses IsReady check
    // ─────────────────────────────────────────────────────────────────────────


    public TzMqttLockerService(
        string brokerHost,
        int    brokerPort,
        string tzDmInstanceId,
        ILogger<TzMqttLockerService> logger)
    {
        _brokerHost     = brokerHost;
        _brokerPort     = brokerPort;
        _tzDmInstanceId = tzDmInstanceId;
        _logger         = logger;
    }


    // ─────────────────────────────────────────────────────────────────────────
    // SLOT MAPPING
    // ─────────────────────────────────────────────────────────────────────────

    public void LoadSlotMappings(IEnumerable<SlotItem> slots)
    {
        _slotsByNumber.Clear();
        _slotsBySerial.Clear();

        foreach (var s in slots)
        {
            if (s.SlotNumber > 0)
                _slotsByNumber[s.SlotNumber] = s;
            if (!string.IsNullOrWhiteSpace(s.TzSerialNumber))
                _slotsBySerial[s.TzSerialNumber] = s;
        }

        _logger.LogInformation("[TZ] Loaded {Count} slot mappings.", _slotsByNumber.Count);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // CONNECT
    // ─────────────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_brokerHost, _brokerPort)
            .WithClientId($"mark6-kiosk-{Environment.MachineName}")
            .WithCleanSession(false)
            .Build();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync              += OnDisconnectedAsync;

        await _client.ConnectAsync(options, ct);
        _logger.LogInformation("[TZ] Connected to broker {Host}:{Port}", _brokerHost, _brokerPort);

        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(TopicConnected,  MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(TopicLicense,    MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(TopicDiscovered, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(TopicLockStatus, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(TopicVersion,    MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(TopicCardSwipe,  MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await RequestDiscoverAsync(ct);

        // Force TZ DM to republish license (not retained)
        await Task.Delay(500, ct);
        await PublishJsonAsync(T("get/version"), new { id = NextMessageId() }, ct);
        _logger.LogInformation("[TZ] Requested version — waiting for license republish.");
    }


    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC COMMANDS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unlock (open) a cabinet slot by slot number.
    /// Publishes { "boardId":"0", "lockId":"...", "action":"open", "id":N }
    /// to tz/v1/nxtgendmsitsyd/set/lock
    /// </summary>
    public async Task<bool> UnlockSlotAsync(int slotNumber, CancellationToken ct = default)
    {
        // IsReady check — bypassed temporarily for testing
        if (!BypassIsReadyCheck && !IsReady)
        {
            _logger.LogWarning("[TZ] UnlockSlot({Slot}): not ready (connected={C} license={L}).",
                slotNumber, _tzDmConnected, _licenseValid);
            return false;
        }

        if (BypassIsReadyCheck)
            _logger.LogWarning("[TZ] *** IsReady check BYPASSED — connected={C} license={L} ***",
                _tzDmConnected, _licenseValid);

        if (!_slotsByNumber.TryGetValue(slotNumber, out var slot))
        {
            _logger.LogError("[TZ] UnlockSlot({Slot}): no slot mapping found.", slotNumber);
            return false;
        }

        if (string.IsNullOrWhiteSpace(slot.TzSerialNumber))
        {
            _logger.LogError("[TZ] UnlockSlot({Slot}): TZ serial not assigned.", slotNumber);
            return false;
        }

        _logger.LogInformation("[TZ] Opening slot {Slot} → serial {Serial}", slotNumber, slot.TzSerialNumber);
        await PublishSetLockAsync(slot.TzSerialNumber, "open", ct);
        return true;
    }

    /// <summary>
    /// Lock (close) a cabinet slot by slot number.
    /// Publishes { "boardId":"0", "lockId":"...", "action":"close", "id":N }
    /// </summary>
    public async Task<bool> LockSlotAsync(int slotNumber, CancellationToken ct = default)
    {
        if (!BypassIsReadyCheck && !IsReady)
        {
            _logger.LogWarning("[TZ] LockSlot({Slot}): not ready.", slotNumber);
            return false;
        }

        if (!_slotsByNumber.TryGetValue(slotNumber, out var slot))
        {
            _logger.LogError("[TZ] LockSlot({Slot}): no mapping found.", slotNumber);
            return false;
        }

        if (string.IsNullOrWhiteSpace(slot.TzSerialNumber))
        {
            _logger.LogError("[TZ] LockSlot({Slot}): TZ serial not assigned.", slotNumber);
            return false;
        }

        _logger.LogInformation("[TZ] Closing slot {Slot} → serial {Serial}", slotNumber, slot.TzSerialNumber);
        await PublishSetLockAsync(slot.TzSerialNumber, "close", ct);
        return true;
    }

    public IReadOnlyDictionary<int, SlotItem> GetAllSlots() => _slotsByNumber;

    public async Task RefreshAllStatusAsync(CancellationToken ct = default)
    {
        foreach (var slot in _slotsByNumber.Values)
            if (!string.IsNullOrWhiteSpace(slot.TzSerialNumber))
                await RequestGetLockAsync(slot.TzSerialNumber, ct);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // INBOUND MESSAGE HANDLER
    // ─────────────────────────────────────────────────────────────────────────

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic   = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        try
        {
            if      (topic == TopicConnected)  HandleConnected(payload);
            else if (topic == TopicLicense)    HandleLicense(payload);
            else if (topic == TopicDiscovered) HandleDiscovered(payload);
            else if (topic == TopicLockStatus) HandleLockStatus(payload);
            else if (topic == TopicCardSwipe)  HandleCardSwipe(payload);
            else if (topic == TopicVersion)    _logger.LogDebug("[TZ] Version: {P}", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TZ] Error on topic {Topic}", topic);
        }

        return Task.CompletedTask;
    }

    private void HandleConnected(string payload)
    {
        _tzDmConnected = payload.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("[TZ] TZ DM connected: {State}", _tzDmConnected);
        OnTzDmConnectionChanged?.Invoke(_tzDmConnected);
    }

    private void HandleLicense(string payload)
    {
        var lic = Deserialize<TzLicensePayload>(payload);
        _licenseValid = lic?.Valid ?? false;
        _logger.LogInformation(
            "[TZ] License valid={Valid}, licensed={Count}, discovered={Disc}, expiry={Expiry}",
            _licenseValid, lic?.DevicesCount, lic?.DiscoveredDevices, lic?.Expiry);
        if (!_licenseValid)
            _logger.LogWarning("[TZ] License INVALID: {Error}", lic?.Error);
        OnLicenseStatus?.Invoke(_licenseValid, lic?.Error);
    }

    private void HandleDiscovered(string payload)
    {
        var disc = Deserialize<TzDiscoverPayload>(payload);
        if (disc?.DeviceList == null) return;
        _logger.LogInformation("[TZ] Discovered {Count} devices.", disc.DeviceList.Count);
        foreach (var dev in disc.DeviceList)
        {
            if (!dev.Type.Equals("Radial", StringComparison.OrdinalIgnoreCase)) continue;
            var state = ParseState(dev.State);
            if (_slotsBySerial.TryGetValue(dev.SerialNumber, out var slot))
            {
                slot.CurrentState = state;
                _logger.LogInformation("  [TZ] Slot {Slot} ← {Serial} state={State} sensor={Sensor}",
                    slot.SlotNumber, dev.SerialNumber, state, dev.Sensor);
            }
            else
            {
                _logger.LogWarning("  [TZ] Unmapped serial={Serial} — assign in admin portal.", dev.SerialNumber);
            }
        }
    }

    private void HandleLockStatus(string payload)
    {
        var status = Deserialize<TzLockStatusPayload>(payload);
        if (status == null) return;
        var newState = ParseState(status.State);
        if (_slotsBySerial.TryGetValue(status.LockId, out var slot))
        {
            slot.CurrentState = newState;
            _logger.LogInformation("[TZ] Slot {Slot} ({Serial}) → {State}  sensor={Sensor}",
                slot.SlotNumber, status.LockId, newState, status.Sensor);
            OnSlotStateChanged?.Invoke(slot.SlotNumber, newState);
        }
        else
        {
            _logger.LogDebug("[TZ] Status for unmapped serial {Serial}: {State}", status.LockId, newState);
        }
    }

    private void HandleCardSwipe(string payload)
    {
        var swipe = Deserialize<TzCardSwipePayload>(payload);
        if (swipe == null) return;
        _logger.LogInformation("[TZ] Card swipe: card={Card} reader={Reader}", swipe.CardNumber, swipe.DeviceId);
        OnCardSwiped?.Invoke(swipe.CardNumber, swipe.DeviceId);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // PUBLISH HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private async Task PublishSetLockAsync(string serialNumber, string action, CancellationToken ct)
    {
        var payload = new TzSetLockPayload(
            BoardId: "0",
            LockId:  serialNumber,
            Action:  action,
            Id:      NextMessageId()
        );
        await PublishJsonAsync(TopicSetLock, payload, ct);
    }

    private async Task RequestGetLockAsync(string serialNumber, CancellationToken ct)
    {
        var payload = new TzGetLockPayload(BoardId: "0", LockId: serialNumber, Id: NextMessageId());
        await PublishJsonAsync(TopicGetLock, payload, ct);
    }

    private async Task RequestDiscoverAsync(CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(TopicDiscover)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client!.PublishAsync(msg, ct);
    }

    private async Task PublishJsonAsync<T>(string topic, T obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client!.PublishAsync(msg, ct);
        _logger.LogInformation("[TZ] → {Topic}  {Json}", topic, json);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // RECONNECT
    // ─────────────────────────────────────────────────────────────────────────

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogWarning("[TZ] Disconnected. Retrying in 5s…");
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            await _client!.ReconnectAsync();
            await Task.Delay(500);
            await PublishJsonAsync(T("get/version"), new { id = NextMessageId() }, default);
        }
        catch (Exception ex) { _logger.LogError(ex, "[TZ] Reconnect failed."); }
    }


    // ─────────────────────────────────────────────────────────────────────────
    // UTILITIES
    // ─────────────────────────────────────────────────────────────────────────

    private static LockState ParseState(string? s) => s?.ToLower() switch
    {
        "locked"   => LockState.Locked,
        "unlocked" => LockState.Unlocked,
        "error"    => LockState.Error,
        _          => LockState.Unknown
    };

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    private int NextMessageId() => Interlocked.Increment(ref _messageIdCounter);

    public void Dispose() => _client?.Dispose();
}