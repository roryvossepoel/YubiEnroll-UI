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
    private ActivityLogView? _cachedActivityView;
    private ActivityLogView _activityView => _cachedActivityView ??= new(_activityJournal);
    private readonly ActivityJournal _activityJournal;
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
    private readonly DeskPolicy? _policy;
    private Provider SelectedProvider => Tenants.SelectedItem as Provider
        ?? throw new InvalidOperationException("Select an Entra tenant first.");
    private EnrollmentProfile SelectedProfile => (Profiles.SelectedItem as ProfileChoice)?.Profile
        ?? throw new InvalidOperationException("Select a configuration profile first.");
    private UserCandidate SelectedUser => Users.SelectedItem as UserCandidate
        ?? throw new InvalidOperationException("Search and select a user first.");
    private CliRunner Runner => File.Exists(_exePath) ? new(_exePath)
        : throw new InvalidOperationException("Install Yubico YubiEnroll in its standard location, then return to Settings → Help & About → Required software.");

    public MainWindow(bool smokeTest = false, DeskPolicy? policy = null)
    {
        _smokeTest = smokeTest;
        _policy = policy;
        if (smokeTest)
        {
            var root = Path.Combine(Path.GetTempPath(), "YubiEnroll-log-test-" + Guid.NewGuid().ToString("N"));
            _activityJournal = new ActivityJournal(Path.Combine(root, "activity.json"), Path.Combine(root, "Documents"));
            _registrationJournal = new RegistrationJournal(Path.Combine(root, "preferences.json"), Path.Combine(root, "Documents"));
        }
        else
        {
            _registrationJournal = new RegistrationJournal(RegistrationJournal.DefaultSettingsPath, policy: policy?.Registration);
            _activityJournal = new ActivityJournal(policy: policy?.Activity);
            if (policy?.EmbeddedTerminal is bool embedded) EmbeddedTerminal = embedded;
        }
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
            issues.Add("YubiEnroll prerequisite not found. Install Yubico YubiEnroll in its standard location, then open Settings → Help & About → Required software.");
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
            StatusText.Text += "\nUse the same Windows account as your working YubiEnroll setup, then return to Settings → Help & About → Required software.";

        }
    }

    private string EmptyTenantGuidance => _policy?.ManagesTenants == true
        ? "No tenants available. Ask your administrator to configure a tenant."
        : "No tenants yet. Open Settings → Tenants → Add tenant to get started.";

    private string SetupOrReadyMessage() => _config?.Providers.Count > 0 ? ReadyMessage() : EmptyTenantGuidance;

    private string ReadyMessage() => IsAdministrator() ? "Ready · administrator · 0.11.5" : "Standard user · administrator rights required for key inspection and FIDO reset";

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
        EmptyTenants.Text = EmptyTenantGuidance;
        EmptyTenants.Visibility = hasTenants ? Visibility.Collapsed : Visibility.Visible;
        ContextBanner.Visibility = hasTenants ? Visibility.Visible : Visibility.Collapsed;
        ContextBanner.Text = provider == null ? "Select a tenant." :
            IsConnected ? $"Connected to {provider.Name}" : $"Not connected · {provider.Name}";
        ConnectButton.Content = IsConnected ? "Reconnect" : "Connect";
        ConnectButton.IsEnabled = provider != null && _cliReady;
        SearchPanel.IsEnabled = IsConnected;
        if (!IsConnected) SelectedUserText.Text = "Connect to a tenant first.";
        PreparePanel.IsEnabled = TargetReady;
        // Wizard rendering determines which action is available at the current step.
        SelectedUserText.Visibility = Visibility.Visible;
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
        SelectedUserText.Text = user == null ? "Select a user." : $"Selected: {user.DisplayName} · {user.UserPrincipalName}";
        if (user != null) StatusText.Text = "";
        UpdateSummary();
    }

    private async Task ActivateAsync(Provider provider, EnrollmentProfile? profile = null)
    {
        var before = ConfigReader.Read(_configPath);
        ConfigReader.EnsureUnchanged(before, provider, profile);
        if (before.ActiveProvider == provider.Name) return;
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
            Diagnostics.AppendText($"Application: Passkey Desk for YubiKeys 0.11.5\n{report.Diagnostic}\n\n");

            Record($"{operation} failed.", "Error", report.Diagnostic);
            if (showDialog)
                MessageBox.Show(this, report.Summary, "Passkey Desk for YubiKeys", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var service = new KeyResetService(a => runner.CaptureAsync(a), a => RunInteractiveAsync(path, a, target), () => NfcDiscovery.ReadAsync(runner));
            // The dedicated review page is the destructive confirmation. Native prompts remain enabled.
            var exit = resetExit = await service.ResetAsync(target, key =>
                _resetStep == 1 && _resetReviewTarget == key && _selectedKey?.Key == key && ResetAcknowledged.IsChecked == true);
            StatusText.Text = exit switch { null => "Reset cancelled.", 0 => "YubiKey Manager reported a successful FIDO reset. Reading target status…", _ => "Reset failed or was interrupted. Check the target key before retrying." };
            Record(StatusText.Text + " · serial " + target.Serial, exit is null or 0 ? "Info" : "Error");
        }, "Reset target key");
        ShowResetResult(target, resetCompleted ? resetExit : null, resetCompleted);
        InvalidateDeviceInventory();
        if (resetCompleted && resetExit == 0) await ReadResetPinAsync();
    }

    private ResetKey? _resultKey;
    private CancellationTokenSource? _pinRead;
    private void StopPinRead() { var pending = _pinRead; _pinRead = null; pending?.Cancel(); }
    private void ShowResetResult(ResetKey target, int? exit, bool completed)
    {
        StopPinRead(); _resultKey = target;
        _resetStep = 2; _resetReviewTarget = null; ResetAcknowledged.IsChecked = false;
        ResetResultTarget.Text = target.Label + (target.IsNfc ? "" : " · USB");
        ResetResultText.Text = exit == 0 ? "YubiKey Manager reported a successful FIDO reset." :
            completed && exit == null ? "Reset cancelled before the erase command." :
            "Reset not confirmed. Inspect this key before starting another reset.";
        ResetPinCheck.Text = exit == 0 ? "Checking the FIDO PIN on this key…" : "FIDO PIN status has not been checked.";
        ResetReadButton.IsEnabled = true; StatusText.Text = ""; RenderWizard();
    }

    private async void ResetRead_Click(object sender, RoutedEventArgs e) => await ReadResetPinAsync();
    private async Task ReadResetPinAsync()
    {
        if (_resetStep != 2 || _resultKey is not { } target || _pinRead != null) return;
        using var request = new CancellationTokenSource();
        _pinRead = request; ResetReadButton.IsEnabled = false;
        ResetPinCheck.Text = "Checking the FIDO PIN on this key…";
        try
        {
            // The probe checks serial and reader on the same connection used for the PIN read.
            var runner = new CliRunner(KeyResetService.FindExecutable());
            var caps = await FidoCapabilityProbe.ReadAsync(runner, target, request.Token);
            if (ReferenceEquals(_pinRead, request)) ResetPinCheck.Text = caps.PinConfigured switch
            {
                false => "FIDO PIN: not set (verified on this key).",
                true => "FIDO PIN: still set. Inspect this key before continuing.",
                _ => "FIDO PIN status could not be verified."
            };
        }
        catch (Exception) when (!request.IsCancellationRequested)
        {
            if (ReferenceEquals(_pinRead, request))
                ResetPinCheck.Text = "FIDO PIN status unavailable. Reconnect this key and select Refresh key status.";
        }
        catch (Exception) when (request.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_pinRead, request)) { _pinRead = null; ResetReadButton.IsEnabled = true; } }
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
            StatusText.Text = ManagePage.IsVisible || _selectedKey?.Key.IsNfc == true ? $"Signed in to {provider.Name}." : "Signed in. Disconnect the administrator key before enrollment; keep the chosen target connected.";
            // Sign-in does not change the inventory; hardware notifications schedule their own refresh.
        }
        else StatusText.Text = $"{_lastInteractiveFailure} Select Connect to try again.";
        UpdateSummary();
        if (IsConnected && !ManagePage.IsVisible && !BatchPage.IsVisible) _ = Dispatcher.BeginInvoke(() => Query.Focus());
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
        SelectedUserText.Text = users.Count == 0 ? "No users found." : users.Count == 1 ? "1 result. Select the user." : $"{users.Count} results. Select a user.";
        Record($"User search completed: {users.Count} user(s) found.");
        StatusText.Text = "";
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
        var target = _selectedKey?.Key ?? throw new InvalidOperationException("Choose a target key first.");
        int code;
        ClearReceipt(); _registrationResultMessage = ""; SetNameEditing(false);
        try { code = await ExecuteEnrollmentAsync(provider, profile, user, target, KeyName.Text); }
        catch (EnrollmentOutcomeUnknownException)
        {
            ShowEnrollmentResult(null);
            throw;
        }
        ShowEnrollmentResult(code, _lastInteractiveFailure);
    });

    private sealed class EnrollmentOutcomeUnknownException(Exception inner) : Exception("The enrollment outcome is unknown. Check Manage user passkeys before retrying.", inner) { }

    private async Task<int> ExecuteEnrollmentAsync(Provider provider, EnrollmentProfile? profile, UserCandidate user, ResetKey target,
        string name, EnrollmentBatch? batch = null)
    {
        RequireConnection();
        if (provider != SelectedProvider || (batch != null && (_addPasskey || batch.Tenant != provider || batch.Profile != profile || batch.Current?.User != user)))
            throw new InvalidOperationException("The selected tenant, profile or recipient changed.");
        var current = ConfigReader.Read(_configPath);
        ConfigReader.EnsureUnchanged(current, provider, profile);
        var setup = _addPasskey ? PasskeyAddition.Settings(_selectedKey?.Capabilities ?? throw new InvalidOperationException("Refresh the selected key.")) :
            DeskPolicy.ReadProfile(File.ReadAllText(_configPath), profile!.Name, _policy);
        if (batch != null && setup != _batchPolicy) throw new InvalidOperationException("The reviewed profile changed. Review the batch again.");
        var reset = setup.Reset;
        await CheckEnrollmentTargetAsync(target, setup, batch);
        // The Review step is the confirmation; never silently skip its fresh target checks.
        await ActivateAsync(provider, profile);
        if (batch != null)
        {
            var resolved = BatchRoster.Exact(user.UserPrincipalName, await LookupBatchUserAsync(provider, user.UserPrincipalName));
            if (resolved == null || !string.Equals(resolved.Id, user.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The reviewed account could not be verified. No registration was started.");
        }
        var fresh = await CheckEnrollmentTargetAsync(target, setup, batch);
        ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, profile);
        var registration = BeginRegistrationRecord(provider, user, target, setup, name);
        if (batch == null) _receipt = new(target, provider, user, setup, name, _addPasskey);
        _captureGeneratedPin = !_addPasskey && setup.RandomPin == true;
        Record($"Enrollment started · tenant {provider.Name} · user {user.Id} · serial {target.Serial} · profile {setup.Name} · reset {reset}.");
        StatusText.Text = _addPasskey ? "Enter the existing FIDO PIN in the native prompt. No new PIN is generated." : EmbeddedTerminal ? "Follow the prompts below. Record the PIN securely before returning." : "Follow the console prompts. Record the PIN securely, then press Enter to return.";
        int code;
        bool targetChanged = false;
        EnrollmentProfileLease? lease = null;
        try
        {
            if (_addPasskey || setup.MinPinLength == null || setup.RandomPinLength == null)
                lease = EnrollmentProfileLease.Create(_configPath, EnrollmentProfileLease.Resolve(setup,
                    fresh.Capabilities ?? throw new InvalidOperationException("Refresh the selected key.")));
            var operationProfile = lease?.Profile ?? profile!;
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, operationProfile);
            var arguments = EnrollmentCommand.Build(user.Id, operationProfile.Name, name, reset, target.Reader);
            if (ConfigReader.Read(_configPath).ActiveProvider != provider.Name)
                throw new InvalidOperationException("The active tenant changed. No registration was started.");
            batch?.Start(user, target, fresh.Pin, setup);
            var generation = _deviceGeneration;
            async Task<string?> FinishEnrollment(int nativeExit, string? pin, string? receiptSerial)
            {
                targetChanged = batch != null && generation != _deviceGeneration;
                if (batch != null && target.IsNfc && reset == true && nativeExit == 0)
                    targetChanged = !await VerifyReturnedNfcKeyAsync(target, EmbeddedTerminal || _captureGeneratedPin, receiptSerial);
                int? verifiedExit = targetChanged ? null : nativeExit;
                batch?.Complete(verifiedExit, pin);
                if (batch == null && verifiedExit == 0) _receiptPin = pin;
                var storedPin = verifiedExit == 0 && registration?.SaveGeneratedPin == true ? pin : null;
                var saved = CompleteRegistrationRecord(registration, verifiedExit, storedPin);
                var notice = targetChanged ? "The selected YubiKey could not be verified. Check its registration in Entra." : null;
                if (setup.RandomPin == true && !_addPasskey && (storedPin == null || !saved))
                {
                    var pinNotice = batch?.Current?.GeneratedPin != null
                        ? "The initial PIN is available in this batch. Record it before leaving the batch."
                        : nativeExit == 0 ? "The temporary PIN has not been saved. Record it securely before continuing."
                        : "If a temporary PIN was displayed, record it securely before continuing.";
                    notice = notice == null ? pinNotice : notice + " " + pinNotice;
                }
                return notice;
            }
            code = await RunInteractiveAsync(_exePath, arguments, target, FinishEnrollment, batch == null, setup);
        }
        catch (Exception ex)
        {
            if (batch?.Current?.Status == IssueStatus.InProgress) batch.Complete(null);
            CompleteRegistrationRecord(registration, null);
            Record($"Enrollment outcome unverified · tenant {provider.Name} · user {user.Id} · serial {target.Serial}.", "Warning");
            if (batch?.Current?.Status == IssueStatus.Pending) throw;
            throw new EnrollmentOutcomeUnknownException(ex);
        }
        finally
        {
            _captureGeneratedPin = false;
            try { lease?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { Record("Temporary enrollment profile could not be removed. The enrollment outcome is unchanged.", "Warning"); }
        }
        InvalidateDeviceInventory(); // Native enrollment can change PIN state without a device notification.
        if (batch != null) _deviceRefresh?.Start();
        Record((targetChanged ? "Key connection changed; enrollment outcome unknown." : code == 0 ? "Enrollment completed (reported by YubiEnroll)." : "Enrollment failed or was interrupted.") + $" · tenant {provider.Name} · user {user.Id} · serial {target.Serial}", code == 0 && !targetChanged ? "Info" : "Error", $"YubiEnroll exit code: {code}\n{_lastInteractiveFailure}");
        return code;
    }

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
            if (restarted == null) { StartupFeedback.Text = "Unable to start the elevated instance. Please try again. "; return; }
            Close();
        }
        catch (Win32Exception) { StartupFeedback.Text = "Administrator restart cancelled or unavailable. No changes were made. You can try again or close the app. "; }
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
        if (!e.Cancel && _hostedWindow is SettingsWindow settings) e.Cancel = !settings.ConfirmLeavePreferences();
        if (!e.Cancel) { StopPinRead(); ClearReceipt(); _batch?.ClearPins(); InitialPinCell.HidePin(); }
    }
}
