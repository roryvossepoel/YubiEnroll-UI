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
    private readonly TextBox Diagnostics = new();
    private readonly TextBox Details = new();
    private readonly ActivityLogView _activityView = new();
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
        ?? throw new InvalidOperationException("Select a configuration profile first.");
    private UserCandidate SelectedUser => Users.SelectedItem as UserCandidate
        ?? throw new InvalidOperationException("Search and select a user first.");
    private CliRunner Runner => File.Exists(_exePath) ? new(_exePath)
        : throw new InvalidOperationException("Install Yubico YubiEnroll in its standard location, then recheck in Settings → Prerequisites.");

    public MainWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        SizeChanged += (_, _) => ResizePreparation();
        ShowPage(OverviewPage);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_smokeTest) { await InitializeAsync(); await RefreshOverviewAsync(); }
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
            issues.Add("YubiEnroll prerequisite not found. Install Yubico YubiEnroll in its standard location, then open Settings → Prerequisites → Recheck.");
        }
        // Config loading is independent of executable discovery/version probing. An error in one
        // must not hide the other or disable Settings and prerequisite recovery.
        if (!await RunWithResultAsync(() =>
        {
            LoadConfiguration(_configPath);
            return Task.CompletedTask;
        }, "Load configuration", showDialog: false)) issues.Add(StatusText.Text);
        StatusText.Text = issues.Count == 0 ? SetupOrReadyMessage() : string.Join("\n", issues);
        if (issues.Count > 0 && _config?.Providers.Count == 0) StatusText.Text += "\n" + SetupOrReadyMessage();
        if (issues.Count > 0)
        {
            StatusText.Text += "\nUse the same Windows account as your working YubiEnroll setup, then recheck in Settings → Prerequisites.";

        }
    }

    private string SetupOrReadyMessage() => _config?.Providers.Count > 0 ? ReadyMessage() :
        "No tenants yet. Open Settings → Tenants → Add tenant to get started.";

    private string ReadyMessage() => IsAdministrator() ? "Ready · administrator · 0.4.3" : "Standard user · administrator rights required for key inspection and FIDO reset";

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
        SuggestRegistrationName();
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
            IsConnected ? $"Connected to {provider.Name}" : $"Not connected · {provider.Name}";
        ConnectButton.Content = IsConnected ? "Reconnect" : "Connect";
        ConnectButton.IsEnabled = provider != null && _cliReady;
        SearchPanel.IsEnabled = IsConnected;
        if (!IsConnected) SelectedUserText.Text = "Connect to a tenant first.";
        PreparePanel.IsEnabled = IsConnected && Users.SelectedItem != null;
        var profile = Profiles.SelectedItem as EnrollmentProfile;
        EnrollButton.IsEnabled = IsConnected && Users.SelectedItem != null && profile != null && !_choosingTarget && !_inventoryDirty &&
            EnrollmentTargetGuard.CanStart(_selectedKey?.Key, _knownKeys.Select(x => x.Key).ToArray(), _unavailableKeys);
        CredentialsButton.IsEnabled = IsConnected && Users.SelectedItem != null;
        CredentialsButton.Visibility = Users.SelectedItem != null ? Visibility.Visible : Visibility.Collapsed;
        SelectedUserText.Visibility = Users.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateEnrollmentGuidance();
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
        SelectedUserText.Text = user == null ? "Search and select a user." : "User selected.";
        if (user != null) StatusText.Text = "User selected. Review the configuration profile before enrolling the key.";
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
        Record(operation + " failed.", "Error", $"YubiEnroll exit code: {result.ExitCode}\n" + InteractiveFailure.Describe(Details.Text));
        throw new InvalidOperationException($"{operation} failed (exit {result.ExitCode}). Open Settings → Activity log for diagnostic details.");
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
        Navigation.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        try { await action(); return true; }
        catch (Exception ex)
        {
            var report = FailureReport.Create(ex, operation, _configPath, _exePath);
            StatusText.Text = report.Summary;
            Diagnostics.AppendText($"Application: YubiEnroll UI 0.4.3\n{report.Diagnostic}\n\n");

            Record($"{operation} failed.", "Error", report.Diagnostic);
            if (showDialog)
                MessageBox.Show(this, report.Summary, "YubiEnroll UI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { WorkPanel.IsEnabled = true; Navigation.IsEnabled = true; SettingsButton.IsEnabled = true; _busy = false; UpdateSummary(); }
    }

    private void Record(string text, string level = "Info", string detail = "")
    {
        _activityView.Add(text, level, detail);
    }

    private async void ResetTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _inventoryDirty || _choosingTarget || _selectedKey == null || !IsAdministrator()) return;
        var target = _selectedKey.Key;
        int? resetExit = null;
        var resetCompleted = await RunWithResultAsync(async () =>
        {
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0) throw new InvalidOperationException("Install YubiKey Manager CLI in Prerequisites.");
            var runner = new CliRunner(path);
            var service = new KeyResetService(a => runner.CaptureAsync(a), a => RunInteractiveAsync(path, a));
            var exit = resetExit = await service.ResetAsync(target, key => MessageBox.Show(this,
                $"Target: {key.Label}\n\nErase all FIDO2/U2F passkeys and the PIN on this key? This cannot be undone. Registrations in Entra and other services remain.\n\nOther connected keys are not targeted. Confirm, reinsert and touch this same key in the following prompts.",
                "Reset target key", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
            StatusText.Text = exit switch { null => "Reset cancelled.", 0 => "YubiKey Manager reported a successful FIDO reset. Reading target status…", _ => "Reset failed or was interrupted. Check the target key before retrying." };
            Record(StatusText.Text + " · serial " + target.Serial, exit is null or 0 ? "Info" : "Error");
        }, "Reset target key");
        await RefreshOverviewAsync();
        if (resetCompleted && resetExit == 0)
        {
            var readBack = _selectedKey?.Key == target ? _selectedKey.Pin : FidoPinState.Unknown;
            StatusText.Text = readBack switch
            {
                FidoPinState.NotSet => "Reset reported successful. Read-back: target PIN is not set. Entra registrations remain.",
                FidoPinState.Set or FidoPinState.Blocked => "Reset reported successful, but a PIN is still set. Inspect the target before continuing.",
                _ => "Reset reported successful, but target PIN status could not be verified. Reconnect the target and refresh."
            };
            Record(StatusText.Text + " · serial " + target.Serial, readBack == FidoPinState.NotSet ? "Info" : "Warning");
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private async void Login_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var provider = SelectedProvider;
        _connectedProvider = null;
        ResetSelection();
        await ActivateAsync(provider);
        StatusText.Text = $"Connecting to {provider.Name}… Complete the native confirmation and browser sign-in. Successful sign-in returns automatically.";
        var exit = await RunInteractiveAsync(_exePath, ["login"]);
        Record(exit == 0 ? $"Sign-in completed · {provider.Name}." : $"Sign-in did not complete · {provider.Name}.", exit == 0 ? "Info" : "Error", $"YubiEnroll exit code: {exit}. See the interactive output for the reported cause.\n{_lastInteractiveFailure}");
        if (exit == 0)
        {
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, null);
            _connectedProvider = provider;
            StatusText.Text = "Signed in. Disconnect the administrator key before enrollment; keep the chosen target connected.";
            _deviceRefresh?.Start();
        }
        else StatusText.Text = $"Could not connect to {provider.Name}. Select Connect to try again.";
        UpdateSummary();
        if (IsConnected) _ = Dispatcher.BeginInvoke(() => Query.Focus());
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
        SelectedUserText.Text = users.Count == 0 ? "No users found." : $"{users.Count} user(s) found. Select a user.";
        Record($"User search completed: {users.Count} user(s) found.");
        StatusText.Text = users.Count == 0 ? "No users recognized. Try another name or email; unsupported output formats cannot be used for enrollment." :
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
        ShowRegisteredKeys(user, SelectedProvider, result.Output);
        Record("Registered keys retrieved.");
        StatusText.Text = "Registered keys retrieved for the selected user.";
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
        var warning = reset switch
        {
            true => "This profile erases all existing FIDO credentials and resets the FIDO PIN before enrollment.",
            false => "This profile does not reset existing FIDO credentials.",
            null => "Reset is not specified. YubiEnroll's default may erase existing FIDO credentials."
        };
        var target = _selectedKey?.Key ?? throw new InvalidOperationException("Choose a target key first.");
        await CheckEnrollmentTargetAsync(target);
        if (!ConfirmEnrollment(provider, profile, user, warning)) return;
        await ActivateAsync(provider, profile);
        await CheckEnrollmentTargetAsync(target);
        Record($"Enrollment started · tenant {provider.Name} · user {user.Id} · profile {profile.Name} · reset {reset}.");
        StatusText.Text = EmbeddedTerminal ? "Follow the prompts below. Record the PIN securely before returning." : "Follow the console prompts. Record the PIN securely, then press Enter to return.";
        var code = await RunInteractiveAsync(_exePath, arguments);
        _deviceRefresh?.Start(); // Refresh status after enrollment, once the operation has released the key.
        Record(code == 0 ? "Enrollment completed (reported by YubiEnroll)." : "Enrollment failed or was interrupted.", code == 0 ? "Info" : "Error", $"YubiEnroll exit code: {code}\n{_lastInteractiveFailure}");
        if (code != 0)
        {
            StatusText.Text = "Enrollment failed or was interrupted. A credential may already exist. Check Registered keys before retrying.";
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
            ShowRegisteredKeys(user, provider, result.Output);
            StatusText.Text = "YubiEnroll reported success. Review the registered keys and test sign-in.";
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
