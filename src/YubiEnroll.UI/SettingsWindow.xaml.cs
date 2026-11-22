using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class SettingsWindow : Window
{
    public event Action? PrerequisitesOpened;
    internal event Action<string>? ActivityRecorded;
    private void SettingsTab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source)) return;
        if (AppSettingsTab != null && e.RemovedItems.Contains(AppSettingsTab) && !ConfirmLeavePreferences()) { SettingsTabs.SelectedItem = AppSettingsTab; return; }
        if (Feedback != null) Feedback.Text = "";
        if (AboutTab?.IsSelected == true && e.Source == SettingsTabs) PrerequisitesOpened?.Invoke();
        if (RegistrationTab?.IsSelected == true && RegistrationHost?.Content is RegistrationLogView log) log.RefreshRecords();
    }
    private readonly RegistrationJournal _journal;
    private readonly DeskPolicy? _policy;
    private bool TenantsManaged => _policy?.ManagesTenants == true;
    private bool ProfilesManaged => _policy?.ManagesProfiles == true;
    private bool TerminalManaged => _policy?.EmbeddedTerminal.HasValue == true;
    private bool ImportLocked => TenantsManaged && ProfilesManaged;
    private string ImportScope => TenantsManaged ? "profiles" : ProfilesManaged ? "tenants" : "tenants and profiles";
    private static void RequireEditable(bool managed, string section)
    {
        if (managed) throw new InvalidOperationException(section + " managed by your organization.");
    }
    private readonly string _path;
    private ConfigSnapshot? _importSnapshot;
    private string? _importText;
    private void TenantFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (TenantList?.ItemsSource == null) return;
        System.Windows.Data.CollectionViewSource.GetDefaultView(TenantList.ItemsSource).Filter = item => item is Provider p && p.Name.Contains(TenantFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    private Window DialogOwner => _transferWindow ?? Owner ?? this;
    private bool _terminal = true;
    public bool EmbeddedTerminal { get => _terminal; set { _terminal = _policy?.EmbeddedTerminal ?? value; EmbeddedChoice.IsChecked = _terminal; ExternalChoice.IsChecked = !_terminal; RefreshDirty(); } }
    private void ProfileFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (ProfileList?.ItemsSource != null)
            System.Windows.Data.CollectionViewSource.GetDefaultView(ProfileList.ItemsSource).Filter = item => item is ProfileRow row && row.Name.Contains(ProfileFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    private void Row_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBox list && e.OriginalSource is DependencyObject src && ItemsControl.ContainerFromElement(list, src) is ListBoxItem row && row.IsSelected) OpenRow(list);
    }
    private void Row_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is ListBox list && list.SelectedItem != null) { e.Handled = true; OpenRow(list); }
    }
    private void OpenRow(ListBox list)
    {
        if (list == TenantList && TenantsManaged || list == ProfileList && ProfilesManaged) return;
        if (list.SelectedItem is Provider tenant) EditTenant(tenant.Name);
        else if (list.SelectedItem is ProfileRow profile) EditProfile(profile.Name);
    }
    private void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (ImportLocked || (sender == TenantTransfer && TenantsManaged) || (sender == ProfileTransfer && ProfilesManaged)) return;
        var content = TransferHolder.Content; TransferHolder.Content = null;
        var scroll = new ScrollViewer { Content = content, Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new DockPanel(); var notice = new TextBlock { Margin = new Thickness(18,4,18,8), TextWrapping = TextWrapping.Wrap }; notice.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text") { Source = Feedback }); DockPanel.SetDock(notice, Dock.Bottom); panel.Children.Add(notice); panel.Children.Add(scroll); Feedback.Text = "";
        var dialog = new Window { Title = $"Import / export {ImportScope}", Owner = DialogOwner, Content = panel, Width = 620, Height = 530, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try { _transferWindow = dialog; dialog.ShowDialog(); }
        finally { _transferWindow = null; CancelImport_Click(this, e); scroll.Content = null; TransferHolder.Content = content; }
    }
    private Window? _transferWindow;
    private void RefreshDirty()
    {
        if (_journal == null) return;
        var dirty = RegDirty || ActivityDirty || (!TerminalManaged && _terminal != (EmbeddedChoice.IsChecked == true));
        SaveLogSettingsButton.IsEnabled = dirty;
        DirtyHint.Text = dirty ? "Unsaved changes" : "";
        DefaultFolderButton.Visibility = FolderPath.Text == _journal.DefaultLogDirectory ? Visibility.Collapsed : Visibility.Visible;
        ActivityDefault.Visibility = ActivityFolder.Text == _activity.DefaultDirectory ? Visibility.Collapsed : Visibility.Visible;
    }
    private bool RegDirty => !_journal.IsManaged && (EnabledChoice.IsChecked != _journal.Preferences.Enabled || SavePinChoice.IsChecked != _journal.Preferences.SaveGeneratedPin || FolderPath.Text != _journal.Preferences.Directory);
    private bool ActivityDirty => !_activity.IsManaged && (SaveActivity.IsChecked != _activity.Preferences.Enabled || ActivityFolder.Text != _activity.Preferences.Directory || ActivityDays.Text != _activity.Preferences.RetentionDays.ToString());
    internal bool ConfirmLeavePreferences()
    {
        if (!SaveLogSettingsButton.IsEnabled) return true;
        var answer = MessageBox.Show(DialogOwner, "You have unsaved app settings. Save your changes before leaving?\n\nYes: save changes. No: discard changes. Cancel: stay here.", "Unsaved app settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Yes) { SaveAll_Click(this, new RoutedEventArgs()); return !SaveLogSettingsButton.IsEnabled; }
        if (answer != MessageBoxResult.No) return false;
        RestorePreferences();
        return true;
    }
    private void RestorePreferences()
    {
        EnabledChoice.IsChecked = _journal.Preferences.Enabled; SavePinChoice.IsChecked = _journal.Preferences.SaveGeneratedPin; FolderPath.Text = _journal.Preferences.Directory;
        SaveActivity.IsChecked = _activity.Preferences.Enabled; ActivityFolder.Text = _activity.Preferences.Directory; ActivityDays.Text = _activity.Preferences.RetentionDays.ToString();
        EmbeddedTerminal = _terminal; LogSettingsFeedback.Text = ActivitySettingsFeedback.Text = ""; RefreshDirty();
    }
    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (ActivityDirty && (!int.TryParse(ActivityDays.Text, out int days) || days is < 1 or > 3650)) { ActivitySettingsFeedback.Text = "Choose a retention period of 1–3650 days."; return; }
        if (RegDirty) { SaveLogSettings_Click(sender,e); if (RegDirty) return; }
        if (ActivityDirty) { SaveActivity_Click(sender,e); if (ActivityDirty) return; }
        if (!TerminalManaged) _terminal = EmbeddedChoice.IsChecked == true;
        RefreshDirty();
    }
    private sealed record Pill(string Label, bool Warning = false);
    private sealed record ProfileRow(string Name, List<Pill> Pills, string Compatibility, string Details);
    private readonly ActivityJournal _activity;
    private KeyStatus? _profileKey;
    public bool Changed { get; private set; }

    public SettingsWindow(string path, string executable, string version, RegistrationJournal? registrationJournal = null, ActivityJournal? activity = null, KeyStatus? profileKey = null, DeskPolicy? policy = null)
    {
        _policy = policy;
        _path = path; _profileKey = profileKey; _activity = activity ?? new ActivityJournal(policy: policy?.Activity);
        InitializeComponent();
        _journal = registrationJournal ?? new RegistrationJournal(RegistrationJournal.DefaultSettingsPath, policy: policy?.Registration);
        SaveActivity.IsChecked = _activity.Preferences.Enabled; ActivityFolder.Text = _activity.Preferences.Directory;
        ActivityDays.Text = _activity.Preferences.RetentionDays.ToString(); ActivitySettingsFeedback.Text = _activity.Error ?? "";
        EnabledChoice.IsChecked = _journal.Preferences.Enabled;
        SavePinChoice.IsChecked = _journal.Preferences.SaveGeneratedPin;
        FolderPath.Text = _journal.Preferences.Directory;
        DefaultFolderButton.IsEnabled = _journal.DefaultLogDirectory.Length > 0;
        DefaultFolderButton.ToolTip = _journal.DefaultLogDirectory;
        LogSettingsFeedback.Text = _journal.SettingsError ?? "";
        var log = new RegistrationLogView(_journal);

        RegistrationHost.Content = log;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
        VersionInfo.Text = version.StartsWith("YubiEnroll", StringComparison.OrdinalIgnoreCase) ? version : "YubiEnroll: " + version;
        RuntimeVersion.Text = Environment.Version.ToString();
        Attempt(RefreshLists);
        foreach (var box in new System.Windows.Controls.Primitives.ToggleButton[] { SaveActivity, EnabledChoice, SavePinChoice, EmbeddedChoice, ExternalChoice }) { box.Checked += (_,_) => RefreshDirty(); box.Unchecked += (_,_) => RefreshDirty(); }
        foreach (var field in new[] { ActivityFolder, FolderPath, ActivityDays }) field.TextChanged += (_,_) => RefreshDirty();
        EmbeddedTerminal = true;
        var managed = new List<string>();
        if (TenantsManaged) { TenantActions.Visibility = Visibility.Collapsed; managed.Add("tenants"); }
        if (ProfilesManaged) { ProfileActions.Visibility = Visibility.Collapsed; managed.Add("configuration profiles"); }
        if (_activity.IsManaged) { ActivityControls.IsEnabled = false; managed.Add("activity logging"); }
        if (_journal.IsManaged) { RegistrationControls.IsEnabled = false; managed.Add("registration logging"); }
        if (TerminalManaged) { TerminalControls.IsEnabled = false; managed.Add("terminal preference"); }
        if (managed.Count > 0)
        {
            PolicyNotice.Text = "Managed by your organization: " + string.Join(", ", managed) + ".";
            PolicyNotice.Visibility = Visibility.Visible;
        }
        void SetTransfer(Button button, Border badge, bool locked, string section)
        {
            if (!TenantsManaged && !ProfilesManaged) return;
            button.Content = $"Import / export {section}…";
            button.IsEnabled = !locked;
            button.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            badge.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        }
        SetTransfer(TenantTransfer, TenantPolicyBadge, TenantsManaged, "tenants");
        SetTransfer(ProfileTransfer, ProfilePolicyBadge, ProfilesManaged, "profiles");
        if (!ImportLocked && (TenantsManaged || ProfilesManaged))
        {
            TransferIntro.Text = $"Transfer local {ImportScope} using a YubiEnroll CLI TOML file.";
            ExportInstructions.Text = $"Save local {ImportScope} to a file.";
            TransferDetails.Text = $"Only local {ImportScope} are included. Managed settings and authentication data are excluded.";
            ImportInstructions.Text = $"Import {ImportScope} only. The managed list stays unchanged.";
            ImportWarning.Text = $"This replaces only local {ImportScope}. A backup of the current file is saved first.";
            ImportButton.Content = $"Replace {ImportScope}…";
        }
    }

    private void ActivityFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the activity log folder" };
        if (Directory.Exists(ActivityFolder.Text)) picker.InitialDirectory = ActivityFolder.Text;
        if (picker.ShowDialog(DialogOwner) == true) ActivityFolder.Text = picker.FolderName;
    }
    private void DefaultActivityFolder_Click(object sender, RoutedEventArgs e) => ActivityFolder.Text = _activity.DefaultDirectory;
    private void SaveActivity_Click(object sender, RoutedEventArgs e)
    {
        if (_activity.IsManaged) return;
        if (!int.TryParse(ActivityDays.Text, out int days) || days is < 1 or > 3650)
        { ActivitySettingsFeedback.Text = "Choose a retention period of 1–3650 days."; return; }
        try { _activity.Configure(SaveActivity.IsChecked == true, ActivityFolder.Text, days); ActivityDays.Text = days.ToString(); ActivitySettingsFeedback.Text = _activity.Error ?? "Activity settings saved."; ActivityRecorded?.Invoke("Activity logging settings changed."); }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex)) { ActivitySettingsFeedback.Text = "Could not save. Choose a writable folder and try again."; }
    }
    private void BrowseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the registration log folder", Multiselect = false };
        if (Directory.Exists(FolderPath.Text)) picker.InitialDirectory = FolderPath.Text;
        if (picker.ShowDialog(DialogOwner) == true) SelectLogFolder(picker.FolderName);
    }

    private void DefaultLogFolder_Click(object sender, RoutedEventArgs e) => SelectLogFolder(_journal.DefaultLogDirectory);

    private void SelectLogFolder(string folder)
    {
        FolderPath.Text = folder;
        LogSettingsFeedback.Text = folder.Equals(_journal.Preferences.Directory, StringComparison.OrdinalIgnoreCase) ? ""
            : "Save to use this folder. Existing records stay in their original folder.";
    }

    private void SaveLogSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_journal.IsManaged) return;
        try
        {
            _journal.Configure(EnabledChoice.IsChecked == true, FolderPath.Text, SavePinChoice.IsChecked == true);
            FolderPath.Text = _journal.Preferences.Directory;
            LogSettingsFeedback.Text = "Logging settings saved."; ActivityRecorded?.Invoke("Registration logging settings changed.");
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex))
        {
            LogSettingsFeedback.Text = _journal.PendingCount > 0
                ? "Restore access to the original folder and retry saving the pending results in Registration log before changing folders."
                : "Could not save your settings. Choose a writable log folder and try again. Your previous settings are unchanged.";
        }
    }

    internal void SetRequirements(RequirementReport report)
    {
        Set(EnrollmentStatusPill, EnrollmentStatus, VersionInfo, report.Enrollment);
        Set(ManagerStatusPill, ManagerStatus, ManagerInfo, report.Manager);
        void Set(Border pill, TextBlock status, TextBlock version, RequirementCheck check)
        {
            status.Text = check.Ready ? "Installed" : check.Detail == "Not installed" ? "Missing" : check.Detail.StartsWith("Unsupported") ? "Update required" : "Check required";
            var match = System.Text.RegularExpressions.Regex.Match(check.Detail, @"version (\d+\.\d+\.\d+)");
            var action = pill == EnrollmentStatusPill ? EnrollmentAction : ManagerAction;
            action.Visibility = check.Ready ? Visibility.Collapsed : Visibility.Visible; action.Content = check.Detail.StartsWith("Unsupported") ? "Update" : "Download";
            version.Text = match.Success ? match.Groups[1].Value : "—";
            ((Border)version.Parent).SetValue(VisibilityProperty, match.Success ? Visibility.Visible : Visibility.Collapsed);
            pill.ToolTip = check.Detail;
            if (!check.Ready) { pill.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 242, 225)); status.Foreground = System.Windows.Media.Brushes.SaddleBrown; }
            else { pill.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#EAF4D6")!; status.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#305A14")!; }
        }
    }

    private void Attempt(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        { Feedback.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Feedback.Text = "Cannot access the configuration. Check permissions, close other sessions and try again."; }
    }

    private void RefreshLists()
    {
        var text = ConfigSnapshot.Read(_path).Text ?? "";
        var config = ConfigReader.Parse(text);
        TenantList.ItemsSource = config.Providers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        TenantFilter_Changed(this, null!);
        ProfileList.ItemsSource = config.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => DescribeProfile(ProfileConfigWriter.Read(text, p.Name), _profileKey)).ToArray();
        ProfileFilter_Changed(this,null!); Feedback.Text = "";
    }
    internal void UpdateProfileKey(KeyStatus? key)
    {
        if (key == _profileKey) return;
        _profileKey = key; Attempt(RefreshLists);
    }
    private static ProfileRow DescribeProfile(ProfileSetup p, KeyStatus? key)
    {
        static string Flag(bool? value) => value switch { true => "On", false => "Off", _ => "Not specified" };
        List<Pill> pills = [new(p.Reset switch { true => "⚠ Clears passkeys on key", false => "Keeps passkeys on key", _ => "Reset not specified" }, p.Reset == true),
            new(p.RandomPin switch { true => p.RandomPinLength is int length ? $"Generated PIN · {length} chars" : "Generated PIN · length not specified", false => "No PIN generated", _ => "Initial PIN not specified" })];
        if (p.ForcePinChange == true) pills.Add(new("PIN change required"));
        var policy = p.MinPinLength.HasValue || p.ForcePinChange == true || p.RequireAlwaysUv == true;
        var needs = new List<string>();
        if (p.MinPinLength.HasValue) needs.Add("configurable PIN minimum");
        if (p.ForcePinChange == true) needs.Add("PIN change at first use");
        if (p.RequireAlwaysUv == true) needs.Add("Always UV");
        if (p.RequireEa == true) needs.Add("enterprise attestation");
        var compatibility = needs.Count == 0 ? "Compatibility · FIDO2 PIN and passkey storage required" : "Requires " + string.Join(" · ", needs);
        if (key is { Capabilities: not null })
        {
            var result = ProfileCompatibility.Evaluate(p, key.Capabilities, key.Pin);
            compatibility = result.Compatible ? "Compatible with " + key.Key.Model : "Not compatible with " + key.Key.Model + " · " + string.Join(" ", result.Issues);
        }
        else if (key != null) compatibility = "Compatibility not verified · " + key.Key.Model;
        var details = $"Clear passkeys and PIN: {Flag(p.Reset)}\nGenerate initial PIN: {Flag(p.RandomPin)}\nInitial PIN length: {(p.RandomPin == false ? "Not applicable" : p.RandomPinLength?.ToString() ?? "Not specified")}\nMinimum PIN length: {p.MinPinLength?.ToString() ?? "No override"}\nChange PIN at first use: {Flag(p.ForcePinChange)}\nAlways require user verification: {Flag(p.RequireAlwaysUv)}\nRequire enterprise attestation: {Flag(p.RequireEa)}";
        if (policy) details += "\nPIN policy support depends on the model (typically firmware 5.7+ on standard YubiKey 5 Series). The selected key is checked before setup.";
        return new(p.Name, pills, compatibility, details);
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EditProfileButton != null) EditProfileButton.IsEnabled = RemoveProfileButton.IsEnabled = ProfileList.SelectedItem != null;
        if (EditTenantButton != null) EditTenantButton.IsEnabled = TenantList.SelectedItem != null;
        if (RemoveTenantButton != null) RemoveTenantButton.IsEnabled = TenantList.SelectedItem != null;
    }

    private void AddTenant_Click(object sender, RoutedEventArgs e) => EditTenant(null);
    private void EditTenant_Click(object sender, RoutedEventArgs e) => EditTenant((TenantList.SelectedItem as Provider)?.Name);
    private void EditTenant(string? name) => Attempt(() =>
    {
        RequireEditable(TenantsManaged, "Tenants are");
        var dialog = new AddTenantWindow(ConfigSnapshot.Read(_path), name) { Owner = DialogOwner };
        if (dialog.ShowDialog() != true) return;
        ActivityRecorded?.Invoke("Tenant configuration saved.");
        Changed = true;
        RefreshLists();
    });
    private void NewProfile_Click(object sender, RoutedEventArgs e) => EditProfile(null);
    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is ProfileRow row) EditProfile(row.Name);
    }
    private void EditProfile(string? name) => Attempt(() =>
    {
        RequireEditable(ProfilesManaged, "Configuration profiles are");
        var dialog = new ProfileWindow(ConfigSnapshot.Read(_path), name, _policy) { Owner = DialogOwner };
        if (dialog.ShowDialog() != true) return;
        ActivityRecorded?.Invoke("Configuration profile saved.");
        Changed = true;
        RefreshLists();
    });

    private void Export_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        RequireEditable(ImportLocked, "Tenants and profiles are");
        var text = ConfigTransfer.Export(ConfigSnapshot.Read(_path).Text ?? "", _policy);
        var picker = new SaveFileDialog { Filter = "TOML configuration|*.toml", FileName = "yubienroll-settings.toml" };
        if (picker.ShowDialog(DialogOwner) != true) return;
        if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a separate export file, not the active configuration.");
        File.WriteAllText(picker.FileName, text);
        ActivityRecorded?.Invoke($"Exported {ImportScope}.");
        Feedback.Text = "Exported to " + picker.FileName + ". No authentication cache was copied.";
    });

    private void ChooseImport_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        RequireEditable(ImportLocked, "Tenants and profiles are");
        _importText = null;
        ImportButton.IsEnabled = false;
        ImportReview.Visibility = Visibility.Collapsed;
        var picker = new OpenFileDialog { Filter = "TOML configuration|*.toml", CheckFileExists = true };
        if (picker.ShowDialog(DialogOwner) != true) return;
        PreviewImport(File.ReadAllText(picker.FileName));
        ImportFileName.Text = Path.GetFileName(picker.FileName);
    });

    internal void PreviewImport(string text)
    {
        RequireEditable(ImportLocked, "Tenants and profiles are");
        _importText = null;
        ImportButton.IsEnabled = false;
        _importSnapshot = ConfigSnapshot.Read(_path);
        var validated = ConfigTransfer.PrepareImport(_importSnapshot.Text, text, _policy);
        var config = ConfigReader.Parse(validated);
        var current = ConfigReader.Parse(_importSnapshot.Text ?? "");
        ImportReview.Visibility = Visibility.Visible;
        var rows = new List<string>();
        if (!TenantsManaged) rows.Add("New tenants: " + string.Join(", ", config.Providers.Select(x => x.Name)) +
            "\nCurrent tenants being replaced: " + string.Join(", ", current.Providers.Select(x => x.Name)));
        if (!ProfilesManaged) rows.Add("New profiles: " + string.Join(", ", config.Profiles.Select(x => x.Name)) +
            "\nCurrent profiles being replaced: " + string.Join(", ", current.Profiles.Select(x => x.Name)));
        ImportPreview.Text = string.Join("\n\n", rows);
        _importText = text;
        ImportButton.IsEnabled = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        RequireEditable(ImportLocked, "Tenants and profiles are");
        if (_importText == null || _importSnapshot == null) return;
        if (MessageBox.Show(DialogOwner, $"Replace the current {ImportScope} with the previewed configuration? An exact backup of the current file will be kept.",
            "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ApplyImport();
    });

    internal void ApplyImport()
    {
        RequireEditable(ImportLocked, "Tenants and profiles are");
        if (_importText == null || _importSnapshot == null) throw new InvalidOperationException("Preview an import first.");
        var backup = ConfigFileWriter.Save(_importSnapshot, ConfigTransfer.PrepareImport(_importSnapshot.Text, _importText, _policy));
        Changed = true;
        _importText = null;
        ActivityRecorded?.Invoke($"Imported {ImportScope}.");
        ImportButton.IsEnabled = false;
        RefreshLists();
        ImportReview.Visibility = Visibility.Collapsed;
        Feedback.Text = "Configuration imported. Reconnect to a tenant. " + (backup == null ? "No previous file needed backup." : "Backup: " + backup);
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _importText = null; _importSnapshot = null; ImportReview.Visibility = Visibility.Collapsed; ImportButton.IsEnabled = false;
    }
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => AboutTab.IsSelected = true;
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Feedback.Text = "Open in your browser: " + url; }
    }
    private void RemoveTenant_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        RequireEditable(TenantsManaged, "Tenants are");
        if (TenantList.SelectedItem is not Provider tenant) return;
        var snapshot = ConfigSnapshot.Read(_path);
        if (MessageBox.Show(DialogOwner, $"Remove '{tenant.Name}' from local configuration?\n\nThis does not delete the Entra tenant, app registration, users or key credentials. Shared profiles remain.", "Remove tenant", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ConfigFileWriter.Save(snapshot, TenantConfigWriter.Remove(snapshot.Text ?? "", tenant.Name));
        ActivityRecorded?.Invoke($"Tenant removed from local configuration · {tenant.Name}.");
        Changed = true; RefreshLists();
    });
    private void ProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.DataContext = button.DataContext; menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void RemoveProfile_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        RequireEditable(ProfilesManaged, "Configuration profiles are");
        if (ProfileList.SelectedItem is not ProfileRow profile) return;
        var snapshot = ConfigSnapshot.Read(_path);
        _policy?.CheckProfileRenameOrRemoval(snapshot, profile.Name, null);
        var result = ProfileConfigWriter.Remove(snapshot.Text ?? "", profile.Name);
        if (MessageBox.Show(DialogOwner, $"Remove '{profile.Name}' from local configuration?\n\nPreviously prepared keys and registered credentials are not changed.", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ConfigFileWriter.Save(snapshot, result);
        ActivityRecorded?.Invoke($"Local profile removed · {profile.Name}.");
        Changed = true; RefreshLists();
    });
}
