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
            Assert(StatusText.Text.StartsWith("Ready") && VersionText.Text.Contains("Mock YubiEnroll"), "Normal startup with valid configuration and mock CLI");
            Assert(Diagnostics.Text.Length == 0 && WorkPanel.IsEnabled, "Successful reload clears diagnostics and busy state");
        }
        finally
        {
            SetThreadErrorMode(previousErrorMode, out _);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
