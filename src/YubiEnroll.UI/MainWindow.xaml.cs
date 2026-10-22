using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow : Window
{
    private Configuration? _config;
    private bool _busy;
    private bool _loading;
    private readonly bool _smokeTest;
    private Provider SelectedProvider => Tenants.SelectedItem as Provider
        ?? throw new InvalidOperationException("Select an Entra tenant first.");
    private EnrollmentProfile SelectedProfile => Profiles.SelectedItem as EnrollmentProfile
        ?? throw new InvalidOperationException("Select an enrollment profile first.");
    private UserCandidate SelectedUser => Users.SelectedItem as UserCandidate
        ?? throw new InvalidOperationException("Search and select a user first.");
    private CliRunner Runner => File.Exists(ExePath.Text) ? new(ExePath.Text)
        : throw new InvalidOperationException("Locate the installed yubienroll.exe using Browse.");

    public MainWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_smokeTest) return;
        ConfigPath.Text = ConfigReader.FindPath();
        string[] candidates = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yubico", "YubiEnroll", "yubienroll.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Yubico", "YubiEnroll", "yubienroll.exe")];
        ExePath.Text = candidates.FirstOrDefault(File.Exists) ?? "";
        await RunAsync(async () =>
        {
            LoadConfiguration();
            if (File.Exists(ExePath.Text)) await CheckVersionAsync();
            else StatusText.Text = "Configuration loaded. Browse to the installed yubienroll.exe.";
        });
    }

    private void LoadConfiguration()
    {
        var oldTenant = (Tenants.SelectedItem as Provider)?.Name;
        var oldProfile = (Profiles.SelectedItem as EnrollmentProfile)?.Name;
        // The CLI uses this Windows user's configuration. Never silently open another tenant config.
        ConfigPath.Text = ConfigReader.FindPath();
        var config = ConfigReader.Read(ConfigPath.Text);
        _loading = true;
        try
        {
            _config = config;
            Tenants.ItemsSource = config.Providers;
            Profiles.ItemsSource = config.Profiles;
            Tenants.SelectedItem = config.Providers.FirstOrDefault(x => x.Name == oldTenant)
                ?? config.Providers.FirstOrDefault(x => x.Name == config.ActiveProvider)
                ?? config.Providers.FirstOrDefault();
            var tenant = Tenants.SelectedItem as Provider;
            Profiles.SelectedItem = config.Profiles.FirstOrDefault(x => x.Name == oldProfile && tenant?.Name == oldTenant)
                ?? config.Profiles.FirstOrDefault(x => x.Name == tenant?.Profile);
            ResetSelection();
            ResetKey.IsChecked = false;
        }
        finally { _loading = false; }
        UpdateSummary();
        if (config.Providers.Count == 0)
            throw new InvalidOperationException("No Entra provider found. Configure YubiEnroll for this Windows account first.");
        Record("Configuration loaded.");
    }

    private async Task CheckVersionAsync()
    {
        var result = await Runner.CaptureAsync("--version");
        RequireSuccess(result, "Version check");
        VersionText.Text = result.Output.Trim();
        StatusText.Text = $"Ready · {(IsAdministrator() ? "administrator" : "standard user")} · preview 0.1.0";
    }

    private void Tenant_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ResetSelection();
        Details.Clear();
        ResetKey.IsChecked = false;
        var provider = Tenants.SelectedItem as Provider;
        Profiles.SelectedItem = _config?.Profiles.FirstOrDefault(x => x.Name == provider?.Profile);
        UpdateSummary();
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ResetKey.IsChecked = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var provider = Tenants.SelectedItem as Provider;
        ContextBanner.Text = provider == null ? "No tenant selected" : $"Tenant: {provider.Name} · {provider.TenantId}";
        var profile = Profiles.SelectedItem as EnrollmentProfile;
        ProfileSummary.Text = profile == null ? "Choose an enrollment profile." :
            $"Profile: {profile.Name}\n{profile.Summary}\n\nReset is overridden by the explicit checkbox; unchecked means no reset.";
        EnrollButton.IsEnabled = Users.SelectedItem != null && profile != null;
    }

    private void ResetSelection()
    {
        Users.ItemsSource = null;
        SelectedUserText.Text = "No user selected";
        EnrollButton.IsEnabled = false;
        CredentialsButton.IsEnabled = false;
    }

    private void User_Changed(object sender, SelectionChangedEventArgs e)
    {
        var user = Users.SelectedItem as UserCandidate;
        SelectedUserText.Text = user == null ? "No user selected" : $"Object ID: {user.Id}";
        EnrollButton.IsEnabled = user != null && Profiles.SelectedItem != null;
        CredentialsButton.IsEnabled = user != null;
    }

    private async Task ActivateAsync(Provider provider, EnrollmentProfile? profile = null)
    {
        var before = ConfigReader.Read(ConfigPath.Text);
        ConfigReader.EnsureUnchanged(before, provider, profile);
        var result = await Runner.CaptureAsync("providers", "activate", provider.Name);
        RequireSuccess(result, "Tenant activation");
        var after = ConfigReader.Read(ConfigPath.Text);
        ConfigReader.EnsureUnchanged(after, provider, profile);
        if (after.ActiveProvider != provider.Name)
            throw new InvalidOperationException("Active provider does not match the selected tenant. Stop other YubiEnroll sessions and reload.");
        // Do not repopulate the dropdowns here: this preserves the exact user-selected profile.
    }

    private void RequireSuccess(CliResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        Details.Text = result.Error + Environment.NewLine + result.Output;
        throw new InvalidOperationException($"{operation} failed (exit {result.ExitCode}). Inspect Details; login or check permissions before trying again.");
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        WorkPanel.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            StatusText.Text = ex switch
            {
                FileNotFoundException => "Required file not found. Check the YubiEnroll installation and configuration.",
                UnauthorizedAccessException => "Access denied. Check file permissions and the Windows account in use.",
                InvalidOperationException or InvalidDataException or TimeoutException or ArgumentException => ex.Message,
                _ => "Operation failed. Check the installation and configuration; no automatic retry was performed."
            };
            Record("Operation failed (" + ex.GetType().Name + ").");
            MessageBox.Show(this, StatusText.Text, "YubiEnroll UI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { WorkPanel.IsEnabled = true; _busy = false; }
    }

    private void Record(string text)
    {
        Activity.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
        Activity.ScrollToEnd();
    }

    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "YubiEnroll|yubienroll.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        ExePath.Text = picker.FileName;
        ResetSelection();
        await RunAsync(CheckVersionAsync);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        LoadConfiguration();
        await CheckVersionAsync();
    });

    private async void Login_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var provider = SelectedProvider;
        await ActivateAsync(provider);
        StatusText.Text = "Complete login in the console/browser, then press Enter in the console.";
        var exit = await Runner.InteractiveAsync("login");
        Record($"Login command finished with exit {exit}.");
        StatusText.Text = exit == 0 ? "Login command completed. Use Status to check authentication." :
            "Login did not complete successfully. Check the console output and retry when ready.";
    });

    private async void Status_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await ActivateAsync(SelectedProvider);
        var result = await Runner.CaptureAsync("status");
        RequireSuccess(result, "Status check");
        Details.Text = result.Output;
        StatusText.Text = "Authentication status shown in Details.";
    });

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); }
    }

    private async Task SearchAsync() => await RunAsync(async () =>
    {
        var query = Query.Text.Trim();
        if (query.Length < 2) throw new ArgumentException("Enter at least two characters to search.");
        var provider = SelectedProvider;
        ResetSelection();
        await ActivateAsync(provider);
        StatusText.Text = "Searching users…";
        var result = await Runner.CaptureAsync("users", "--", query);
        RequireSuccess(result, "User search");
        Details.Text = result.Output;
        var users = UserSearch.Parse(result.Output);
        Users.ItemsSource = users;
        Record($"User search completed: {users.Count} recognized object IDs.");
        StatusText.Text = users.Count == 0 ? "No recognized user rows. Inspect Details; unknown output formats are not accepted for enrollment." :
            $"Select a user from {users.Count} result(s).";
    });

    private async void Credentials_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var user = SelectedUser;
        await ActivateAsync(SelectedProvider);
        var result = await Runner.CaptureAsync("credentials", "list", user.Id);
        RequireSuccess(result, "Credential lookup");
        Details.Text = result.Output;
        StatusText.Text = "Registered credentials shown in Details.";
    });

    private async void Enroll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var provider = SelectedProvider;
        var profile = SelectedProfile;
        var user = SelectedUser;
        var reset = ResetKey.IsChecked == true;
        var arguments = EnrollmentCommand.Build(user.Id, profile.Name, KeyName.Text, reset);
        var message = $"Tenant: {provider.Name}\nTenant ID: {provider.TenantId}\n\nUser: {user.Details}\n\nProfile: {profile.Name}\nReset key: {reset}\n\n" +
            (reset ? "WARNING: Reset destroys existing FIDO credentials on the key.\n\n" : "") +
            "Register a YubiKey for this user?";
        if (MessageBox.Show(this, message, "Confirm enrollment", MessageBoxButton.YesNo,
            reset ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ActivateAsync(provider, profile);
        Record($"Enrollment started · tenant {provider.Name} · user {user.Id} · profile {profile.Name} · reset {reset}.");
        StatusText.Text = "Follow the console prompts. Record the PIN securely, then press Enter to return.";
        var code = await Runner.InteractiveAsync(arguments);
        ResetKey.IsChecked = false;
        Record($"Enrollment command returned exit {code}.");
        if (code != 0)
        {
            StatusText.Text = "Enrollment failed or was interrupted. A credential may already exist. Check List credentials before retrying.";
            return;
        }
        // Exit 0 is CLI-reported success, not an independent authentication test.
        StatusText.Text = "YubiEnroll reported success. Retrieving registered credentials…";
        try
        {
            await ActivateAsync(provider, profile);
            var result = await Runner.CaptureAsync("credentials", "list", user.Id);
            if (result.ExitCode != 0) throw new InvalidOperationException();
            Details.Text = result.Output;
            StatusText.Text = "YubiEnroll reported success. Check the credential list in Details and test sign-in with the key.";
            Record("Post-enrollment credential list retrieved; sign-in validation is still required.");
        }
        catch
        {
            StatusText.Text = "YubiEnroll reported success, but the credential list could not be retrieved. Verify in Entra before retrying.";
            Record("Post-enrollment lookup unavailable; enrollment was not repeated.");
        }
    });

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (IsAdministrator()) { StatusText.Text = "Already running as administrator."; return; }
        if (MessageBox.Show(this, "YubiEnroll normally requests elevation when required. Use this fallback only if needed. " +
            "If you enter a different administrator account, that account has its own YubiEnroll configuration and sign-in. Continue?",
            "Restart as administrator", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        try
        {
            // No arguments/config/tokens cross the account boundary. The new instance reloads its own config.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception) { StatusText.Text = "Administrator restart cancelled or unavailable."; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        MessageBox.Show(this, "Finish the active operation and close its console before closing the application.", "Operation in progress");
    }
}
