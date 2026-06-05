using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PyConnectKiosk;

public class RackCheckItem
{
    public string Tag { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsChecked { get; set; }
    public string Label => $"{Tag}  —  {DisplayName}";
}

public class AdminUserViewModel
{
    public string Name { get; set; } = "";
    public List<string> AllowedRacks { get; set; } = new();
    public string LastFourNric { get; set; } = "";
    public string RackSummary => AllowedRacks.Count == 0
                                 ? "(no racks)" : string.Join(", ", AllowedRacks);
}

public class PhotoEntry
{
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
}

public partial class AdminWindow : Window
{
    private readonly ObservableCollection<AdminUserViewModel> _users = new();
    private readonly ObservableCollection<RackCheckItem> _rackItems = new();
    private AdminUserViewModel? _editingUser;
    private string? _pendingEscortPhotoPath;
    private string? _pendingUserPhotoPath;

    public AdminWindow()
    {
        InitializeComponent();
        LoadRacks();        // loads racks.json → also calls BuildRackCheckboxes()
        LoadUsers();
        RefreshEscortList();
        RefreshUserPhotoList();
        RefreshActivityLog();
    }

    // ── Rack checkboxes ───────────────────────────────────────────────────────

    private void BuildRackCheckboxes()
    {
        _rackItems.Clear();
        foreach (var rack in RackCatalogue.All)
            _rackItems.Add(new RackCheckItem { Tag = rack.Tag, DisplayName = rack.DisplayName });
        RackCheckList.ItemsSource = _rackItems;
    }

    private void ApplyRackSelection(List<string> tags)
    {
        foreach (var item in _rackItems)
            item.IsChecked = tags.Contains(item.Tag, StringComparer.OrdinalIgnoreCase);
        RackCheckList.Items.Refresh();
    }

    private List<string> GetSelectedRackTags()
        => _rackItems.Where(r => r.IsChecked).Select(r => r.Tag).ToList();

    // ── Users tab ─────────────────────────────────────────────────────────────

    private void LoadUsers()
    {
        _users.Clear();
        foreach (var p in UserService.GetAll())
            _users.Add(new AdminUserViewModel
            {
                Name = p.Name,
                AllowedRacks = new List<string>(p.AllowedRacks),
                LastFourNric = p.LastFourNric
            });
        UserList.ItemsSource = _users;
        ClearEditPanel();
        PopulateUserPhotoDropdown();
    }

    private void UserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _editingUser = UserList.SelectedItem as AdminUserViewModel;
        if (_editingUser == null) { ClearEditPanel(); return; }
        TxtUserName.Text = _editingUser.Name;
        TxtLastFourNric.Text = _editingUser.LastFourNric;
        ApplyRackSelection(_editingUser.AllowedRacks);
    }

    private void ClearEditPanel()
    {
        TxtUserName.Text = "";
        TxtLastFourNric.Text = "";
        foreach (var item in _rackItems) item.IsChecked = false;
        RackCheckList.Items.Refresh();
    }

    private void BtnAddUser_Click(object sender, RoutedEventArgs e)
    {
        var newUser = new AdminUserViewModel { Name = "New User" };
        _users.Add(newUser);
        UserList.SelectedItem = newUser;
        TxtUserName.Focus(); TxtUserName.SelectAll();
    }

    private void BtnSaveUser_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtUserName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        { MessageBox.Show("Please enter a name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        string nric = TxtLastFourNric.Text.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(nric) && nric.Length != 4)
        { MessageBox.Show("Last 4 NRIC must be exactly 4 characters.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var racks = GetSelectedRackTags();
        bool isNew = _editingUser == null;

        // Capture old racks BEFORE updating
        string oldRacks = _editingUser != null
            ? string.Join(", ", _editingUser.AllowedRacks)
            : "";

        if (_editingUser != null)
        {
            _editingUser.Name = name;
            _editingUser.AllowedRacks = racks;
            _editingUser.LastFourNric = nric;
        }
        else
        {
            var u = new AdminUserViewModel { Name = name, AllowedRacks = racks, LastFourNric = nric };
            _users.Add(u);
            _editingUser = u;
        }

        PersistUsersJson();
        UserList.Items.Refresh();

        // ── Build detailed log message ─────────────────────────────────────────
        string nricPart = nric != "" ? $" NRIC:{nric}" : "";
        string rackSummary = racks.Count > 0 ? string.Join(", ", racks) : "no racks";
        string rackChange;

        if (isNew)
        {
            // Brand new user — just show assigned racks
            rackChange = $" | Racks: {rackSummary}";
        }
        else
        {
            // Existing user — show what changed
            string oldSummary = oldRacks != "" ? oldRacks : "no racks";
            rackChange = oldSummary == rackSummary
                ? $" | Racks: {rackSummary} (unchanged)"
                : $" | Racks: {oldSummary} → {rackSummary}";
        }

        ActivityLog.AdminAction($"{(isNew ? "Added" : "Updated")} user '{name}'{nricPart}{rackChange}");
        // ─────────────────────────────────────────────────────────────────────

        MessageBox.Show("Saved successfully.", "Admin Portal", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshActivityLog();
        PopulateUserPhotoDropdown();
    }

    private void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (_editingUser == null) return;
        string n = _editingUser.Name;
        if (MessageBox.Show($"Delete user '{n}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _users.Remove(_editingUser); _editingUser = null; ClearEditPanel();
        PersistUsersJson();
        ActivityLog.AdminAction($"Deleted user '{n}'");
        RefreshActivityLog(); PopulateUserPhotoDropdown();
    }

    private void PersistUsersJson()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataFiles.UsersJson)!);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(DataFiles.UsersJson, JsonSerializer.Serialize(
                _users.Select(u => new { name = u.Name, allowed_racks = u.AllowedRacks, last_four_nric = u.LastFourNric }), opts));
            UserService.Reload();
        }
        catch (Exception ex) { MessageBox.Show($"Failed to save:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ── Escort Photos tab ────────────────────────────────────────────────────

    private void BtnUploadEscortPhoto_Click(object sender, RoutedEventArgs e)
    {
        _pendingEscortPhotoPath = PickImageFile();
        if (_pendingEscortPhotoPath != null && string.IsNullOrWhiteSpace(TxtEscortName.Text))
            TxtEscortName.Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                Path.GetFileNameWithoutExtension(_pendingEscortPhotoPath).Replace("_", " "));
    }

    private void BtnEnrolEscort_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtEscortName.Text.Trim();
        EnrolPhoto(name, _pendingEscortPhotoPath, FaceSettings.EscortFacesDir, () =>
        {
            TxtEscortName.Text = ""; _pendingEscortPhotoPath = null; RefreshEscortList();
            ActivityLog.AdminAction($"Enrolled escort photo: '{name}'"); RefreshActivityLog();
        });
    }

    private void BtnRefreshEscorts_Click(object sender, RoutedEventArgs e) => RefreshEscortList();

    private void BtnDeleteEscortPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string f)
            DeletePhoto(f, FaceSettings.EscortFacesDir, () =>
            { RefreshEscortList(); ActivityLog.AdminAction($"Deleted escort photo: '{f}'"); RefreshActivityLog(); });
    }

    private void RefreshEscortList()
        => EscortPhotoList.ItemsSource = GetPhotoEntries(FaceSettings.EscortFacesDir);

    // ── User Photos tab ───────────────────────────────────────────────────────

    // Populate the dropdown from the registered users list
    private void PopulateUserPhotoDropdown()
    {
        CmbUserPhotoSelect.ItemsSource = _users.Select(u => u.Name).ToList();
        CmbUserPhotoSelect.SelectedIndex = -1;
    }

    private void BtnUploadUserPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (CmbUserPhotoSelect.SelectedItem == null)
        { MessageBox.Show("Select a user from the dropdown first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _pendingUserPhotoPath = PickImageFile();
    }

    private void BtnEnrolUser_Click(object sender, RoutedEventArgs e)
    {
        string? name = CmbUserPhotoSelect.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(name))
        { MessageBox.Show("Select a user from the dropdown first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        EnrolPhoto(name, _pendingUserPhotoPath, FaceSettings.UserFacesDir, () =>
        {
            CmbUserPhotoSelect.SelectedIndex = -1; _pendingUserPhotoPath = null; RefreshUserPhotoList();
            ActivityLog.AdminAction($"Enrolled user photo: '{name}'"); RefreshActivityLog();
        });
    }

    private void BtnRefreshUsers_Click(object sender, RoutedEventArgs e) => RefreshUserPhotoList();

    private void BtnDeleteUserPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string f)
            DeletePhoto(f, FaceSettings.UserFacesDir, () =>
            { RefreshUserPhotoList(); ActivityLog.AdminAction($"Deleted user photo: '{f}'"); RefreshActivityLog(); });
    }

    private void RefreshUserPhotoList()
        => UserPhotoList.ItemsSource = GetPhotoEntries(FaceSettings.UserFacesDir);

    // ── Activity Log tab ──────────────────────────────────────────────────────

    private void RefreshActivityLog()
        => ActivityLogGrid.ItemsSource = ActivityLog.Load();

    private void BtnRefreshLog_Click(object sender, RoutedEventArgs e) => RefreshActivityLog();

    // ── Shared photo helpers ──────────────────────────────────────────────────

    private static string? PickImageFile()
    {
        var dlg = new OpenFileDialog { Title = "Select Face Photo", Filter = "Image Files|*.jpg;*.jpeg;*.png" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static void EnrolPhoto(string name, string? src, string dir, Action onSuccess)
    {
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("Enter a name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) { MessageBox.Show("Choose a photo first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(src).ToLower();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") ext = ".jpg";
            File.Copy(src, Path.Combine(dir, name.ToLowerInvariant().Replace(" ", "_") + ext), overwrite: true);
            MessageBox.Show($"'{name}' enrolled.\n\nThe kiosk will pick up the new face automatically within ~1 second.", "Enrolled", MessageBoxButton.OK, MessageBoxImage.Information);
            onSuccess();
        }
        catch (Exception ex) { MessageBox.Show($"Failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void DeletePhoto(string fileName, string dir, Action onSuccess)
    {
        if (MessageBox.Show($"Delete '{fileName}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { string p = Path.Combine(dir, fileName); if (File.Exists(p)) File.Delete(p); onSuccess(); }
        catch (Exception ex) { MessageBox.Show($"Failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static List<PhotoEntry> GetPhotoEntries(string dir)
    {
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir)
            .Where(f => Path.GetExtension(f).ToLower() is ".jpg" or ".jpeg" or ".png")
            .OrderBy(f => f)
            .Select(f => new PhotoEntry
            {
                DisplayName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Path.GetFileNameWithoutExtension(f).Replace("_", " ")),
                FileName = Path.GetFileName(f)
            }).ToList();
    }


    // =========================================================================
    //  RACKS TAB
    // =========================================================================

    // Holds the current rack list displayed in the sidebar
    private List<RackItem> _racks = new();
    private RackItem? _editingRack;

    private void LoadRacks()
    {
        _racks = RackService.GetAll();
        RackList.ItemsSource = null;
        RackList.ItemsSource = _racks;
        ClearRackEditPanel();

        // Rebuild the rack checkboxes in the Users tab so they reflect
        // any newly added or removed racks
        BuildRackCheckboxes();
    }

    private void RackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _editingRack = RackList.SelectedItem as RackItem;
        if (_editingRack == null) { ClearRackEditPanel(); return; }

        TxtRackTag.Text = _editingRack.Tag;
        TxtRackLockId.Text = _editingRack.LockId;
        TxtRackDisplayName.Text = _editingRack.DisplayName;
        TxtRackRow.Text = _editingRack.Row;
    }

    private void ClearRackEditPanel()
    {
        TxtRackTag.Text = "";
        TxtRackLockId.Text = "";
        TxtRackDisplayName.Text = "";
        TxtRackRow.Text = "";
        _editingRack = null;
    }

    private void BtnAddRack_Click(object sender, RoutedEventArgs e)
    {
        // Create a placeholder so admin can fill in the details
        ClearRackEditPanel();
        TxtRackTag.Focus();
    }

    private void BtnSaveRack_Click(object sender, RoutedEventArgs e)
    {
        string tag = TxtRackTag.Text.Trim().ToUpperInvariant();
        string lockId = TxtRackLockId.Text.Trim();
        string name = TxtRackDisplayName.Text.Trim();
        string row = TxtRackRow.Text.Trim();

        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(lockId) || string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Tag, Lock ID and Display Name are required.",
                            "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newRack = new RackItem(tag, lockId, name, row);

        if (_editingRack != null)
        {
            // Replace the edited rack in the list
            int idx = _racks.IndexOf(_editingRack);
            if (idx >= 0) _racks[idx] = newRack;
        }
        else
        {
            // Check for duplicate tag
            if (_racks.Any(r => r.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"A rack with tag '{tag}' already exists.",
                                "Duplicate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _racks.Add(newRack);
        }

        RackService.Save(_racks);
        ActivityLog.AdminAction($"Saved rack '{tag}' ({name}, Lock: {lockId})");
        MessageBox.Show("Rack saved. Restart the kiosk to apply changes.",
                        "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadRacks();
        RefreshActivityLog();
    }

    private void BtnDeleteRack_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRack == null) return;
        string tag = _editingRack.Tag;
        if (MessageBox.Show($"Delete rack '{tag}'?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _racks.Remove(_editingRack);
        RackService.Save(_racks);
        ActivityLog.AdminAction($"Deleted rack '{tag}'");
        LoadRacks();
        RefreshActivityLog();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}