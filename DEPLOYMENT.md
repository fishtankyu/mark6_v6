# mark6 v2 — What's new

## Changes since v1

### 1. Floating-key draw logic
User picks a rack → kiosk allocates the next available physical key in the
cabinet → OTP is generated for that physical key → that slot opens.

The `LockId` in `racks.json` no longer determines which key gets dispensed.
Racks are now access-permission labels. If you want "Rack A01 ALWAYS
dispenses key X" semantics, keep v1.

**Files changed:**
- `PyConnectKiosk/SlotService.cs` — new `GetAvailableKeys()`
- `PyConnectKiosk/PyConnectService.cs` — new `GenerateForAssignmentsAsync()`
- `PyConnectKiosk/MainWindow.xaml.cs` — `OnGenerate()` uses floating allocation

### 2. Runtime configuration via `appsettings.json`
Change IPs, ports, PINs, and face thresholds **without recompiling**.
Edit `appsettings.json` next to the `.exe` and restart.

```json
{
  "PyConnect":  { "Host": "192.168.1.4", "Port": 13131, ... },
  "Locker":     { "BrokerHost": "192.168.1.2", "BrokerPort": 1883, ... },
  "Face":       { "Threshold": 100.0, "ConfirmFrames": 15, ... },
  "Kiosk":      { "AdminPin": "1234", "WebAdminPin": "1234", ... },
  "Printer":    { ... }
}
```

If the file is missing or malformed, the apps fall back to baked-in defaults
(same values as v1). They never crash because of a bad config.

**Files changed:**
- `PyConnectKiosk/AppConfig.cs` — `const` → `static` properties + `AppSettings.LoadFromDisk()`
- `AdminPortal/AppConfig.cs` — same
- `PyConnectKiosk/App.xaml.cs` & `AdminPortal/App.xaml.cs` — call loader at startup

### 3. Seed data aligned with real PyKeys
`data/racks.json` and `data/slots.json` now seed with `13071023` and
`13070870` instead of the old test values. If you delete your runtime
`data\` folder, the kiosk will re-seed to a working state on next launch.

---

## Build & deploy

```powershell
Remove-Item -Recurse -Force C:\PyConnectKiosk -ErrorAction SilentlyContinue
cd C:\Users\ISS_Laptop_5\Downloads\mark6_fixed\mark6_fixed
dotnet publish PyConnectKiosk\PyConnectKiosk.csproj -c Release -r win-x64 --self-contained true -o C:\PyConnectKiosk
dotnet publish AdminPortal\AdminPortal.csproj      -c Release -r win-x64 --self-contained true -o C:\PyConnectKiosk
copy appsettings.json C:\PyConnectKiosk\
```

Then double-click `C:\PyConnectKiosk\PyConnectKiosk.exe`.

## Changing an IP later (the whole point of #2)

1. Open `C:\PyConnectKiosk\appsettings.json` in Notepad
2. Edit the value (e.g. `"BrokerHost": "192.168.1.7"`)
3. Save
4. Close and reopen `PyConnectKiosk.exe`

No rebuild. No Visual Studio. No SDK on the target PC.

## Testing the new draw flow

1. Make sure `data\slots.json` has at least one slot with `IsEmpty: false`
   and a valid `CurrentPyKey` + `TzSerialNumber`.
2. Launch the kiosk, scan faces, get to the rack selection screen.
3. Pick any rack. The kiosk allocates slot 1 (the first available).
4. OTP is generated for slot 1's `CurrentPyKey`. Slot 1 opens.
5. `slots.json` is updated automatically — slot 1 is now empty.
6. Next draw goes to slot 2.
7. After both slots are drawn, the kiosk shows:
   *"Only 0 key(s) currently in the cabinet, but 1 rack(s) requested."*

## What each file controls

| File                          | What                            | Lives where on the target PC      |
| ----------------------------- | ------------------------------- | --------------------------------- |
| `appsettings.json`            | IPs, ports, PINs, thresholds    | `C:\PyConnectKiosk\`              |
| `data\racks.json`             | Rack catalogue (display only)   | `C:\PyConnectKiosk\data\`         |
| `data\slots.json`             | Live cabinet state              | `C:\PyConnectKiosk\data\`         |
| `data\users.json`             | Allowed users + permissions     | `C:\PyConnectKiosk\data\`         |
| `data\activity_log.json`      | Audit trail                     | `C:\PyConnectKiosk\data\`         |
| `faces\escorts\*.jpg`         | Enrolled escort faces           | `C:\PyConnectKiosk\faces\escorts\`|
| `faces\users\*.jpg`           | Enrolled user faces             | `C:\PyConnectKiosk\faces\users\`  |
