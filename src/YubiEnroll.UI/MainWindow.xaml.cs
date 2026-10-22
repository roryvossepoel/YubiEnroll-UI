using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow : Window
{
    private Configuration? _config;
    private bool _busy;
    private string _exePath = "";
    private string _configPath = "";
    private string _versionText = "";
    private bool _cliReady;
    private Provider? _connectedProvider;
    private bool IsConnected => _connectedProvider != null && Tenants.SelectedItem is Provider p && p == _connectedProvider;
    private void RequireConnection()
    {
        if (!IsConnected) throw new InvalidOperationException("Connect to the selected tenant first.");
    }
    private bool _loading;
    private readonly bool _smokeTest;
    private Provider SelectedProvider => Tenants.SelectedItem as Provider
        ?? throw new InvalidOperationException("Select an Entra tenant first.");
    private EnrollmentProfile SelectedProfile => Profiles.SelectedItem as EnrollmentProfile
        ?? throw new InvalidOperationException("Select an enrollment profile first.");
    private UserCandidate SelectedUser => Users.SelectedItem as UserCandidate
        ?? throw new InvalidOperationException("Search and select a user first.");
    private CliRunner Runner => File.Exists(_exePath) ? new(_exePath)
        : throw new InvalidOperationException("Install Yubico YubiEnroll in its standard location, then recheck in Settings → System.");

    public MainWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_smokeTest) await InitializeAsync();
    }

    private async Task InitializeAsync(string? appDataOverride = null, string? executableOverride = null)
    {
        _cliReady = false;
        _connectedProvider = null;
        Diagnostics.Clear();
        Details.Clear();
        var issues = new List<string>();
        string[] candidates = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yubico", "YubiEnroll", "yubienroll.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Yubico", "YubiEnroll", "yubienroll.exe")];
        _exePath = executableOverride ?? candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        _configPath = ConfigReader.FindPath(appDataOverride);
        _versionText = "CLI version not checked";
        if (File.Exists(_exePath))
        {
            if (!await RunWithResultAsync(CheckVersionAsync, "Check YubiEnroll version", showDialog: false)) issues.Add(StatusText.Text);
        }
        else
        {
            _versionText = "YubiEnroll is not installed";
            issues.Add("YubiEnroll prerequisite not found. Install Yubico YubiEnroll in its standard location, then open Settings → System → Recheck.");
        }
        // Config loading is independent of executable discovery/version probing. An error in one
        // must not hide the other or disable Browse/Reload/the administrator restart option.
        if (!await RunWithResultAsync(() =>
        {
            LoadConfiguration(_configPath);
            return Task.CompletedTask;
        }, "Load configuration", showDialog: false)) issues.Add(StatusText.Text);
        StatusText.Text = issues.Count == 0 ? SetupOrReadyMessage() : string.Join("\n", issues);
        if (issues.Count > 0 && _config?.Providers.Count == 0) StatusText.Text += "\n" + SetupOrReadyMessage();
        if (issues.Count > 0)
        {
            StatusText.Text += "\nUse the same Windows account as your working YubiEnroll setup, then recheck in Settings → System.";
            if (Diagnostics.Text.Length > 0) OutputTabs.SelectedIndex = 2;
        }
    }

    private string SetupOrReadyMessage() => _config?.Providers.Count > 0 ? ReadyMessage() :
        "No tenants yet. Open Settings → Tenants → Add tenant to get started.";

    private string ReadyMessage() => $"Ready · {(IsAdministrator() ? "administrator" : "standard user")} · preview 0.2.0";

    private void LoadConfiguration(string? configPath = null)
    {
        _connectedProvider = null;
        var oldTenant = (Tenants.SelectedItem as Provider)?.Name;
        var oldProfile = (Profiles.SelectedItem as EnrollmentProfile)?.Name;
        // The CLI uses this Windows user's configuration. Never silently open another tenant config.
        _configPath = configPath ?? ConfigReader.FindPath();
        // Invalidate stale selections even if the new config is missing or malformed.
        _loading = true;
        try
        {
            _config = null;
            Tenants.ItemsSource = null;
            Profiles.ItemsSource = null;
            ResetSelection();
        }
        finally { _loading = false; }
        UpdateSummary();
        Configuration config;
        try { config = ConfigReader.Read(_configPath); }
        catch (FileNotFoundException) { config = ConfigReader.Parse("", _configPath); }
        catch (DirectoryNotFoundException) { config = ConfigReader.Parse("", _configPath); }
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
        }
        finally { _loading = false; }
        UpdateSummary();

        Record("Configuration loaded.");
    }

    private async Task CheckVersionAsync()
    {
        var result = await Runner.CaptureAsync("--version");
        RequireSuccess(result, "Version check");
        _versionText = result.Output.Trim();
        _cliReady = true;
        StatusText.Text = _config == null ? "YubiEnroll found. Load configuration to continue." : SetupOrReadyMessage();
    }

    private void Tenant_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _connectedProvider = null;
        ResetSelection();
        Query.Clear();
        KeyName.Clear();
        Details.Clear();
        var provider = Tenants.SelectedItem as Provider;
        Profiles.SelectedItem = _config?.Profiles.FirstOrDefault(x => x.Name == provider?.Profile);
        UpdateSummary();
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var provider = Tenants.SelectedItem as Provider;
        ContextBanner.Text = provider == null ? "Add a tenant in Settings to begin." :
            IsConnected ? $"Login completed · {provider.Name}" : $"{provider.Name} · Connect to activate this tenant and sign in.";
        ConnectButton.Content = IsConnected ? "Reconnect" : "Connect";
        ConnectButton.IsEnabled = provider != null && _cliReady;
        SearchPanel.IsEnabled = IsConnected;
        PreparePanel.IsEnabled = IsConnected && Users.SelectedItem != null;
        var profile = Profiles.SelectedItem as EnrollmentProfile;
        ProfileSummary.Text = profile?.Summary ?? "Choose an enrollment profile.";
        ResetNotice.Text = "";
        if (profile != null)
        {
            try
            {
                var policy = ProfileConfigWriter.Read(ConfigSnapshot.Read(_configPath).Text ?? "", profile.Name);
                ResetNotice.Text = policy.Reset switch
                {
                    true => "This profile resets the key and erases its existing FIDO credentials.",
                    false => "This profile keeps existing FIDO credentials (no reset).",
                    null => "Reset is not specified. Review the native YubiEnroll confirmation."
                };
            }
            catch { ResetNotice.Text = "Profile settings changed or cannot be read. Reload in Settings before continuing."; }
        }
        EnrollButton.IsEnabled = IsConnected && Users.SelectedItem != null && profile != null;
        CredentialsButton.IsEnabled = IsConnected && Users.SelectedItem != null;
    }

    private void ResetSelection()
    {
        Users.ItemsSource = null;
        SelectedUserText.Text = "Search and select a user.";
        EnrollButton.IsEnabled = false;
        CredentialsButton.IsEnabled = false;
        PreparePanel.IsEnabled = false;
    }

    private void User_Changed(object sender, SelectionChangedEventArgs e)
    {
        var user = Users.SelectedItem as UserCandidate;
        SelectedUserText.Text = user == null ? "Search and select a user." : "Selected: " + user.Label;
        UpdateSummary();
    }

    private async Task ActivateAsync(Provider provider, EnrollmentProfile? profile = null)
    {
        var before = ConfigReader.Read(_configPath);
        ConfigReader.EnsureUnchanged(before, provider, profile);
        var result = await Runner.CaptureAsync("providers", "activate", provider.Name);
        RequireSuccess(result, "Tenant activation");
        var after = ConfigReader.Read(_configPath);
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

    private async Task RunAsync(Func<Task> action, string operation = "Operation")
    {
        await RunWithResultAsync(action, operation);
    }

    private async Task<bool> RunWithResultAsync(Func<Task> action, string operation = "Operation", bool showDialog = true)
    {
        if (_busy) return false;
        _busy = true;
        WorkPanel.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        try { await action(); return true; }
        catch (Exception ex)
        {
            var report = FailureReport.Create(ex, operation, _configPath, _exePath);
            StatusText.Text = report.Summary;
            Diagnostics.AppendText($"Application: YubiEnroll UI 0.2.0\n{report.Diagnostic}\n\n");
            LogExpander.IsExpanded = true;
            OutputTabs.SelectedIndex = 2;
            Record($"{operation} failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            if (showDialog)
                MessageBox.Show(this, report.Summary, "YubiEnroll UI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { WorkPanel.IsEnabled = true; SettingsButton.IsEnabled = true; _busy = false; UpdateSummary(); }
    }

    private void Record(string text)
    {
        Activity.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
        Activity.ScrollToEnd();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow? dialog = null;
        await RunAsync(() =>
        {
            dialog = new SettingsWindow(_configPath, _exePath, _versionText) { Owner = this };
            dialog.ShowDialog();
            if (dialog.Changed) LoadConfiguration(_configPath);
            return Task.CompletedTask;
        }, "Settings");
        if (dialog?.RecheckRequested == true) await InitializeAsync();
        if (dialog?.ElevationRequested == true) Elevate_Click(sender, e);
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var provider = SelectedProvider;
        _connectedProvider = null;
        ResetSelection();
        await ActivateAsync(provider);
        StatusText.Text = "Complete login in the console/browser, then press Enter in the console.";
        var exit = await Runner.InteractiveAsync("login");
        Record($"Login command finished with exit {exit}.");
        if (exit == 0)
        {
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, null);
            _connectedProvider = provider;
            StatusText.Text = "Login completed. Search for the user who will receive this key.";
        }
        else StatusText.Text = "Login did not complete. Reconnect to try again.";
    }, "Connect tenant");

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); }
    }

    private async Task SearchAsync() => await RunAsync(async () =>
    {
        RequireConnection();
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
        RequireConnection();
        var user = SelectedUser;
        await ActivateAsync(SelectedProvider);
        var result = await Runner.CaptureAsync("credentials", "list", user.Id);
        RequireSuccess(result, "Credential lookup");
        Details.Text = result.Output;
        LogExpander.IsExpanded = true;
        OutputTabs.SelectedIndex = 0;
        StatusText.Text = "Registered credentials shown in Details.";
    });

    private async void Enroll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var provider = SelectedProvider;
        var profile = SelectedProfile;
        var user = SelectedUser;
        RequireConnection();
        var current = ConfigReader.Read(_configPath);
        ConfigReader.EnsureUnchanged(current, provider, profile);
        var reset = ProfileConfigWriter.Read(File.ReadAllText(_configPath), profile.Name).Reset;
        var arguments = EnrollmentCommand.Build(user.Id, profile.Name, KeyName.Text, reset);
        var message = $"Tenant: {provider.Name}\nTenant ID: {provider.TenantId}\n\nUser: {user.Details}\n\nProfile: {profile.Name}\nReset key: {reset?.ToString() ?? "CLI default; review native prompt"}\n\n" +
            (reset == true ? "WARNING: Reset destroys existing FIDO credentials on the key.\n\n" : "") +
            "Register a YubiKey for this user?";
        if (MessageBox.Show(this, message, "Confirm enrollment", MessageBoxButton.YesNo,
            reset != false ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ActivateAsync(provider, profile);
        Record($"Enrollment started · tenant {provider.Name} · user {user.Id} · profile {profile.Name} · reset {reset}.");
        StatusText.Text = "Follow the console prompts. Record the PIN securely, then press Enter to return.";
        var code = await Runner.InteractiveAsync(arguments);
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
            LogExpander.IsExpanded = true;
            OutputTabs.SelectedIndex = 0;
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
