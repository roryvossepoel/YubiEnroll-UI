using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class SettingsWindow : Window
{
    public event Action? PrerequisitesOpened;
    private void SettingsTab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != SettingsTabs) return;
        if (Feedback != null) Feedback.Text = "";
        if (PrerequisitesTab?.IsSelected == true) PrerequisitesOpened?.Invoke();
        if (RegistrationTab?.IsSelected == true && RegistrationHost?.Content is RegistrationLogView log) log.RefreshRecords();
    }
    private readonly RegistrationJournal _journal;
    private readonly string _path;
    private ConfigSnapshot? _importSnapshot;
    private string? _importText;
    private void TenantFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (TenantList?.ItemsSource == null) return;
        System.Windows.Data.CollectionViewSource.GetDefaultView(TenantList.ItemsSource).Filter = item => item is Provider p && p.Name.Contains(TenantFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    private Window DialogOwner => Owner ?? this;
    public bool EmbeddedTerminal { get => EmbeddedChoice.IsChecked == true; set => EmbeddedChoice.IsChecked = value; }
    private sealed record ProfileRow(string Name, string Usage);
    public bool Changed { get; private set; }

    public SettingsWindow(string path, string executable, string version, RegistrationJournal? registrationJournal = null)
    {
        _path = path;
        InitializeComponent();
        _journal = registrationJournal ?? new RegistrationJournal(RegistrationJournal.DefaultSettingsPath);
        EnabledChoice.IsChecked = _journal.Preferences.Enabled;
        SavePinChoice.IsChecked = _journal.Preferences.SaveGeneratedPin;
        FolderPath.Text = _journal.Preferences.Directory;
        DefaultFolderButton.IsEnabled = _journal.DefaultLogDirectory.Length > 0;
        DefaultFolderButton.ToolTip = _journal.DefaultLogDirectory;
        LogSettingsFeedback.Text = _journal.SettingsError ?? "";
        var log = new RegistrationLogView(_journal);
        log.OpenAppSettings += () => AppSettingsTab.IsSelected = true;
        RegistrationHost.Content = log;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
        VersionInfo.Text = version.StartsWith("YubiEnroll", StringComparison.OrdinalIgnoreCase) ? version : "YubiEnroll: " + version;
        RuntimeVersion.Text = "Version " + Environment.Version;
        Attempt(RefreshLists);
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
        try
        {
            _journal.Configure(EnabledChoice.IsChecked == true, FolderPath.Text, SavePinChoice.IsChecked == true);
            FolderPath.Text = _journal.Preferences.Directory;
            LogSettingsFeedback.Text = "Logging settings saved.";
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
        static void Set(Border pill, TextBlock status, TextBlock version, RequirementCheck check)
        {
            status.Text = check.Ready ? "Installed" : check.Detail == "Not installed" ? "Not installed" : "Check required";
            var match = System.Text.RegularExpressions.Regex.Match(check.Detail, @"version (\d+\.\d+\.\d+)");
            version.Text = match.Success ? "Version " + match.Groups[1].Value : "—";
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
        var config = ConfigReader.Parse(ConfigSnapshot.Read(_path).Text ?? "");
        TenantList.ItemsSource = config.Providers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        TenantFilter_Changed(this, null!);
        ProfileList.ItemsSource = config.Profiles.Select(p => new ProfileRow(p.Name, ProfileDescription(ProfileConfigWriter.Read(ConfigSnapshot.Read(_path).Text ?? "", p.Name))));
        Feedback.Text = "";
    }

    private static string ProfileDescription(ProfileSetup p)
    {
        var lines = new List<string> { p.Reset == true ? "Reset FIDO before setup" : p.Reset == false ? "Keep existing FIDO credentials" : "Reset: not specified" };
        lines.Add(p.RandomPin == true ? $"Generate PIN{(p.RandomPinLength is { } length ? $" · {length} characters" : "")}" : "Use an existing or manually entered PIN");
        if (p.ForcePinChange == true) lines.Add("Change PIN on first use");
        if (p.MinPinLength is { } minimum) lines.Add($"Minimum PIN length: {minimum}");
        if (p.RequireAlwaysUv == true) lines.Add("Always require user verification");
        if (p.RequireEa == true) lines.Add("Enterprise attestation");
        return string.Join("\n", lines);
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EditTenantButton != null) EditTenantButton.IsEnabled = TenantList.SelectedItem != null;
        if (EditProfileButton != null) EditProfileButton.IsEnabled = ProfileList.SelectedItem != null;
        if (RemoveTenantButton != null) RemoveTenantButton.IsEnabled = TenantList.SelectedItem != null;
        if (RemoveProfileButton != null) RemoveProfileButton.IsEnabled = ProfileList.SelectedItem != null;
    }

    private void AddTenant_Click(object sender, RoutedEventArgs e) => EditTenant(null);
    private void EditTenant_Click(object sender, RoutedEventArgs e) => EditTenant((TenantList.SelectedItem as Provider)?.Name);
    private void EditTenant(string? name) => Attempt(() =>
    {
        var dialog = new AddTenantWindow(ConfigSnapshot.Read(_path), name) { Owner = DialogOwner };
        if (dialog.ShowDialog() != true) return;
        Changed = true;
        RefreshLists();
    });
    private void NewProfile_Click(object sender, RoutedEventArgs e) => EditProfile(null);
    private void EditProfile_Click(object sender, RoutedEventArgs e) => EditProfile((ProfileList.SelectedItem as ProfileRow)?.Name);
    private void EditProfile(string? name) => Attempt(() =>
    {
        var dialog = new ProfileWindow(ConfigSnapshot.Read(_path), name) { Owner = DialogOwner };
        if (dialog.ShowDialog() != true) return;
        Changed = true;
        RefreshLists();
    });

    private void Export_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var text = ConfigTransfer.Export(ConfigSnapshot.Read(_path).Text ?? "");
        var picker = new SaveFileDialog { Filter = "TOML configuration|*.toml", FileName = "yubienroll-settings.toml" };
        if (picker.ShowDialog(DialogOwner) != true) return;
        if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a separate export file, not the active configuration.");
        File.WriteAllText(picker.FileName, text);
        Feedback.Text = "Exported to " + picker.FileName + ". No authentication cache was copied.";
    });

    private void ChooseImport_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
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
        _importText = null;
        ImportButton.IsEnabled = false;
        var validated = ConfigTransfer.ValidateImport(text);
        _importSnapshot = ConfigSnapshot.Read(_path);
        var config = ConfigReader.Parse(validated);
        var current = ConfigReader.Parse(_importSnapshot.Text ?? "");
        ImportReview.Visibility = Visibility.Visible;
        ImportPreview.Text = $"{config.Providers.Count} tenant(s): " + string.Join(", ", config.Providers.Select(x => x.Name)) +
            $"\n\n{config.Profiles.Count} profile(s): " + string.Join(", ", config.Profiles.Select(x => x.Name));
        ImportPreview.Text += "\n\nCurrent tenants being replaced: " + string.Join(", ", current.Providers.Select(x => x.Name)) +
            "\nCurrent profiles being replaced: " + string.Join(", ", current.Profiles.Select(x => x.Name));
        _importText = validated;
        ImportButton.IsEnabled = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_importText == null || _importSnapshot == null) return;
        if (MessageBox.Show(DialogOwner, "Replace all current tenant and profile settings with the previewed configuration? An exact backup of the current file will be kept.",
            "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ApplyImport();
    });

    internal void ApplyImport()
    {
        if (_importText == null || _importSnapshot == null) throw new InvalidOperationException("Preview an import first.");
        var backup = ConfigFileWriter.Save(_importSnapshot, ConfigTransfer.ValidateImport(_importText));
        Changed = true;
        _importText = null;
        ImportButton.IsEnabled = false;
        RefreshLists();
        ImportReview.Visibility = Visibility.Collapsed;
        Feedback.Text = "Configuration imported. Reconnect to a tenant. " + (backup == null ? "No previous file needed backup." : "Backup: " + backup);
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _importText = null; _importSnapshot = null; ImportReview.Visibility = Visibility.Collapsed; ImportButton.IsEnabled = false;
    }
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => PrerequisitesTab.IsSelected = true;
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Feedback.Text = "Open in your browser: " + url; }
    }
    private void RemoveTenant_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (TenantList.SelectedItem is not Provider tenant) return;
        var snapshot = ConfigSnapshot.Read(_path);
        if (MessageBox.Show(DialogOwner, $"Remove '{tenant.Name}' from local configuration?\n\nThis does not delete the Entra tenant, app registration, users or key credentials. Shared profiles remain.", "Remove tenant", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ConfigFileWriter.Save(snapshot, TenantConfigWriter.Remove(snapshot.Text ?? "", tenant.Name));
        Changed = true; RefreshLists();
    });
    private void RemoveProfile_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (ProfileList.SelectedItem is not ProfileRow profile) return;
        var snapshot = ConfigSnapshot.Read(_path);
        var result = ProfileConfigWriter.Remove(snapshot.Text ?? "", profile.Name);
        if (MessageBox.Show(DialogOwner, $"Remove '{profile.Name}' from local configuration?\n\nPreviously prepared keys and registered credentials are not changed.", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ConfigFileWriter.Save(snapshot, result);
        Changed = true; RefreshLists();
    });
}
