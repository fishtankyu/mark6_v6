using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace PyConnectKiosk;

public partial class MainWindow : System.Windows.Window
{
    // ── Services ──────────────────────────────────────────────────────────────
    private FaceService? _escortFace;
    private FaceService? _userFace;
    private readonly CameraService _camera = new();
    private readonly TzMqttLockerService _locker;

    // ── Session state ─────────────────────────────────────────────────────────
    private KioskState _state = KioskState.Idle;
    private SessionMode _mode = SessionMode.Draw;   // draw or return
    private FaceMatch? _escort = null;
    private FaceMatch? _user = null;
    private UserProfile? _userProfile = null;
    private int _confirmCount = 0;

    // ── NRIC entry ────────────────────────────────────────────────────────────
    private string _nricInput = "";

    // ── Code quantity ─────────────────────────────────────────────────────────
    private const int MinCodes = 1;
    private const int MaxCodes = 5;
    private int _codesPerRack = 1;

    // ── Print ─────────────────────────────────────────────────────────────────
    private PrintJob? _lastPrintJob;

    // ── Return session state ──────────────────────────────────────────────────
    private SlotItem? _returnTargetSlot; // empty slot chosen for return
    private string? _returnPyKey;      // PyKey being returned
    private string? _returnRackTag;    // rack tag being returned

    // ── Timers ────────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _clockTimer = new()
    { Interval = TimeSpan.FromSeconds(1) };
    private DispatcherTimer? _scanTimer;
    private DispatcherTimer? _countdown;
    private int _countdownSec;

    // ── Rack selection ────────────────────────────────────────────────────────
    private readonly ObservableCollection<RackViewModel> _racks = new();

    // ═════════════════════════════════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();

        _clockTimer.Tick += (_, _) =>
            ClockText.Text = DateTime.Now.ToString("HH:mm:ss   dd MMM yyyy");
        _clockTimer.Start();

        try
        {
            _escortFace = new FaceService(FaceSettings.EscortFacesDir);
            _userFace = new FaceService(FaceSettings.UserFacesDir);
        }
        catch (Exception ex) { ShowError($"Face service init failed:\n{ex.Message}"); }

        // Initialise TZ locker service
        var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        _locker = new TzMqttLockerService(
            brokerHost: LockerSettings.BrokerHost,
            brokerPort: LockerSettings.BrokerPort,
            tzDmInstanceId: LockerSettings.TzDmInstanceId,
            logger: loggerFactory.CreateLogger<TzMqttLockerService>()
        );
        _locker.LoadSlotMappings(SlotService.GetAll());
        _locker.OnTzDmConnectionChanged += c =>
            Dispatcher.Invoke(() => System.Diagnostics.Debug.WriteLine($"[LOCKER] TZ DM: {c}"));
        _locker.OnSlotStateChanged += (s, st) =>
            Dispatcher.Invoke(() => System.Diagnostics.Debug.WriteLine($"[LOCKER] Slot {s} → {st}"));
        _ = ConnectLockerAsync();

        RackGrid.ItemsSource = _racks;
        _camera.FrameReady += OnFrame;
    }

    private async System.Threading.Tasks.Task ConnectLockerAsync()
    {
        try
        {
            await _locker.ConnectAsync();
            DebugConsole.Show("Locker", $"MQTT Connected to {LockerSettings.BrokerHost}:{LockerSettings.BrokerPort}");

        }

        catch (Exception ex)
        {
            MessageBox.Show($"MQTT Connect FAILED:\n{ex.Message}", "Locker Error");
            System.Diagnostics.Debug.WriteLine($"[LOCKER] Connect failed: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CAMERA / FACE
    // ═════════════════════════════════════════════════════════════════════════

    private void OnFrame(System.Windows.Media.Imaging.BitmapSource _, Mat frame)
    {
        if (_state != KioskState.EscortScan && _state != KioskState.UserScan)
        { frame.Dispose(); return; }

        bool isEscort = _state == KioskState.EscortScan;
        var service = isEscort ? _escortFace : _userFace;
        var match = service?.ProcessFrame(frame);
        var bitmap = CameraService.MatToBitmapSource(frame);
        bitmap.Freeze();
        frame.Dispose();

        Dispatcher.Invoke(() =>
        {
            if (isEscort) { EscortCam.Source = bitmap; HandleConfirm(match, true); }
            else { UserCam.Source = bitmap; HandleConfirm(match, false); }
        });
    }

    private void HandleConfirm(FaceMatch? match, bool isEscort)
    {
        if (match == null)
        {
            _confirmCount = 0;
            if (isEscort) EscortStatus.Text = "Scanning for escort…";
            else UserStatus.Text = "Scanning for user…";
            return;
        }

        _confirmCount++;
        string msg = $"Detected: {match.Name}  ({_confirmCount}/{FaceSettings.ConfirmFrames})";
        if (isEscort) EscortStatus.Text = msg;
        else UserStatus.Text = msg;

        if (_confirmCount >= FaceSettings.ConfirmFrames)
        {
            _confirmCount = 0;
            StopCamera();
            if (isEscort) OnEscortConfirmed(match);
            else OnUserConfirmed(match);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  IDLE — two buttons: DRAW and RETURN
    // ═════════════════════════════════════════════════════════════════════════

    private void GoIdle()
    {
        StopCamera(); StopCountdown();
        _escort = null;
        _user = null;
        _userProfile = null;
        _confirmCount = 0;
        _lastPrintJob = null;
        _codesPerRack = 1;
        _nricInput = "";
        _returnTargetSlot = null;
        _returnPyKey = null;
        _returnRackTag = null;
        _racks.Clear();
        StepText.Text = "";
        _state = KioskState.Idle;
        Show(PageIdle);
    }

    // Draw button on idle screen
    private void BtnStartEscort_Click(object s, RoutedEventArgs e)
    {
        _mode = SessionMode.Draw;
        GoEscortScan();
    }

    // Return button on idle screen
    private void BtnReturnKey_Click(object s, RoutedEventArgs e)
    {
        _mode = SessionMode.Return;
        GoEscortScan();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STEP 1 — ESCORT SCAN (shared by draw and return)
    // ═════════════════════════════════════════════════════════════════════════

    private void GoEscortScan()
    {
        _state = KioskState.EscortScan;
        StepText.Text = _mode == SessionMode.Draw ? "Step 1 / 4  –  Escort Scan" : "Return: Step 1 / 3  –  Escort Scan";
        Show(PageEscortScan);
        StartScanTimer(EscortTimer, GoIdle);
        _camera.Start(FaceSettings.CameraIndex);
    }

    private void OnEscortConfirmed(FaceMatch escort)
    {
        _escort = escort;
        StopCountdown();
        GoNricEntry();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STEP 2 — NRIC ENTRY (shared)
    // ═════════════════════════════════════════════════════════════════════════

    private void GoNricEntry()
    {
        _state = KioskState.NricEntry;
        _nricInput = "";
        StepText.Text = _mode == SessionMode.Draw ? "Step 2 / 4  –  Enter NRIC" : "Return: Step 2 / 3  –  Enter NRIC";
        NricEscortName.Text = _escort?.Name ?? "";
        UpdateNricDisplay();
        NricHint.Visibility = Visibility.Collapsed;
        Show(PageNricEntry);
    }

    private void KeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (_nricInput.Length >= 4) return;
        _nricInput += btn.Tag?.ToString() ?? "";
        UpdateNricDisplay();
    }

    private void BtnNricBack_Click(object sender, RoutedEventArgs e)
    {
        if (_nricInput.Length > 0) _nricInput = _nricInput[..^1];
        UpdateNricDisplay();
    }

    private void UpdateNricDisplay()
    {
        int len = _nricInput.Length;
        NricDisplay.Text = string.Join(" ",
            _nricInput.ToCharArray().Select(c => c.ToString())
                      .Concat(Enumerable.Repeat("_", 4 - len)));
        BtnNricConfirm.IsEnabled = len == 4;
    }

    private void BtnNricConfirm_Click(object sender, RoutedEventArgs e)
    {
        _userProfile = UserService.FindByNric(_nricInput);
        if (_userProfile == null)
        {
            NricHint.Text = $"NRIC '{_nricInput}' not registered. Please try again.";
            NricHint.Visibility = Visibility.Visible;
            _nricInput = "";
            UpdateNricDisplay();
            return;
        }
        StopCountdown();
        GoUserScan();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STEP 3 — USER FACE SCAN (shared)
    // ═════════════════════════════════════════════════════════════════════════

    private void GoUserScan()
    {
        _state = KioskState.UserScan;
        EscortVerifiedName.Text = _escort?.Name ?? "";
        NricVerifiedUser.Text = _userProfile?.Name ?? "";
        StepText.Text = _mode == SessionMode.Draw ? "Step 3 / 4  –  User Face Scan" : "Return: Step 3 / 3  –  User Face Scan";
        Show(PageUserScan);
        StartScanTimer(UserTimer, GoIdle);
        _camera.Start(FaceSettings.CameraIndex);
    }

    private void OnUserConfirmed(FaceMatch user)
    {
        if (!user.Name.Equals(_userProfile!.Name, StringComparison.OrdinalIgnoreCase))
        {
            ShowError($"Face does not match the registered NRIC.\n" +
                      $"Expected: {_userProfile.Name}\nDetected: {user.Name}\n\n" +
                      $"Please contact your administrator.");
            return;
        }

        _user = user;
        StopCountdown();

        // Branch based on session mode
        if (_mode == SessionMode.Draw)
            GoRackSelect();
        else
            GoReturnConfirm();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DRAW FLOW — Step 4 onwards
    // ═════════════════════════════════════════════════════════════════════════

    private void GoRackSelect()
    {
        _state = KioskState.RackSelect;
        StepText.Text = "Step 4 / 4  –  Select Racks";
        BadgeEscort.Text = _escort?.Name ?? "";
        BadgeUser.Text = _user?.Name ?? "";
        _codesPerRack = 1;
        UpdateCodesLabel();
        _racks.Clear();
        var allowed = UserService.GetAllowedRacks(_userProfile!);
        foreach (var rack in allowed)
        {
            var vm = new RackViewModel(rack);
            vm.PropertyChanged += (_, _) => RefreshSelectionBar();
            _racks.Add(vm);
        }
        AllowedCountLabel.Text = $"({allowed.Count} available to you)";
        RefreshSelectionBar();
        Show(PageRackSelect);
    }

    private async void OnGenerate()
    {
        var selected = _racks.Where(v => v.IsSelected).Select(v => v.Rack).ToList();
        if (!selected.Any()) return;

        // ── Floating-key allocation: pair each selected rack with the next ──
        // ── physical key currently in the cabinet (not rack.LockId).        ──
        var availableSlots = SlotService.GetAvailableKeys();
        if (availableSlots.Count < selected.Count)
        {
            ShowError(
                $"Only {availableSlots.Count} key(s) currently in the cabinet, " +
                $"but {selected.Count} rack(s) requested.");
            return;
        }

        var assignments = selected
            .Zip(availableSlots, (rack, slot) => (rack, slot))
            .ToList();

        _state = KioskState.Generating;
        StepText.Text = "Generating…";
        int total = selected.Count * _codesPerRack;
        GeneratingMsg.Text = $"Contacting PYCONNECT for {selected.Count} key(s) × {_codesPerRack} code(s) = {total} total…";
        Show(PageGenerating);

        string ticket = $"Kiosk-{(_user?.Name ?? "").Replace(" ", "")}-{DateTime.Now:yyyyMMddHHmmss}";

        // Generate OTPs against each ALLOCATED slot's CurrentPyKey, not rack.LockId.
        var pyKeyAssignments = assignments
            .Select(a => (a.rack, pyKey: a.slot.CurrentPyKey ?? ""))
            .ToList();

        List<OtcResult> results;
        try { results = await PyConnectService.GenerateForAssignmentsAsync(pyKeyAssignments, ticket, _codesPerRack); }
        catch (Exception ex) { ShowError($"PYCONNECT error:\n{ex.Message}"); return; }

        _lastPrintJob = new PrintJob(
            EscortName: _escort?.Name ?? "Unknown",
            UserName:   _user?.Name ?? "Unknown",
            Ticket:     ticket,
            IssuedAt:   DateTime.Now,
            Results:    results,
            CodesPerRack: _codesPerRack
        );

        ActivityLog.CodeGenerated(_escort?.Name ?? "Unknown", _user?.Name ?? "Unknown", results);
        PrintReceipt();

        // Open each allocated slot, parallel to results
        for (int i = 0; i < results.Count; i++)
        {
            if (!results[i].Success) continue;
            var slot = assignments[i].slot;

            bool opened = await _locker.UnlockSlotAsync(slot.SlotNumber);
            if (opened)
            {
                SlotService.ClearSlot(slot.SlotNumber);
                ActivityLog.KeyDrawn(
                    escort:     _escort?.Name ?? "Unknown",
                    user:       _user?.Name ?? "Unknown",
                    rackTag:    results[i].Rack.Tag,
                    pyKey:      slot.CurrentPyKey ?? "",
                    slotNumber: slot.SlotNumber);
            }
        }

        ShowCodes(results);
    }


    private void ShowCodes(List<OtcResult> results)
    {
        _state = KioskState.DisplayCodes;
        StepText.Text = "Access Granted";
        CodesPanel.ItemsSource = results.Select(r => new OtcCardViewModel(r)).ToList();
        Show(PageCodes);
        StartCountdown(KioskSettings.CodeDisplaySec, CodesTimer, GoIdle);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  RETURN FLOW
    // ═════════════════════════════════════════════════════════════════════════

    private void GoReturnConfirm()
    {
        string userName = _user?.Name ?? "";

        // Check what key this user currently holds
        var held = ActivityLog.GetHeldKey(userName);
        if (held == null)
        {
            ShowError($"{userName} does not currently have an active key draw on record.\n\n" +
                      $"If you believe this is an error, please contact your administrator.");
            return;
        }

        // Find an empty slot to accept the return
        _returnTargetSlot = SlotService.FindEmptySlot();
        if (_returnTargetSlot == null)
        {
            ShowError("No empty cabinet slots available.\n\nPlease contact your administrator.");
            return;
        }

        _returnPyKey = held.Value.PyKey;
        _returnRackTag = held.Value.RackTag;

        // Populate the return page UI
        var rack = RackService.ByTag(held.Value.RackTag);
        ReturnEscortName.Text = _escort?.Name ?? "";
        ReturnUserName.Text = userName;
        ReturnRackName.Text = rack?.DisplayName ?? held.Value.RackTag;
        ReturnRackTag.Text = held.Value.RackTag;
        ReturnSlotLabel.Text = $"Cabinet Slot {_returnTargetSlot.SlotNumber}";

        _state = KioskState.ReturnConfirm;
        StepText.Text = "Return Key";
        Show(PageReturn);
    }

    private async void BtnOpenReturnSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_returnTargetSlot == null || _returnPyKey == null) return;

        BtnOpenReturnSlot.IsEnabled = false;
        BtnOpenReturnSlot.Content = "Opening slot…";

        bool opened = await _locker.UnlockSlotAsync(_returnTargetSlot.SlotNumber);

        if (!opened)
        {
            ShowError("Failed to open cabinet slot.\n\nPlease contact your administrator.");
            return;
        }

        // Mark slot as occupied with the returned key
        SlotService.OccupySlot(_returnTargetSlot.SlotNumber, _returnPyKey);

        // Log the return
        ActivityLog.KeyReturned(
            escort: _escort?.Name ?? "Unknown",
            user: _user?.Name ?? "Unknown",
            rackTag: _returnRackTag ?? "",
            pyKey: _returnPyKey,
            slotNumber: _returnTargetSlot.SlotNumber
        );

        // Show success then reset
        ShowReturnSuccess();
    }

    private void ShowReturnSuccess()
    {
        // Reuse the codes page banner area — just show a green success message
        _state = KioskState.DisplayCodes;
        StepText.Text = "Key Returned";

        CodesPanel.ItemsSource = new List<object>(); // empty

        // Swap the banner text temporarily using a dispatcher trick
        // (reuse existing PageCodes — the banner is already green ✅)
        Show(PageCodes);

        // Manually update the banner to say "KEY RETURNED"
        // (CodesTimer is the countdown label)
        StartCountdown(10, CodesTimer, GoIdle);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ERROR
    // ═════════════════════════════════════════════════════════════════════════

    private void ShowError(string message)
    {
        StopCamera(); StopCountdown();
        _state = KioskState.Error;
        StepText.Text = "Error";
        ErrorMsg.Text = message;
        Show(PageError);
        StartCountdown(KioskSettings.ErrorResetSec, ErrorTimer, GoIdle);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CODE QUANTITY STEPPER
    // ═════════════════════════════════════════════════════════════════════════

    private void BtnCodesDown_Click(object s, RoutedEventArgs e)
    { if (_codesPerRack > MinCodes) _codesPerRack--; UpdateCodesLabel(); }

    private void BtnCodesUp_Click(object s, RoutedEventArgs e)
    { if (_codesPerRack < MaxCodes) _codesPerRack++; UpdateCodesLabel(); }

    private void UpdateCodesLabel()
    {
        CodesPerRackLabel.Text = _codesPerRack.ToString();
        BtnCodesDown.IsEnabled = _codesPerRack > MinCodes;
        BtnCodesUp.IsEnabled = _codesPerRack < MaxCodes;
        int sel = _racks.Count(v => v.IsSelected);
        CodesTotalLabel.Text = sel > 0 ? $"= {sel * _codesPerRack} code(s) total" : "";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PRINTING
    // ═════════════════════════════════════════════════════════════════════════

    private void PrintReceipt()
    {
        if (_lastPrintJob == null) return;
        try { ReceiptPrinter.Print(_lastPrintJob); }
        catch (Exception ex)
        { System.Diagnostics.Debug.WriteLine($"[PRINT] {ex.Message}"); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BUTTON HANDLERS
    // ═════════════════════════════════════════════════════════════════════════

    private void BtnCancel_Click(object s, RoutedEventArgs e) => GoIdle();
    private void BtnGenerate_Click(object s, RoutedEventArgs e) => OnGenerate();
    private void BtnReprint_Click(object s, RoutedEventArgs e) => PrintReceipt();

    private void Window_KeyDown(object s, KeyEventArgs e)
    { if (e.Key == Key.Escape) Close(); }

    protected override void OnClosed(EventArgs e)
    {
        _camera.FrameReady -= OnFrame;
        _camera.Dispose();
        _escortFace?.Dispose();
        _userFace?.Dispose();
        _locker.Dispose();
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private void StopCamera() => _camera.Stop();

    private void RefreshSelectionBar()
    {
        int n = _racks.Count(v => v.IsSelected);
        SelectionCount.Text = n == 0 ? "No racks selected" : $"{n} rack{(n > 1 ? "s" : "")} selected";
        BtnGenerate.IsEnabled = n > 0;
        UpdateCodesLabel();
    }

    private void Show(UIElement page)
    {
        foreach (var p in new UIElement[]
            { PageIdle, PageEscortScan, PageNricEntry, PageUserScan,
              PageRackSelect, PageGenerating, PageCodes, PageReturn, PageError })
            p.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    private void StartScanTimer(System.Windows.Controls.TextBlock label, Action onTimeout)
    {
        int left = FaceSettings.ScanTimeoutSec;
        label.Text = $"Timeout in {left}s";
        _scanTimer?.Stop();
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scanTimer.Tick += (_, _) =>
        {
            left--;
            label.Text = $"Timeout in {left}s";
            if (left <= 0) { _scanTimer.Stop(); StopCamera(); onTimeout(); }
        };
        _scanTimer.Start();
    }

    private void StartCountdown(int seconds, System.Windows.Controls.TextBlock label, Action onDone)
    {
        _countdownSec = seconds;
        label.Text = $"Resets in {_countdownSec}s";
        _countdown?.Stop();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            _countdownSec--;
            label.Text = $"Resets in {_countdownSec}s";
            if (_countdownSec <= 0) { _countdown.Stop(); onDone(); }
        };
        _countdown.Start();
    }

    private void StopCountdown() => _countdown?.Stop();
}