using System.IO;
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
            await TestKeyToolsAsync();
            await TestEmbeddedTerminalAsync(mockExecutable);
            await TestExternalOutcomeAsync(mockExecutable);
            TraceStartup("Initializing");
            await InitializeAsync(root, "");
            Assert(_config?.Providers.Count == 0 && Diagnostics.Text.Length == 0 && StatusText.Text.Contains("Add tenant"), "First run offers tenant setup without an error");
            Assert(WorkPanel.IsEnabled && !EnrollButton.IsEnabled, "Setup remains usable without enrollment");
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
            Assert(_config?.Providers.Single().Name == "New tenant" && Profiles.Items.Count == 1,
                "First tenant and profile load after setup");
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
                    profileDialog.ProfileName.Text = "Second profile";
                    profileDialog.MinimumPinLength.Text = "8";
                    profileDialog.GeneratedPinLength.Text = "6";
                    Assert(!profileDialog.TrySave(), "Profile form rejects generated PIN shorter than minimum");
                    profileDialog.GeneratedPinLength.Text = "10";
                    Assert(profileDialog.TrySave(), "Independent profile can be created");
                }
                finally { if (profileDialog.IsVisible) profileDialog.Close(); }
            });
            Assert(profileDialog.ShowDialog() == true, "Profile dialog closes after save");
            await profileOperation.Task;
            await InitializeAsync(root, mockExecutable);
            Assert(Profiles.Items.Count == 2 && _config?.Providers.Count == 1, "Multiple profiles load alongside existing tenant");
            var linkedProfile = _config!.Providers.Single().Profile;
            var editDialog = new ProfileWindow(ConfigSnapshot.Read(path), linkedProfile) { Owner = this };
            var editOperation = editDialog.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Assert(editDialog.LinkedTenants.Items.Count == 1 && !editDialog.ProfileName.IsReadOnly, "Editor shows linked tenants and permits profile renaming");
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
            var settings = new SettingsWindow(path, mockExecutable, "Mock CLI") { Owner = this };
            var settingsOperation = settings.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Assert(settings.SettingsTabs.Items.Count == 6 && settings.TenantList.Items.Count == 1 && settings.ProfileList.Items.Count == 2,
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
                    settings.SettingsTabs.SelectedIndex = 2;
                    CaptureWindow(settings, "settings-system.png");
                    settings.SettingsTabs.SelectedIndex = 3;
                    CaptureWindow(settings, "settings-transfer.png");
                    settings.SettingsTabs.SelectedIndex = 4;
                    CaptureWindow(settings, "settings-about.png");
                }
                finally { settings.Close(); }
            });
            settings.ShowDialog();
            await settingsOperation.Task;
            await InitializeAsync(root, mockExecutable);
            Assert(!SearchPanel.IsEnabled && !PreparePanel.IsEnabled && ConnectButton.IsEnabled, "Connect gates user search and preparation");
            _connectedProvider = SelectedProvider; // Synthetic completed connection; never performs real login.
            UpdateSummary();
            Assert(SearchPanel.IsEnabled && !PreparePanel.IsEnabled, "User selection required after connection");
            Users.ItemsSource = new[] { new UserCandidate("11111111-2222-3333-4444-555555555555", "Alex Example  alex@example.test") };
            Users.SelectedIndex = 0;
            Assert(PreparePanel.IsEnabled && EnrollButton.IsEnabled && ResetNotice.Text.Contains("erases"), "Profile reset is visible before enrollment");
            ShowPage(OverviewPage);
            ReadinessText.Text = "YubiEnroll: installed · Administrator: yes\nConfiguration: 1 tenant · 2 profiles";
            KeyInfo.Text = "Connect a YubiKey to get started.";
            CaptureWindow(this, "overview.png");
            ShowPage(PreparePage);
            CaptureWindow(this, "enrollment.png");
            var reviewCapture = Dispatcher.InvokeAsync(() =>
            {
                var review = OwnedWindows.Cast<System.Windows.Window>().Single(w => w.Title == "Confirm enrollment");
                CaptureWindow(review, "enrollment-review.png");
                review.Close();
            });
            Assert(!ConfirmEnrollment(SelectedProvider, SelectedProfile, SelectedUser, "This profile erases existing FIDO credentials."), "Closing enrollment review cancels without launching a tool");
            await reviewCapture.Task;
            var keysCapture = Dispatcher.InvokeAsync(() =>
            {
                var keys = OwnedWindows.Cast<System.Windows.Window>().Single(w => w.Title == "Registered keys");
                CaptureWindow(keys, "registered-keys.png");
                keys.Close();
            });
            ShowRegisteredKeys(SelectedUser, SelectedProvider, "Display name  Model  ID\nDemo key  YubiKey 5 NFC  synthetic-credential-id");
            await keysCapture.Task;
            AssertActionFooterVisible();
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
            CaptureWindow(this, "shell-settings.png");
            ShowPage(OverviewPage);
            Assert(double.IsPositiveInfinity(MaxWidth) && double.IsPositiveInfinity(MaxHeight), "Main window does not constrain maximization to a reduced work area");
            _activityView.Add("Synthetic operation failed.", "Error", "HTTP status: 405");
            ShowPage(DetailsPage); CaptureWindow(this, "activity-log.png");
            ShowPage(OverviewPage);
            Width = 800; Height = 580;
            CaptureWindow(this, "overview-compact.png");
            ShowPage(PreparePage);
            CaptureWindow(this, "prepare-compact.png");
            AssertActionFooterVisible();
            Width = 1120; Height = 780;
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

    private void AssertActionFooterVisible()
    {
        UpdateLayout();
        var root = (System.Windows.FrameworkElement)Content;
        var bottom = EnrollButton.TranslatePoint(new System.Windows.Point(0, EnrollButton.ActualHeight), root);
        Assert(EnrollButton.IsVisible && EnrollButton.ActualHeight > 0 && bottom.Y <= root.ActualHeight,
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
                while ((!File.Exists(result) || File.ReadAllText(result).Length == 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
                Assert(File.ReadAllText(result) == "7", "External host records the native result before waiting for Enter");
                process.Kill(); await process.WaitForExitAsync();
                Assert(CliRunner.ReadNativeOutcome(result, process.ExitCode) == 7, "Closing the host preserves the recorded native result");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally { File.Delete(result); }
        var terminal = new TerminalPanel("Login completion test", false, true);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = System.Windows.Visibility.Visible;
        try { Assert(await terminal.RunAsync(mockExecutable, ["--version"]).WaitAsync(TimeSpan.FromSeconds(10)) == 0, "Successful embedded login returns without a Done click"); }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = System.Windows.Visibility.Collapsed; }
    }

    private static void CaptureWindow(System.Windows.Window window, string file)
    {
        window.UpdateLayout();
        var content = (System.Windows.FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
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
