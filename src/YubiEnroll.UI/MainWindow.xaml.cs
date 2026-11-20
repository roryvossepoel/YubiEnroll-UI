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
    private EnrollmentProfile SelectedProfile => (Profiles.SelectedItem as ProfileChoice)?.Profile
        ?? throw new InvalidOperationException("Select a configuration profile first.");
    private UserCandidate SelectedUser => Users.SelectedItem as UserCandidate
        ?? throw new InvalidOperationException("Search and select a user first.");
    private CliRunner Runner => File.Exists(_exePath) ? new(_exePath)
        : throw new InvalidOperationException("Install Yubico YubiEnroll in its standard location, then return to Settings → Prerequisites.");

    public MainWindow(bool smokeTest = false)
    {
        _smokeTest = smokeTest;
        _registrationJournal = new RegistrationJournal(smokeTest
            ? Path.Combine(Path.GetTempPath(), "YubiEnroll-log-test-" + Guid.NewGuid().ToString("N"), "preferences.json")
            : RegistrationJournal.DefaultSettingsPath);
        InitializeComponent();
        Closed += (_, _) => EndManagementSession();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Activated += Window_Activated;
        SizeChanged += (_, _) => ResizePreparation();
        ShowPage(OverviewPage);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_smokeTest) await StartApplicationAsync();
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
            issues.Add("YubiEnroll prerequisite not found. Install Yubico YubiEnroll in its standard location, then open Settings → Prerequisites.");
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
            StatusText.Text += "\nUse the same Windows account as your working YubiEnroll setup, then return to Settings → Prerequisites.";

        }
    }

    private string SetupOrReadyMessage() => _config?.Providers.Count > 0 ? ReadyMessage() :
        "No tenants yet. Open Settings → Tenants → Add tenant to get started.";

    private string ReadyMessage() => IsAdministrator() ? "Ready · administrator · 0.7.4" : "Standard user · administrator rights required for key inspection and FIDO reset";

    private void LoadConfiguration(string? configPath = null)
    {
        ResetManagement();
        _connectedProvider = null;
        var oldTenant = (Tenants.SelectedItem as Provider)?.Name;
        var oldProfile = (Profiles.SelectedItem as ProfileChoice)?.Profile.Name;
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
            Profiles.ItemsSource = config.Profiles.Select(p => new ProfileChoice(p)).ToArray();
            Tenants.SelectedItem = config.Providers.FirstOrDefault(x => x.Name == oldTenant)
                ?? config.Providers.FirstOrDefault(x => x.Name == config.ActiveProvider)
                ?? config.Providers.FirstOrDefault();
            var tenant = Tenants.SelectedItem as Provider;
            Profiles.SelectedItem = Profiles.Items.Cast<ProfileChoice>().FirstOrDefault(x => x.Profile.Name == oldProfile);
            ResetSelection();
        }
        finally { _loading = false; }
        UpdateSummary();

        Record("Configuration loaded.");
    }

    private async Task CheckVersionAsync()
    {
        var result = await Runner.CaptureWithTimeoutAsync(TimeSpan.FromSeconds(10), "--version");
        RequireSuccess(result, "Version check");
        _versionText = result.Output.Trim();
        _cliReady = true;
        StatusText.Text = _config == null ? "YubiEnroll found. Load configuration to continue." : SetupOrReadyMessage();
    }

    private void Tenant_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _connectedProvider = null;
        ResetManagement();
        ResetSelection();
        Query.Clear();
        KeyName.Clear();
        SuggestRegistrationName();
        Details.Clear();
        var provider = Tenants.SelectedItem as Provider;
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
        var hasTenants = Tenants.Items.Count > 0;
        TenantConnectionRow.Visibility = hasTenants ? Visibility.Visible : Visibility.Collapsed;
        EmptyTenants.Visibility = hasTenants ? Visibility.Collapsed : Visibility.Visible;
        ContextBanner.Visibility = hasTenants ? Visibility.Visible : Visibility.Collapsed;
        ContextBanner.Text = provider == null ? "Select a tenant." :
            IsConnected ? $"Connected to {provider.Name}" : $"Not connected · {provider.Name}";
        ConnectButton.Content = IsConnected ? "Reconnect" : "Connect";
        ConnectButton.IsEnabled = provider != null && _cliReady;
        SearchPanel.IsEnabled = IsConnected;
        if (!IsConnected) SelectedUserText.Text = "Connect to a tenant first.";
        PreparePanel.IsEnabled = TargetReady;
        var profile = Profiles.SelectedItem as EnrollmentProfile;
        // Wizard rendering determines which action is available at the current step.
        SelectedUserText.Visibility = Users.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateEnrollmentGuidance();
        RenderManagement();
    }

    private void ResetSelection()
    {
        Users.ItemsSource = null;
        SelectedUserText.Text = "Search and select a user.";
        EnrollButton.IsEnabled = false;
        PreparePanel.IsEnabled = false;
    }

    private void User_Changed(object sender, SelectionChangedEventArgs e)
    {
        var user = Users.SelectedItem as UserCandidate;
        SelectedUserText.Text = user == null ? "Search and select a user." : "User selected.";
        if (user != null) StatusText.Text = _addPasskey ? "User selected. Review the registration before adding the passkey." : "User selected. Review the registration before enrolling the key.";
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
            Diagnostics.AppendText($"Application: YubiEnroll UI 0.7.4\n{report.Diagnostic}\n\n");

            Record($"{operation} failed.", "Error", report.Diagnostic);
            if (showDialog)
                MessageBox.Show(this, report.Summary, "YubiEnroll UI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { WorkPanel.IsEnabled = true; Navigation.IsEnabled = true; SettingsButton.IsEnabled = true; _busy = false; UpdateSummary(); if (_closeAfterLogin) { _closeAfterLogin = false; Close(); } }
    }

    private void Record(string text, string level = "Info", string detail = "")
    {
        _activityView.Add(text, level, detail);
    }

    private async void ResetTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _resetStep != 1 || !TargetReady || _selectedKey?.Key != _resetReviewTarget || ResetAcknowledged.IsChecked != true || !IsAdministrator()) return;
        var target = _selectedKey!.Key;
        if (target.IsNfc && _selectedKey.Capabilities?.ResetAllowed != true) return;
        int? resetExit = null;
        var resetCompleted = await RunWithResultAsync(async () =>
        {
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0) throw new InvalidOperationException("Install YubiKey Manager CLI in Prerequisites.");
            var runner = new CliRunner(path);
            var service = new KeyResetService(a => runner.CaptureAsync(a), a => RunInteractiveAsync(path, a), () => NfcDiscovery.ReadAsync(runner));
            // The dedicated review page is the destructive confirmation. Native prompts remain enabled.
            var exit = resetExit = await service.ResetAsync(target, key =>
                _resetStep == 1 && _resetReviewTarget == key && _selectedKey?.Key == key && ResetAcknowledged.IsChecked == true);
            StatusText.Text = exit switch { null => "Reset cancelled.", 0 => "YubiKey Manager reported a successful FIDO reset. Reading target status…", _ => "Reset failed or was interrupted. Check the target key before retrying." };
            Record(StatusText.Text + " · serial " + target.Serial, exit is null or 0 ? "Info" : "Error");
        }, "Reset target key");
        var operationMessage = StatusText.Text;
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
        else StatusText.Text = operationMessage;
        ResetResultText.Text = "Target · " + target.Label + "\n\n" + StatusText.Text;
        _resetStep = 2; ResetAcknowledged.IsChecked = false; RenderWizard();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private async void Login_Click(object sender, RoutedEventArgs e) => await LoginAsync();
    private async Task LoginAsync() => await RunAsync(async () =>
    {
        var provider = SelectedProvider;
        _connectedProvider = null;
        ResetSelection();
        await ActivateAsync(provider);
        StatusText.Text = $"Connecting to {provider.Name}… Complete browser sign-in. Successful sign-in returns automatically.";
        var exit = await RunInteractiveAsync(_exePath, ["login"]);
        Record(exit == 0 ? $"Sign-in completed · {provider.Name}." : $"Sign-in did not complete · {provider.Name}.", exit == 0 ? "Info" : "Error", $"YubiEnroll exit code: {exit}. See the interactive output for the reported cause.\n{_lastInteractiveFailure}");
        if (exit == 0)
        {
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, null);
            _connectedProvider = provider;
            StatusText.Text = ManagePage.IsVisible ? $"Signed in to {provider.Name}." : "Signed in. Disconnect the administrator key before enrollment; keep the chosen target connected.";
            if (!ManagePage.IsVisible) _deviceRefresh?.Start();
        }
        else StatusText.Text = $"{_lastInteractiveFailure} Select Connect to try again.";
        UpdateSummary();
        if (IsConnected && !ManagePage.IsVisible) _ = Dispatcher.BeginInvoke(() => Query.Focus());
    }, "Connect tenant");

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); }
    }

    private async Task SearchAsync() => await RunAsync(async () =>
    {
        ResetSelection();
        StatusText.Text = "Searching users…";
        var users = await FindUsersAsync(Query.Text.Trim());
        Users.ItemsSource = users;
        SelectedUserText.Text = users.Count == 0 ? "No users found." : $"{users.Count} user(s) found. Select a user.";
        Record($"User search completed: {users.Count} user(s) found.");
        StatusText.Text = users.Count == 0 ? "No users recognized. Try another name or email; unsupported output formats cannot be used for enrollment." :
            $"Select a user from {users.Count} result(s).";
    });

    private async Task<IReadOnlyList<UserCandidate>> FindUsersAsync(string query)
    {
        RequireConnection();
        if (query.Length < 2) throw new ArgumentException("Enter at least two characters to search.");
        await ActivateAsync(SelectedProvider);
        var result = await Runner.CaptureAsync("users", "--", query);
        RequireSuccess(result, "User search");
        return UserSearch.Parse(result.Output);
    }

    private async void Enroll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_enrollStep != ReviewStep || !EnrollmentReady || !_reviewValid) throw new InvalidOperationException("Review the target, tenant, user and profile first.");
        var provider = SelectedProvider;
        EnrollmentProfile? profile = _addPasskey ? null : SelectedProfile;
        var user = SelectedUser;
        RequireConnection();
        var current = ConfigReader.Read(_configPath);
        ConfigReader.EnsureUnchanged(current, provider, profile);
        var setup = _addPasskey ? PasskeyAddition.Settings(_selectedKey?.Capabilities ?? throw new InvalidOperationException("Refresh the selected key.")) :
            ProfileConfigWriter.Read(File.ReadAllText(_configPath), profile!.Name);
        var reset = setup.Reset;
        var target = _selectedKey?.Key ?? throw new InvalidOperationException("Choose a target key first.");
        await CheckEnrollmentTargetAsync(target, setup);
        // The Review step is the confirmation; never silently skip its fresh target checks.
        await ActivateAsync(provider, profile);
        await CheckEnrollmentTargetAsync(target, setup);
        ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, profile);
        var registration = BeginRegistrationRecord(provider, user, target, setup, KeyName.Text);
        _generatedPin = null;
        _captureGeneratedPin = registration?.SaveGeneratedPin == true && !_addPasskey && setup.RandomPin == true;
        Record($"Enrollment started · tenant {provider.Name} · user {user.Id} · profile {setup.Name} · reset {reset}.");
        StatusText.Text = _addPasskey ? "Enter the existing FIDO PIN in the native prompt. No new PIN is generated." : EmbeddedTerminal ? "Follow the prompts below. Record the PIN securely before returning." : "Follow the console prompts. Record the PIN securely, then press Enter to return.";
        int code;
        EnrollmentProfileLease? lease = null;
        try
        {
            if (_addPasskey || setup.MinPinLength == null || setup.RandomPinLength == null)
                lease = EnrollmentProfileLease.Create(_configPath, EnrollmentProfileLease.Resolve(setup,
                    _selectedKey?.Capabilities ?? throw new InvalidOperationException("Refresh the selected key.")));
            var operationProfile = lease?.Profile ?? profile!;
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, operationProfile);
            var arguments = EnrollmentCommand.Build(user.Id, operationProfile.Name, KeyName.Text, reset, target.Reader);
            code = await RunInteractiveAsync(_exePath, arguments);
        }
        catch
        {
            CompleteRegistrationRecord(registration, null);
            ShowEnrollmentResult("The enrollment outcome is unknown. Check Manage user passkeys before retrying.");
            throw;
        }
        finally
        {
            _captureGeneratedPin = false;
            try { lease?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { Record("Temporary enrollment profile could not be removed. The enrollment outcome is unchanged.", "Warning"); }
        }
        // Commit the native outcome before read-back, UI navigation or a later enrollment can alter its context.
        CompleteRegistrationRecord(registration, code, _generatedPin);
        _generatedPin = null;
        _deviceRefresh?.Start(); // Refresh status after enrollment, once the operation has released the key.
        Record(code == 0 ? "Enrollment completed (reported by YubiEnroll)." : "Enrollment failed or was interrupted.", code == 0 ? "Info" : "Error", $"YubiEnroll exit code: {code}\n{_lastInteractiveFailure}");
        if (code != 0)
        {
            ShowEnrollmentResult("Enrollment failed or was interrupted. A credential may already exist. Check Manage user passkeys before retrying.");
            return;
        }
        // Exit 0 is CLI-reported success, not an independent authentication test.
        StatusText.Text = "YubiEnroll reported success. Test sign-in with the new passkey.";
        ShowEnrollmentResult(StatusText.Text);
    });

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (IsAdministrator()) { StatusText.Text = "Already running as administrator."; return; }
        try
        {
            // No arguments/config/tokens cross the account boundary. The new instance reloads its own config.
            var restarted = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            if (restarted == null) { StartupFeedback.Text = "Unable to start the elevated instance. Please try again."; return; }
            Close();
        }
        catch (Win32Exception) { StartupFeedback.Text = "Administrator restart cancelled or unavailable. No changes were made. You can try again or close the app."; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy && _activeLogin is { } login)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Cancel sign-in and close the application?", "Sign-in in progress", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { _closeAfterLogin = true; login.Cancel(); }
        }
        else if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Finish the active operation and close its console before closing the application.", "Operation in progress");
        }
        else if (_registrationJournal.PendingCount > 0)
            e.Cancel = MessageBox.Show(this, $"{_registrationJournal.PendingCount} enrollment result(s) have not been saved. Closing loses their final outcomes; the on-disk attempts will remain unknown. Close anyway?", "Unsaved enrollment records", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes;
    }
}
