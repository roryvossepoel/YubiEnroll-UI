using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    // Explicit CI mode: never uses the real Windows profile, CLI installation, tenant or hardware.
    internal async Task RunStartupSmokeTestsAsync(string mockExecutable)
    {
        var root = Path.Combine(Path.GetTempPath(), "YubiEnroll-UI-test-" + Guid.NewGuid().ToString("N"));
        // Suppress native Windows error dialogs in unattended CI only.
        if (!SetThreadErrorMode(0x0001 | 0x0002 | 0x8000, out var previousErrorMode))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            await TestStartupRequirementsAsync(mockExecutable);
            await TestKeyToolsAsync();
            await TestEmbeddedTerminalAsync(mockExecutable);
            await TestExternalOutcomeAsync(mockExecutable);
            await TestLoginCancellationAsync(mockExecutable);
            TraceStartup("Initializing");
            await InitializeAsync(root, "");
            Assert(_config?.Providers.Count == 0 && Diagnostics.Text.Length == 0 && StatusText.Text.Contains("Add tenant"), "First run offers tenant setup without an error");
            Assert(WorkPanel.IsEnabled && !EnrollButton.IsEnabled, "Setup remains usable without enrollment");
            _enrollStep = TenantStepIndex; ShowPage(PreparePage); UpdateLayout();
            Assert(EmptyTenants.IsVisible && !TenantConnectionRow.IsVisible, "An empty tenant list shows Settings guidance instead of an empty connection form");
            CaptureWindow(this, "enroll-no-tenants.png");
            _enrollStep = 0; ShowPage(OverviewPage);
            var path = ConfigReader.FindPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            TraceStartup("Initializing");
            await InitializeAsync(root, "");
            Assert(_config?.Providers.Count == 0 && Diagnostics.Text.Length == 0, "Missing configuration file is an empty setup state");
            var setupDialog = new AddTenantWindow(ConfigSnapshot.Read(path)) { Owner = this };
            var saveOperation = setupDialog.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                setupDialog.TenantName.Text = "New tenant";
                setupDialog.TenantId.Text = "invalid";
                setupDialog.ClientId.Text = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                Assert(!setupDialog.TrySave() && !File.Exists(path), "Invalid form leaves configuration untouched");
                setupDialog.TenantId.Text = "11111111-2222-3333-4444-555555555555";
                Assert(setupDialog.TrySave(), "Tenant form saves a valid tenant");
                }
                finally { if (setupDialog.IsVisible) setupDialog.Close(); }
            });
            var saved = setupDialog.ShowDialog();
            await saveOperation.Task;
            Assert(saved == true, "Tenant dialog closes after successful save");
            await InitializeAsync(root, mockExecutable);
            Assert(_config?.Providers.Single().Name == "New tenant" && Profiles.Items.Count == 0 && _config.Providers.Single().Profile == "",
                "Tenant setup creates no profile or default association");
            ProfileConfigWriter.Save(ConfigSnapshot.Read(path), new ProfileSetup("standard"));
            LoadConfiguration(path);
            var beforeCancel = File.ReadAllText(path);
            var cancelDialog = new AddTenantWindow(ConfigSnapshot.Read(path)) { Owner = this };
            var cancelOperation = cancelDialog.Dispatcher.InvokeAsync(() => cancelDialog.Close());
            var cancelled = cancelDialog.ShowDialog();
            await cancelOperation.Task;
            Assert(cancelled != true && File.ReadAllText(path) == beforeCancel, "Cancel does not change configuration");
            var profileDialog = new ProfileWindow(ConfigSnapshot.Read(path)) { Owner = this };
            var profileOperation = profileDialog.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Assert(profileDialog.MinimumPinLength.Text.Length == 0 && profileDialog.ForcePinChange.IsChecked == false,
                        "New profile starts with basic settings suitable for older FIDO2 keys");
                    profileDialog.ManagedPreset.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(profileDialog.MinimumPinLength.Text == "6" && profileDialog.ForcePinChange.IsChecked == true && profileDialog.RequirementsText.Text.Contains("forced PIN change"),
                        "Managed preset sets policy and displays live capability requirements");
                    profileDialog.BasicPreset.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(profileDialog.MinimumPinLength.Text.Length == 0 && profileDialog.RandomPin.IsChecked == true && profileDialog.ForcePinChange.IsChecked == false,
                        "Basic preset keeps initial PIN generation separate from enforced PIN change");
                    profileDialog.ProfileName.Text = "Second profile";
                    profileDialog.MinimumPinLength.Text = "8";
                    profileDialog.GeneratedPinLength.Text = "6";
                    Assert(!profileDialog.TrySave(), "Profile form rejects generated PIN shorter than minimum");
                    CaptureWindow(profileDialog, "profile-editor.png");
                    profileDialog.GeneratedPinLength.Text = "10";
                    Assert(profileDialog.TrySave(), "Independent profile can be created");
                }
                finally { if (profileDialog.IsVisible) profileDialog.Close(); }
            });
            Assert(profileDialog.ShowDialog() == true, "Profile dialog closes after save");
            await profileOperation.Task;
            await InitializeAsync(root, mockExecutable);
            Assert(Profiles.Items.Count == 2 && _config?.Providers.Count == 1, "Multiple profiles load alongside existing tenant");
            var linkedProfile = "standard";
            var editDialog = new ProfileWindow(ConfigSnapshot.Read(path), linkedProfile) { Owner = this };
            var editOperation = editDialog.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Assert(editDialog.UsageText.Text.Contains("any tenant") && !editDialog.ProfileName.IsReadOnly, "Editor describes independent profiles and permits renaming");
                    editDialog.ResetProfile.IsChecked = true;
                    editDialog.ForcePinChange.IsChecked = true;
                    Assert(editDialog.TrySave(), "Existing profile can be edited");
                }
                finally { if (editDialog.IsVisible) editDialog.Close(); }
            });
            Assert(editDialog.ShowDialog() == true, "Edited profile saved");
            await editOperation.Task;
            Assert(ProfileConfigWriter.Read(File.ReadAllText(path), linkedProfile).ForcePinChange == true &&
                ConfigReader.Read(path).Profiles.Count == 2, "Editing preserves other profiles");
            var beforeProfileCancel = File.ReadAllText(path);
            var cancelProfile = new ProfileWindow(ConfigSnapshot.Read(path), linkedProfile) { Owner = this };
            var cancelProfileOperation = cancelProfile.Dispatcher.InvokeAsync(() => cancelProfile.Close());
            Assert(cancelProfile.ShowDialog() != true, "Profile editor cancels");
            await cancelProfileOperation.Task;
            Assert(File.ReadAllText(path) == beforeProfileCancel, "Profile cancellation leaves config unchanged");
            var tenantEditor = new AddTenantWindow(ConfigSnapshot.Read(path), "New tenant") { Owner = this };
            var tenantEditOperation = tenantEditor.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Assert(!tenantEditor.TenantName.IsReadOnly && tenantEditor.ClientId.Text.Length > 0, "Tenant editor loads existing values and permits renaming");
                    tenantEditor.ClientId.Text = "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee";
                    Assert(tenantEditor.TrySave(), "Tenant form edits existing tenant without creating duplicates");
                }
                finally { if (tenantEditor.IsVisible) tenantEditor.Close(); }
            });
            tenantEditor.ShowDialog();
            await tenantEditOperation.Task;
            var settings = new SettingsWindow(path, mockExecutable, "Mock CLI", _registrationJournal) { Owner = this };
            var settingsOperation = settings.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Assert(settings.SettingsTabs.Items.Count == 7 && settings.TenantList.Items.Count == 1 && settings.ProfileList.Items.Count == 2,
                        "Settings separates tenants, profiles, system and transfer");
                    var transfer = ConfigTransfer.Export(File.ReadAllText(path));
                    var beforePreview = File.ReadAllText(path);
                    settings.PreviewImport(transfer);
                    Assert(settings.ImportButton.IsEnabled && File.ReadAllText(path) == beforePreview, "Import preview validates without writing");
                    File.AppendAllText(path, "\n# external change");
                    try { settings.ApplyImport(); throw new Exception("Stale import was accepted"); }
                    catch (InvalidOperationException) { Assert(!settings.Changed, "Stale import cannot overwrite external changes"); }
                    settings.PreviewImport(transfer);
                    settings.ApplyImport();
                    Assert(settings.Changed && ConfigReader.Read(path).Profiles.Count == 2, "Import applies both tenants and profiles");
                    CaptureWindow(settings, "settings-tenants.png");
                    settings.SettingsTabs.SelectedIndex = 1;
                    CaptureWindow(settings, "settings-profiles.png");
                    settings.SetRequirements(new RequirementReport(true, new(true, "Installed · version 1.2.0"), new(true, "Installed · version 5.9.2"), mockExecutable));
                    settings.SettingsTabs.SelectedIndex = 2;
                    CaptureWindow(settings, "settings-system.png");
                    settings.SettingsTabs.SelectedIndex = 3;
                    CaptureWindow(settings, "settings-transfer.png");
                    settings.SettingsTabs.SelectedIndex = 4;
                    CaptureWindow(settings, "settings-about.png");
                    TestRegistrationLogView(settings);
                }
                finally { settings.Close(); }
            });
            settings.ShowDialog();
            await settingsOperation.Task;
            await InitializeAsync(root, mockExecutable);
            Assert(!SearchPanel.IsEnabled && !PreparePanel.IsEnabled && ConnectButton.IsEnabled, "Connect gates user search and preparation");
            TestGuidedWorkflow();
            var connectedBeforeProbe = _connectedProvider;
            var targetBeforeProbe = _selectedKey;
            var inventoryBeforeProbe = _knownKeys;
            await CheckRequirementsAsync(() => Task.FromResult(new RequirementReport(true, new(true, "YubiEnroll version: 1.2.0"), new(true, "Installed · version 5.9.2"), mockExecutable)));
            Assert(connectedBeforeProbe != null && ReferenceEquals(connectedBeforeProbe, _connectedProvider) &&
                ReferenceEquals(targetBeforeProbe, _selectedKey) && ReferenceEquals(inventoryBeforeProbe, _knownKeys),
                "Automatic software detection preserves the authenticated tenant and selected target");
            Users.ItemsSource = new[] { new UserCandidate("11111111-2222-3333-4444-555555555555", "Alex Example  alex@example.test") }; Users.SelectedIndex = 0;
            var keysCapture = Dispatcher.InvokeAsync(() =>
            {
                var keys = OwnedWindows.Cast<System.Windows.Window>().Single(w => w.Title == "Registered keys");
                CaptureWindow(keys, "registered-keys.png"); keys.Close();
            });
            ShowRegisteredKeys(SelectedUser, SelectedProvider, "Display name  Model  ID\nDemo key  YubiKey 5 NFC  synthetic-credential-id");
            await keysCapture.Task;
            TenantConfigWriter.Save(ConfigSnapshot.Read(path), new TenantSetup("Another tenant", "22222222-2222-3333-4444-555555555555",
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "http://localhost/yubienroll-redirect", linkedProfile, false));
            LoadConfiguration(path);
            _connectedProvider = SelectedProvider;
            UpdateSummary();
            Tenants.SelectedIndex = 1;
            Assert(!IsConnected && !SearchPanel.IsEnabled && !EnrollButton.IsEnabled && Users.SelectedItem == null,
                "Changing tenants invalidates connection and selected user");
            File.WriteAllText(path, "[broken-secret-marker");
            TraceStartup("Initializing");
            await InitializeAsync(root, "");
            Assert(Diagnostics.Text.Contains("InvalidDataException") && !Diagnostics.Text.Contains("broken-secret-marker"), "Malformed config without leaking contents");
            File.WriteAllText(path, """
                active_provider = "demo"
                [providers.demo]
                provider = "ENTRA"
                tenant_id = "11111111-2222-3333-4444-555555555555"
                profile = "default"
                [profiles.default]
                min_pin_length = 6
                """);
            var brokenExe = Path.Combine(root, "broken.exe");
            File.WriteAllText(brokenExe, "not a Windows executable");
            TraceStartup("Initializing");
            await InitializeAsync(root, brokenExe);
            TraceStartup(Diagnostics.Text); // Synthetic paths/config only in this explicit test mode.
            Assert(_config?.Providers.Count == 1, "Executable failure does not prevent configuration loading");
            Assert(Diagnostics.Text.Contains("Win32Exception") &&
                Diagnostics.Text.Contains("Step: Check YubiEnroll version") &&
                !Diagnostics.Text.Contains("Native error: n/a"), "Windows executable failure includes its native error code");
            TraceStartup("Initializing");
            await InitializeAsync(root, mockExecutable);
            Assert((StatusText.Text.StartsWith("Ready") || StatusText.Text.StartsWith("Standard user")) && _versionText.Contains("Mock YubiEnroll"), "Normal startup with valid configuration and mock CLI");
            Assert(Diagnostics.Text.Length == 0 && WorkPanel.IsEnabled, "Successful reload clears diagnostics and busy state");
            OpenSettings();
            Assert(HostedPage.Visibility == System.Windows.Visibility.Visible && HostedPage.Content != null, "Settings opens inside the main shell");
            UpdateLayout();
            var hosted = (SettingsWindow)_hostedWindow!;
            Assert(System.Windows.Documents.TextElement.GetFontSize((System.Windows.DependencyObject)HostedPage.Content!) == 14, "Hosted settings retains the shared body font size");
            hosted.SettingsTabs.SelectedIndex = 5;
            UpdateLayout();
            Assert(hosted.ActivityHost.Content == _activityView && _activityView.IsVisible, "Activity log is visible directly in its Settings tab");
            Assert(System.Windows.Documents.TextElement.GetFontWeight(_activityView) == System.Windows.FontWeights.Normal, "Selected tab does not make all activity text bold");
            hosted.SettingsTabs.SelectedIndex = 0;
            CaptureWindow(this, "shell-settings.png");
            ShowPage(OverviewPage);
            Assert(double.IsPositiveInfinity(MaxWidth) && double.IsPositiveInfinity(MaxHeight), "Main window does not constrain maximization to a reduced work area");
            _activityView.Add("Synthetic operation failed.", "Error", "HTTP status: 405");
            _activityView.Add("Synthetic summary only.", "Info", "   ");
            OpenSettings(5); UpdateLayout();
            var disclosures = VisualDescendants<System.Windows.Controls.Expander>(_activityView).ToArray();
            Assert(!disclosures.Single(x => x.Header.ToString()!.Contains("Synthetic summary only.")).IsVisible,
                "Activity entries with empty or whitespace details have no disclosure control");
            var diagnostic = disclosures.Single(x => x.Header.ToString()!.Contains("Synthetic operation failed."));
            Assert(diagnostic.IsVisible, "Activity entries with diagnostics retain a disclosure control");
            diagnostic.IsExpanded = true; UpdateLayout();
            Assert(VisualDescendants<System.Windows.Controls.TextBlock>(diagnostic).Any(x => x.IsVisible && x.Text == "HTTP status: 405"),
                "Expanding an activity diagnostic shows its actual details");
            CaptureWindow(this, "activity-log.png");
            ShowPage(OverviewPage);
            Width = 800; Height = 580;
            CaptureWindow(this, "overview-compact.png");
            ShowPage(PreparePage);
            CaptureWindow(this, "prepare-compact.png");
            AssertActionFooterVisible();
            Width = 1120; Height = 860;
            var panel = new TerminalPanel("Prepare Key — interactive operation", true);
            panel.Screen.Text = "Example output — synthetic test only\r\nTouch your YubiKey to continue.\r\n";
            TerminalOverlay.Content = panel; TerminalOverlay.Visibility = System.Windows.Visibility.Visible;
            CaptureWindow(this, "embedded-terminal.png");
            TerminalOverlay.Content = null; TerminalOverlay.Visibility = System.Windows.Visibility.Collapsed;

        }
        finally
        {
            SetThreadErrorMode(previousErrorMode, out _);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task TestStartupRequirementsAsync(string mockExecutable)
    {
        _startupChecksComplete = true;
        var calls = 0;
        Task<CliResult> ReadyVersion(string path)
        {
            calls++;
            return Task.FromResult(new CliResult(0, calls % 2 == 1 ? "YubiEnroll version: 1.2.0" : "YubiKey Manager (ykman) version: 5.9.2", ""));
        }
        var missing = await Requirements.ProbeAsync(false, "", "", ReadyVersion);
        Assert(!missing.Ready && !missing.Enrollment.Ready && !missing.Manager.Ready && calls == 0,
            "Missing tools never launch a version probe");
        ShowPage(StartupPage);
        await CheckRequirementsAsync(() => Task.FromResult(missing));
        UpdateLayout();
        Assert(StartupPage.IsVisible && StartupElevate.IsVisible && StartupRequirements.IsVisible && SettingsButton.IsEnabled,
            "Missing requirements show recovery actions and leave Settings accessible");
        ShowPage(PreparePage);
        Assert(StartupPage.IsVisible && !PreparePage.IsVisible && !_inventoryLoaded, "Missing requirements route task navigation to recovery without reading keys");
        CaptureWindow(this, "startup-missing.png");
        var toolsReady = await Requirements.ProbeAsync(false, mockExecutable, mockExecutable, ReadyVersion);
        Assert(!toolsReady.Ready && toolsReady.Enrollment.Ready && toolsReady.Manager.Ready && calls == 2,
            "Installed tools do not bypass the administrator requirement");
        await CheckRequirementsAsync(() => Task.FromResult(toolsReady));
        UpdateLayout();
        Assert(StartupElevate.IsVisible && !StartupRequirements.IsVisible && StartupPage.IsVisible,
            "Standard user sees only the elevation action when both tools are ready");
        CaptureWindow(this, "startup-admin.png");
        var outdated = await Requirements.ProbeAsync(true, mockExecutable, mockExecutable,
            _ => Task.FromResult(new CliResult(0, "YubiKey Manager (ykman) version: 5.8.0", "")));
        Assert(!outdated.Ready && !outdated.Manager.Ready && outdated.Manager.Detail.Contains("Unsupported"),
            "Installed unsupported ykman is not reported ready");
        var unrecognized = await Requirements.ProbeAsync(true, mockExecutable, mockExecutable,
            _ => Task.FromResult(new CliResult(0, "unrecognized output", "")));
        Assert(!unrecognized.Ready && !unrecognized.Enrollment.Ready, "Unrecognized YubiEnroll output is not reported ready");
        var failed = await Requirements.ProbeAsync(true, mockExecutable, mockExecutable,
            _ => Task.FromResult(new CliResult(2, "private error marker", "private error marker")));
        Assert(!failed.Ready && !failed.Manager.Detail.Contains("private error marker"),
            "Failed version checks are not ready and do not display raw output");
        var timedOut = await Requirements.ProbeAsync(true, mockExecutable, mockExecutable,
            _ => Task.FromException<CliResult>(new TimeoutException()));
        Assert(!timedOut.Ready && timedOut.Enrollment.Detail.Contains("timed out"), "A stalled version probe has an actionable state");
        var ready = toolsReady with { Administrator = true };
        await CheckRequirementsAsync(() => Task.FromResult(ready));
        Assert(OverviewPage.IsVisible && !StartupPage.IsVisible && !_inventoryLoaded,
            "Successful startup opens Home without discovering hardware");
        var settings = new SettingsWindow(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.toml"), mockExecutable, "Checking…", _registrationJournal);
        Host(settings);
        var prerequisiteVisits = 0;
        settings.PrerequisitesOpened += () => prerequisiteVisits++;
        settings.SettingsTabs.SelectedIndex = 2;
        Assert(prerequisiteVisits == 1, "Opening the Prerequisites tab requests automatic detection");
        // Preserve the actual object reference; probing must not reload the config or clear a session.
        var session = _connectedProvider;
        var inventory = _knownKeys;
        await CheckRequirementsAsync(() => Task.FromResult(ready));
        Assert(ReferenceEquals(_connectedProvider, session) && ReferenceEquals(_knownKeys, inventory) && HostedPage.IsVisible,
            "Returning to Prerequisites checks software without changing login or key inventory");
        Assert(settings.ManagerInfo.Text.Contains("5.9.2") && settings.VersionInfo.Text == "Version 1.2.0" && settings.EnrollmentStatus.Text == "Installed" && settings.RuntimeVersion.Text.StartsWith("Version 10."),
            "Prerequisite cards display the latest detected tool status");
        CaptureWindow(this, "prerequisites-ready.png");
        await CheckRequirementsAsync(() => Task.FromResult(missing with { Administrator = true }));
        Assert(settings.ManagerStatus.Text == "Not installed" && ((System.Windows.Controls.Border)settings.ManagerInfo.Parent).Visibility == System.Windows.Visibility.Collapsed, "Removal is reflected in the prerequisite cards");
        CaptureWindow(this, "prerequisites-missing.png");
        _requirements = null; _startupChecksComplete = false;
        ShowPage(OverviewPage);
    }

    private void TestGuidedWorkflow()
    {
        var e = new System.Windows.RoutedEventArgs();
        var target = new KeyStatus(new ResetKey("12345678", "YubiKey 5C NFC (5.7.1)"), FidoPinState.Blocked,
            new FidoCapabilities("12345678", "5.7.1", true, true, true, true, false, false, 4, 63, false, true, false));
        var admin = new KeyStatus(new ResetKey("87654321", "YubiKey 5 NFC (5.7.1)"), FidoPinState.Set);
        ShowPage(OverviewPage);
        Assert(OverviewPage.IsVisible && !PreparePage.IsVisible && !ResetPage.IsVisible && !StatusBar.IsVisible,
            "Home starts with task choices and no device form or technical footer");
        Width = 1180; Height = 840; CaptureWindow(this, "home.png");
        Assert(FindResource("BrandMark") is System.Windows.Media.DrawingImage &&
            new[] { "HomeGlyph", "EnrollGlyph", "ResetGlyph", "SettingsGlyph" }.All(key => FindResource(key) is System.Windows.Media.DrawingImage),
            "The shared app mark and navigation icons are vector resources");
        CaptureWindow(this, "home-150-percent.png", 144);
        CaptureWindow(this, "home-200-percent.png", 192);
        ShowPage(PreparePage);
        ApplyInventory(new[] { target, admin }, 0);
        Profiles.SelectedIndex = 0;
        Assert(_selectedKey == null && !EnrollNext.IsEnabled, "Discovery never selects a target automatically");
        EnrollmentKeys.SelectedIndex = 0;
        Assert(_selectedKey == null && OverviewKeys.SelectedItem is KeyStatus candidate && candidate == target && EnrollNext.IsEnabled,
            "Both task lists share an unconfirmed candidate");
        CaptureWindow(this, "enroll-key.png");
        EnrollNext_Click(this, e);
        Assert(_selectedKey == target && _enrollStep == 1 && ProfileStep.IsVisible && PreparePanel.IsEnabled && !TenantStep.IsVisible,
            "Key selection leads to an enabled profile step before authentication");
        Assert(EnrollNext.IsEnabled && !IsConnected, "Compatible profile can be chosen without a connected tenant");
        CaptureWindow(this, "enroll-profile.png");
        EnrollNext_Click(this, e);
        Assert(_enrollStep == 2 && TenantStep.IsVisible && ConnectButton.IsEnabled && !EnrollNext.IsEnabled, "Tenant follows profile and requires login");
        EnrollNext_Click(this, e);
        Assert(_enrollStep == 2, "Cannot skip authentication through the event handler");
        _connectedProvider = SelectedProvider; UpdateSummary();
        Assert(EnrollNext.IsEnabled && EnrollmentTargetNotice.Text.Contains(admin.Key.Serial), "Signed-in tenant identifies other keys to disconnect");
        CaptureWindow(this, "enroll-tenant.png");
        EnrollNext_Click(this, e);
        Assert(_enrollStep == 3 && UserCard.IsVisible && !TenantStep.IsVisible && !EnrollNext.IsEnabled, "Recipient is a separate gated step");
        Users.ItemsSource = new[] { new UserCandidate("11111111-2222-3333-4444-555555555555", "Alex Example  alex@example.test  alex@example.test") { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test", Email = "alex@example.test" } };
        Users.SelectedIndex = 0;
        CaptureWindow(this, "enroll-user.png");
        EnrollNext_Click(this, e);
        Assert(_enrollStep == 4 && PreparationCard.IsVisible && !Profiles.IsVisible && !KeyName.IsVisible && !EnrollButton.IsEnabled && ReviewTargetNotice.Text.Contains(admin.Key.Serial),
            "Review blocks enrollment while another key is attached");
        ApplyInventory(new[] { admin }, 0);
        Assert(_selectedKey == null && _selectionSerial == target.Key.Serial && !EnrollButton.IsEnabled, "Missing target is never replaced by the administrator key");
        ApplyInventory(new[] { target }, 0);
        Assert(_selectedKey == target && _enrollStep == 0 && !EnrollButton.IsEnabled, "Target reconnect requires choosing the key again before enrollment");
        EnrollNext_Click(this, e); EnrollNext_Click(this, e); EnrollNext_Click(this, e); EnrollNext_Click(this, e);
        Assert(_enrollStep == 4 && EnrollButton.IsEnabled, "Review is available again only after confirming the reconnected target");
        InvalidateDeviceInventory(); UpdateSummary();
        Assert(!EnrollButton.IsEnabled && !ResetTargetButton.IsEnabled && !EnrollNext.IsEnabled, "USB change immediately invalidates action readiness");
        ApplyInventory(new[] { target }, 0);
        KeyName.Text = "My custom key";
        ApplyInventory(new[] { target with { Pin = FidoPinState.Set } }, 0);
        Assert(KeyName.Text == "My custom key", "Refresh preserves the custom registration name");
        KeyName.Clear(); SuggestRegistrationName();
        Assert(KeyName.Text == "YubiKey 5C NFC (12345678)", "Default name contains model and serial without firmware");
        CaptureWindow(this, "enroll-review.png"); AssertActionFooterVisible();
        ApplyInventory(new[] { target with { Capabilities = target.Capabilities! with { PinPolicy = false } } }, 0);
        Assert(!EnrollButton.IsEnabled && ProfileCompatibilityText.Text.Contains("Forced PIN change"), "Unsupported PIN policy blocks enrollment with an actionable explanation");
        CaptureWindow(this, "profile-incompatible.png");
        ApplyInventory(new[] { target with { Capabilities = null } }, 0);
        Assert(!EnrollButton.IsEnabled && ProfileCompatibilityText.Text.Contains("unavailable"), "Unreadable capabilities cannot reuse an old successful check");
        ApplyInventory(new[] { target }, 0);
        Assert(EnrollButton.IsEnabled, "A fresh compatible capability snapshot restores enrollment");
        var staleGeneration = _deviceGeneration;
        ScheduleDeviceRefresh();
        Assert(!TryApplyInventory(new[] { target }, 0, staleGeneration) && _inventoryDirty && !EnrollButton.IsEnabled,
            "USB changes discard an in-flight discovery snapshot instead of re-enabling stale actions");
        TryApplyInventory(new[] { target }, 0, _deviceGeneration);
        Assert(!_inventoryDirty && EnrollButton.IsEnabled, "The latest USB inventory restores readiness when the confirmed target remains present");
        var cached = _selectedKey;
        ShowPage(OverviewPage); ShowPage(PreparePage);
        Assert(ReferenceEquals(cached, _selectedKey) && _enrollStep == 4 && !_busy, "Navigation preserves target and step without reading hardware");
        Assert(TaskbarIdentity.HasWindowIcons(this), "Published app exposes the shared icon at both native sizes");
        // A reset is an independent task and cannot start from enrollment selection alone.
        ShowPage(ResetPage); CaptureWindow(this, "reset-key.png");
        Assert(!ResetTargetButton.IsVisible && ResetNext.IsEnabled, "Reset begins with selection rather than a destructive button");
        ResetNext_Click(this, e);
        Assert(_resetStep == 1 && _resetReviewTarget == target.Key && !ResetTargetButton.IsEnabled, "Reset review captures exact target and requires acknowledgment");
        ResetAcknowledged.IsChecked = true;
        Assert(ResetTargetButton.IsEnabled == IsAdministrator(), "Reset action also requires Windows administrator access");
        ResetTargetButton.ApplyTemplate();
        var resetSurface = (System.Windows.Controls.Border)ResetTargetButton.Template.FindName("DangerSurface", ResetTargetButton);
        var resetLabel = (System.Windows.Controls.TextBlock)resetSurface.Child;
        Assert(((System.Windows.Media.SolidColorBrush)resetLabel.Foreground).Color == (ResetTargetButton.IsEnabled ? System.Windows.Media.Colors.White : System.Windows.Media.Color.FromRgb(82, 96, 107)), "Erase label has explicit readable contrast");
        CaptureWindow(this, "reset-confirm.png");
        InvalidateDeviceInventory();
        Assert(ResetAcknowledged.IsChecked == false && !ResetTargetButton.IsEnabled, "Device change revokes destructive acknowledgment");
        ApplyInventory(new[] { admin }, 0);
        Assert(_resetStep == 0 && _resetReviewTarget == null && ResetAcknowledged.IsChecked == false && !ResetTargetButton.IsVisible && EnrollmentKeys.SelectedItem == null,
            "Removing the reviewed target returns to key selection, clears confirmation and never selects the admin key");
        CaptureWindow(this, "reset-target-removed.png");
        ApplyInventory(new[] { target, admin }, 0);
        Assert(_resetStep == 0 && !ResetTargetButton.IsEnabled, "Reconnecting a key does not restore the old destructive confirmation");
        ResetNext_Click(this, e);
        ResetBack_Click(this, e);
        Assert(_resetStep == 0 && !ResetTargetButton.IsVisible, "Back from reset confirmation performs no operation");
        var tenant = _connectedProvider; var profile = Profiles.SelectedItem;
        OverviewKeys.SelectedIndex = 1; ResetNext_Click(this, e);
        Assert(_selectedKey == admin && Users.SelectedItem == null && KeyName.Text == admin.Key.RegistrationName,
            "Confirming a different target clears the previous recipient and name");
        Assert(_connectedProvider == tenant && ReferenceEquals(Profiles.SelectedItem, profile), "Changing target retains tenant login and profile");
        ResetResultText.Text = "Synthetic result: reset reported successful. Read-back: target PIN is not set. Entra registrations remain.";
        _resetStep = 2; RenderWizard(); CaptureWindow(this, "reset-result.png");
        ResetNext_Click(this, e);
        Assert(_resetStep == 0 && _selectedKey == null && !ResetNext.IsEnabled, "Reset another key requires a fresh explicit selection");
        ShowPage(PreparePage);
        EnrollmentKeys.SelectedIndex = 0; EnrollNext_Click(this, e); EnrollNext_Click(this, e);
        Users.ItemsSource = new[] { new UserCandidate("11111111-2222-3333-4444-555555555555", "Alex Example  alex@example.test  alex@example.test") { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test", Email = "alex@example.test" } }; Users.SelectedIndex = 0;
        EnrollNext_Click(this, e); ApplyInventory(new[] { target }, 0);
        ShowEnrollmentResult("Synthetic result: YubiEnroll reported success. Review registered keys and test sign-in.");
        CaptureWindow(this, "enroll-result.png");
        Assert(_enrollStep == 5 && !EnrollButton.IsVisible && EnrollNext.IsEnabled, "Enrollment result offers another key without repeating enrollment");
        AddPasskey_Click(this, e); UpdateLayout();
        Assert(_addPasskey && _enrollStep == 0 && AddPasskeyButton.Tag as string == "active", "Add passkey is a separate task starting at key selection");
        ApplyInventory(new[] { target }, 0); EnrollmentKeys.SelectedIndex = 0;
        Assert(!EnrollNext.IsEnabled, "Add passkey rejects a blocked PIN without proposing an automatic reset");
        var existing = target with { Pin = FidoPinState.Set };
        ApplyInventory(new[] { existing }, 0); EnrollmentKeys.SelectedIndex = 0;
        Assert(EnrollNext.IsEnabled, "Existing working PIN permits the addition flow");
        EnrollNext_Click(this, e); EnrollNext_Click(this, e);
        Users.ItemsSource = new[] { new UserCandidate("11111111-2222-3333-4444-555555555555", "Alex") { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test", Email = "alex@example.test" } }; Users.SelectedIndex = 0;
        Profiles.SelectedItem = null; EnrollNext_Click(this, e); UpdateLayout();
        Assert(_enrollStep == 3 && PreparationCard.IsVisible && !ProfileStep.IsVisible && EnrollButton.IsEnabled && !ReviewWarningBox.IsVisible,
            "Adding a passkey skips profile selection and reset warnings, and does not need any selected profile");
        CaptureWindow(this, "add-passkey-review.png"); AssertActionFooterVisible();
        ApplyInventory(new[] { existing with { Capabilities = existing.Capabilities! with { ForcePinChange = true } } }, 0);
        Assert(!EnrollButton.IsEnabled, "An existing required PIN change blocks preservation mode");
        ApplyInventory(new[] { existing }, 0);
        Prepare_Click(this, e);
        Assert(!_addPasskey && _enrollStep == 0 && !EnrollButton.IsVisible, "Switching tasks requires a fresh key choice");
        var nfc = existing with { Key = existing.Key with { Reader = "HID Global OMNIKEY 5022 Smart Card Reader 0" },
            Capabilities = existing.Capabilities! with { IsNfc = true, ResetOverNfc = true } };
        ApplyInventory([nfc, admin], 0); EnrollmentKeys.SelectedIndex = 0;
        CaptureWindow(this, "nfc-key-selection.png");
        EnrollNext_Click(this, e); Profiles.SelectedIndex = 0;
        Assert(_selectedKey == nfc && _reviewValid, "NFC target supports the same profile-first setup flow");
        AddPasskey_Click(this, e); ApplyInventory([nfc, admin], 0); EnrollmentKeys.SelectedIndex = 0;
        EnrollNext_Click(this, e); EnrollNext_Click(this, e);
        Users.ItemsSource = new[] { new UserCandidate("11111111-2222-3333-4444-555555555555", "Alex") { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test" } }; Users.SelectedIndex = 0;
        EnrollNext_Click(this, e);
        Assert(EnrollButton.IsEnabled && _selectedKey == nfc, "NFC passkey addition can retain a USB admin key");
        CaptureWindow(this, "nfc-passkey-review.png");
        ShowPage(ResetPage); _resetStep = 0; ResetNext_Click(this, e); ResetAcknowledged.IsChecked = true;
        Assert(ResetTargetButton.IsEnabled && ResetConnectionHelp.Text.Contains("NFC reader"), "NFC reset review shows reader instructions");
        CaptureWindow(this, "nfc-reset-review.png");
        ApplyInventory([existing, admin], 0);
        Assert(_selectedKey == null && !ResetTargetButton.IsEnabled && ResetAcknowledged.IsChecked != true,
            "NFC target removal cannot switch to a USB key with the same serial");
        ApplyInventory([nfc with { Key = nfc.Key with { Serial = "11112222" } }], 0);
        Assert(_selectedKey == null && !EnrollButton.IsEnabled, "Replacing a card on the same reader requires explicit selection");
        Assert(NfcPresenceMonitor.Snapshot().Length > 0, "Windows PC/SC presence probe handles readers or absent service without opening a card");
        BeginNextKey(); ShowPage(PreparePage);
        var emptyReader = new NfcReaderStatus(nfc.Key.Reader!, true, "empty");
        ApplyInventory([], 0, [emptyReader]);
        Assert(EnrollmentReaderStatus.IsVisible && ResetReaderStatus.Children.Count == 1 && EnrollmentKeys.Items.Count == 0 &&
            !EnrollNext.IsEnabled && KeyInfo.Text == "Connect a YubiKey by USB or place it on the NFC reader.",
            "Empty connected NFC reader has a named status and one USB/NFC instruction");
        CaptureWindow(this, "nfc-reader-empty.png");
        ApplyInventory([nfc], 0, [emptyReader with { State = "ready" }, new("Internal contact reader", false, "unavailable"),
            new("Windows Hello for Business 1", true, "unavailable")]);
        Assert(EnrollmentReaderStatus.Children.Count == 1 && KeyInfo.Visibility == System.Windows.Visibility.Collapsed,
            "Unrelated reader failure does not warn about a successfully discovered NFC key");
        Assert(EnrollmentReaderStatus.ActualHeight < 50 &&
            !VisualDescendants<TextBlock>(EnrollmentReaderStatus).Any(t => t.Text.Contains("Windows Hello") || t.Text == "YubiKey detected"),
            "Reader presence is compact and excludes Windows Hello and redundant detection text");
        CaptureWindow(this, "nfc-reader-ready.png");
        EnrollmentKeys.SelectedIndex = 0;
        InvalidateDeviceInventory(); UpdateLayout();
        Assert(EnrollmentKeys.Items.Count == 1 && EnrollmentReaderStatus.Children.Count == 1 &&
            !EnrollNext.IsEnabled && !ResetNext.IsEnabled && !EnrollButton.IsEnabled &&
            KeyInfo.Visibility == Visibility.Collapsed && FooterInstruction.Visibility == Visibility.Collapsed &&
            VisualDescendants<TextBlock>(this).Count(t => t.IsVisible && t.Text == "Checking connected keys…") == 1,
            "Refreshing preserves inventory, gates actions and shows one progress message beside Refresh");
        CaptureWindow(this, "nfc-reader-refreshing.png");
        ApplyInventory([existing], 0, []); EnrollmentKeys.SelectedIndex = 0;
        Assert(EnrollmentReaderStatus.Visibility == System.Windows.Visibility.Collapsed && ResetReaderStatus.Visibility == System.Windows.Visibility.Collapsed && EnrollNext.IsEnabled,
            "USB remains selectable with no NFC block when the reader is disconnected");
        CaptureWindow(this, "usb-without-nfc-reader.png");
        ApplyInventory([], 0, [new("Windows Hello for Business 1", true, "unavailable")]);
        Assert(EnrollmentReaderStatus.Visibility == System.Windows.Visibility.Collapsed && !KeyInfo.Text.Contains("NFC") &&
            !EnrollNext.IsEnabled && EnrollmentInventoryProgress.Visibility == Visibility.Collapsed,
            "No reader and no key has simple USB guidance without NFC text");
        CaptureWindow(this, "no-reader-no-key.png");

        ShowPage(OverviewPage);

    }

    private void AssertActionFooterVisible()
    {
        UpdateLayout();
        var root = (System.Windows.FrameworkElement)Content;
        var action = EnrollButton.IsVisible ? EnrollButton : EnrollNext;
        var bottom = action.TranslatePoint(new System.Windows.Point(0, action.ActualHeight), root);
        Assert(action.IsVisible && action.ActualHeight > 0 && bottom.Y <= root.ActualHeight,
            "Prepare action remains visible outside the scrolling form");
    }

    private async Task TestEmbeddedTerminalAsync(string mockExecutable)
    {
        using var terminal = new PseudoConsole();
        var output = new System.Text.StringBuilder();
        var sent = false;
        var sentHidden = false;
        terminal.Output += text =>
        {
            lock (output)
            {
                output.Append(text);
                if (!sent && output.ToString().Contains("TERMINAL_READY")) { sent = true; terminal.Write("confirm\r"); }
                if (!sentHidden && output.ToString().Contains("HIDDEN_READY")) { sentHidden = true; terminal.Write("synthetic-pin\r"); }
            }
        };
        var run = terminal.RunAsync(mockExecutable, ["--terminal-probe"]);
        var code = await run.WaitAsync(TimeSpan.FromSeconds(20)); // Synthetic test only; production operations have no timeout.
        Assert(code == 7 && output.ToString().Contains("INPUT_OK"), "Embedded ConPTY round-trips interactive input and preserves exit code");
        Assert(output.ToString().Contains("HIDDEN_OK") && !output.ToString().Contains("synthetic-pin"), "Native no-echo input is not displayed by embedded terminal");
        // Test the actual live renderer too, not just transport output after process completion.
        var panel = new TerminalPanel("Live prompt rendering", false);
        TerminalOverlay.Content = panel; TerminalOverlay.Visibility = System.Windows.Visibility.Visible;
        var live = panel.RunAsync(mockExecutable, ["--terminal-probe"]);
        async Task WaitForText(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!panel.Screen.Text.Contains(expected) && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert(panel.Screen.Text.Contains(expected), "Live terminal displays " + expected + " before input/process exit");
        }
        await WaitForText("TERMINAL_READY");
        CaptureWindow(this, "terminal-live-prompt.png");
        panel.SendInput("confirm\r");
        await WaitForText("HIDDEN_READY");
        panel.SendInput("synthetic-pin\r");
        await WaitForText("HIDDEN_OK");
        while (!live.IsCompleted) { panel.DismissCompleted(); await Task.Delay(50); }
        Assert(await live == 7 && !panel.Screen.Text.Contains("synthetic-pin"), "Live renderer retains outcome and clears hidden-input session");
        TerminalOverlay.Content = null; TerminalOverlay.Visibility = System.Windows.Visibility.Collapsed;
        foreach (var capture in new[] { false, true })
        {
            var pinPanel = new TerminalPanel("Synthetic PIN summary", true, captureSerial: capture ? "00123456" : null);
            TerminalOverlay.Content = pinPanel; TerminalOverlay.Visibility = System.Windows.Visibility.Visible;
            var pinRun = pinPanel.RunAsync(mockExecutable, ["--pin-summary-probe"]);
            var end = DateTime.UtcNow.AddSeconds(10);
            while (pinPanel.CompletedExitCode == null && DateTime.UtcNow < end) await Task.Delay(50);
            Assert(pinPanel.CompletedExitCode == 0 && pinPanel.GeneratedPin == (capture ? "001234" : null),
                "Published terminal captures the generated summary PIN only with explicit opt-in: " + capture);
            pinPanel.DismissCompleted(); await pinRun;
        }
        TerminalOverlay.Content = null; TerminalOverlay.Visibility = System.Windows.Visibility.Collapsed;
        var screen = new TerminalScreen(); screen.Feed(output.ToString());
        Assert(screen.Text.Contains("VT_OK"), "Embedded terminal renders native VT output");
    }

    private async Task TestExternalOutcomeAsync(string mockExecutable)
    {
        var result = Path.Combine(Path.GetTempPath(), "yubienroll-test-exit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = Path.Combine(AppContext.BaseDirectory, "YubiEnroll.ConsoleHost.exe");
            using var process = System.Diagnostics.Process.Start(CliRunner.StartInfo(host, ["--result-file", result, mockExecutable, "--exit-probe"]))!;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                string recorded = "";
                while (recorded.Length == 0 && DateTime.UtcNow < deadline)
                {
                    // The host creates and writes this file before waiting for Enter.
                    // Existence alone does not mean its write handle has closed.
                    try { if (File.Exists(result)) recorded = File.ReadAllText(result); }
                    catch (IOException) { }
                    if (recorded.Length == 0) await Task.Delay(100);
                }
                Assert(recorded == "7", "External host records the native result before waiting for Enter");
                process.Kill(); await process.WaitForExitAsync();
                Assert(CliRunner.ReadNativeOutcome(result, process.ExitCode) == 7, "Closing the host preserves the recorded native result");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally
        {
            // Cleanup must not hide an assertion or process failure.
            try { File.Delete(result); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var terminal = new TerminalPanel("Login completion test", false, true);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = System.Windows.Visibility.Visible;
        try { Assert(await terminal.RunAsync(mockExecutable, ["--version"]).WaitAsync(TimeSpan.FromSeconds(10)) == 0, "Successful embedded login returns without a Done click"); }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = System.Windows.Visibility.Collapsed; }
    }

    internal async Task RunNativeTerminalCheckAsync(string executable)
    {
        var panel = new TerminalPanel("Native CLI rendering check", false);
        TerminalOverlay.Content = panel; TerminalOverlay.Visibility = System.Windows.Visibility.Visible;
        var run = panel.RunAsync(executable, ["--help"]); // No login, configuration mutation or device operation.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (panel.CompletedExitCode == null && DateTime.UtcNow < deadline) await Task.Delay(50);
        CaptureWindow(this, "terminal-native-help.png");
        Assert(panel.CompletedExitCode == 0 && panel.Screen.Text.Contains("Usage:"), "Actual YubiEnroll help renders inside the published terminal control");
        panel.DismissCompleted();
        Assert(await run == 0, "Native terminal check returns successfully");
        TerminalOverlay.Content = null; TerminalOverlay.Visibility = System.Windows.Visibility.Collapsed;
    }

    private static IEnumerable<T> VisualDescendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static void CaptureWindow(System.Windows.Window window, string file, int dpi = 96)
    {
        // Let the native resize and deferred templates settle before sampling the actual client area.
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        var content = (System.Windows.FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96), (int)Math.Ceiling(content.ActualHeight * dpi / 96), dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var rect = new System.Windows.Rect(0, 0, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(window.Background, null, rect);
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(content) { Stretch = System.Windows.Media.Stretch.Fill }, null, rect);
        }
        bitmap.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var folder = Path.Combine(AppContext.BaseDirectory, "visual-checks");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, file));
        encoder.Save(stream);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint mode, out uint previousMode);

    internal static void TraceStartup(string message) =>
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup-smoke-test.txt"), message + Environment.NewLine);

    private static void Assert(bool condition, string label)
    {
        TraceStartup((condition ? "PASS: " : "FAIL: ") + label);
        if (!condition) throw new InvalidOperationException("Startup smoke test failed: " + label);
    }
}
