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
    public bool Changed { get; private set; }
    public bool RecheckRequested { get; private set; }
    public bool ElevationRequested { get; private set; }

    public SettingsWindow(string path, string executable, string version)
    {
        _path = path;
        InitializeComponent();
        ExecutableInfo.Text = executable;
        ConfigurationInfo.Text = path;
        VersionInfo.Text = "YubiEnroll UI 0.2.0 · " + version;
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
        TenantList.ItemsSource = config.Providers;
        ProfileList.ItemsSource = config.Profiles;
        Feedback.Text = $"{config.Providers.Count} tenant(s) · {config.Profiles.Count} profile(s)";
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EditTenantButton != null) EditTenantButton.IsEnabled = TenantList.SelectedItem != null;
        if (EditProfileButton != null) EditProfileButton.IsEnabled = ProfileList.SelectedItem != null;
    }

    private void AddTenant_Click(object sender, RoutedEventArgs e) => EditTenant(null);
    private void EditTenant_Click(object sender, RoutedEventArgs e) => EditTenant((TenantList.SelectedItem as Provider)?.Name);
    private void EditTenant(string? name) => Attempt(() =>
    {
        var dialog = new AddTenantWindow(ConfigSnapshot.Read(_path), name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Changed = true;
        RefreshLists();
    });
    private void NewProfile_Click(object sender, RoutedEventArgs e) => EditProfile(null);
    private void EditProfile_Click(object sender, RoutedEventArgs e) => EditProfile((ProfileList.SelectedItem as EnrollmentProfile)?.Name);
    private void EditProfile(string? name) => Attempt(() =>
    {
        var dialog = new ProfileWindow(ConfigSnapshot.Read(_path), name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Changed = true;
        RefreshLists();
    });
    private void Recheck_Click(object sender, RoutedEventArgs e) { RecheckRequested = true; Close(); }
    private void Elevate_Click(object sender, RoutedEventArgs e) { ElevationRequested = true; Close(); }

    private void Export_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        var text = ConfigTransfer.Export(ConfigSnapshot.Read(_path).Text ?? "");
        var picker = new SaveFileDialog { Filter = "TOML configuration|*.toml", FileName = "yubienroll-settings.toml" };
        if (picker.ShowDialog(this) != true) return;
        if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a separate export file, not the active configuration.");
        File.WriteAllText(picker.FileName, text);
        Feedback.Text = "Tenants and profiles exported. No authentication cache was copied.";
    });

    private void ChooseImport_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        _importText = null;
        ImportButton.IsEnabled = false;
        var picker = new OpenFileDialog { Filter = "TOML configuration|*.toml", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        PreviewImport(File.ReadAllText(picker.FileName));
    });

    internal void PreviewImport(string text)
    {
        _importText = null;
        ImportButton.IsEnabled = false;
        var validated = ConfigTransfer.ValidateImport(text);
        _importSnapshot = ConfigSnapshot.Read(_path);
        var config = ConfigReader.Parse(validated);
        ImportPreview.Text = $"{config.Providers.Count} tenant(s): " + string.Join(", ", config.Providers.Select(x => x.Name)) +
            $"\n\n{config.Profiles.Count} profile(s): " + string.Join(", ", config.Profiles.Select(x => x.Name));
        _importText = validated;
        ImportButton.IsEnabled = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Attempt(() =>
    {
        if (_importText == null || _importSnapshot == null) return;
        if (MessageBox.Show(this, "Replace all current tenant and profile settings with the previewed configuration? An exact backup of the current file will be kept.",
            "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ApplyImport();
    });

    internal void ApplyImport()
    {
        if (_importText == null || _importSnapshot == null) throw new InvalidOperationException("Preview an import first.");
        ConfigFileWriter.Save(_importSnapshot, ConfigTransfer.ValidateImport(_importText));
        Changed = true;
        _importText = null;
        ImportButton.IsEnabled = false;
        RefreshLists();
        Feedback.Text = "Configuration imported. Reconnect to a tenant after closing Settings.";
    }
}
