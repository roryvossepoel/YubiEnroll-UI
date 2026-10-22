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
                    Assert(editDialog.UsageText.Text.Contains("New tenant") && editDialog.ProfileName.IsReadOnly, "Editor shows linked tenants and protects profile name");
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
                    Assert(tenantEditor.TenantName.IsReadOnly && tenantEditor.ClientId.Text.Length > 0, "Tenant editor loads existing values and protects name");
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
                    Assert(settings.SettingsTabs.Items.Count == 4 && settings.TenantList.Items.Count == 1 && settings.ProfileList.Items.Count == 2,
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
            LogExpander.IsExpanded = false;
            CaptureWindow(this, "enrollment.png");
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
            Assert(StatusText.Text.StartsWith("Ready") && _versionText.Contains("Mock YubiEnroll"), "Normal startup with valid configuration and mock CLI");
            Assert(Diagnostics.Text.Length == 0 && WorkPanel.IsEnabled, "Successful reload clears diagnostics and busy state");
        }
        finally
        {
            SetThreadErrorMode(previousErrorMode, out _);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CaptureWindow(System.Windows.Window window, string file)
    {
        window.UpdateLayout();
        var content = (System.Windows.FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
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
