using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class SettingsWindow : Window
{
    private readonly string _path;
    private ConfigSnapshot? _importSnapshot;
    private string? _importText;
    public event Action? Finished;
    private void TenantFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (TenantList?.ItemsSource == null) return;
        System.Windows.Data.CollectionViewSource.GetDefaultView(TenantList.ItemsSource).Filter = item => item is Provider p && p.Name.Contains(TenantFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    private Window DialogOwner => Owner ?? this;
    public bool EmbeddedTerminal { get => EmbeddedChoice.IsChecked == true; set => EmbeddedChoice.IsChecked = value; }
    private sealed record ProfileRow(string Name, string[] Tenants)
    {
        public string Usage => Tenants.Length == 0 ? "Not assigned to a tenant" : $"Used by {Tenants.Length} tenant(s)";
    }
    public bool Changed { get; private set; }
    public bool RecheckRequested { get; private set; }
    public bool ElevationRequested { get; private set; }

    public SettingsWindow(string path, string executable, string version)
    {
        _path = path;
        InitializeComponent();
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
        ExecutableInfo.Text = executable;
        ConfigurationInfo.Text = path;
        VersionInfo.Text = version.StartsWith("YubiEnroll", StringComparison.OrdinalIgnoreCase) ? version : "YubiEnroll: " + version;
        AccessInfo.Text = "Running with administrative permissions: " + (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "Yes" : "No");
        ManagerInfo.Text = KeyResetService.FindExecutable().Length == 0 ? "YubiKey Manager CLI: missing (required for target verification)" : "YubiKey Manager CLI: installed; version and device access checked on Overview.";
        Attempt(RefreshLists);
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
        ProfileList.ItemsSource = config.Profiles.Select(p => new ProfileRow(p.Name, config.Providers.Where(t => t.Profile == p.Name).Select(t => t.Name).ToArray()));
        Feedback.Text = "";
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
    private void Recheck_Click(object sender, RoutedEventArgs e) { RecheckRequested = true; Finish(); }
    private void Elevate_Click(object sender, RoutedEventArgs e) { ElevationRequested = true; Finish(); }

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

    private void Finish() { if (IsVisible) Close(); else Finished?.Invoke(); }
    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _importText = null; _importSnapshot = null; ImportReview.Visibility = Visibility.Collapsed; ImportButton.IsEnabled = false;
    }
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => SettingsTabs.SelectedIndex = 2;
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
        var result = ProfileConfigWriter.Remove(snapshot.Text ?? "", profile.Name); // Refuses linked profiles before confirmation.
        if (MessageBox.Show(DialogOwner, $"Remove '{profile.Name}' from local configuration?\n\nPreviously prepared keys and registered credentials are not changed.", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ConfigFileWriter.Save(snapshot, result);
        Changed = true; RefreshLists();
    });
}
