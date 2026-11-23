using System.IO;
using System.Windows;
namespace YubiEnroll.UI;
public partial class App : Application
{
    internal static void StopForPolicyError(Exception ex)
    {
        MessageBox.Show("Passkey Desk cannot apply the device policy. " + ex.Message +
            "\n\nCheck the policy and the CLI configuration, then restart the app.",
            "Passkey Desk · Device policy", MessageBoxButton.OK, MessageBoxImage.Warning);
        Current.Shutdown(1);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        TaskbarIdentity.Initialize();
        base.OnStartup(e);
        YubiEnroll.Core.DeskPolicy? policy;
        try
        {
            using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = machine.OpenSubKey(YubiEnroll.Core.DeskPolicy.RegistryPath, writable: false);
            policy = YubiEnroll.Core.DeskPolicy.ReadRegistry(key,
                YubiEnroll.Core.RegistrationJournal.LogDirectory("ActivityLog"), YubiEnroll.Core.RegistrationJournal.LogDirectory("RegistrationLog"));
            if (policy != null && (e.Args.Contains("--smoke-test") || e.Args.Contains("--terminal-check")))
                throw new InvalidOperationException("Run diagnostics on a device outside policy mode.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StopForPolicyError(ex);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--terminal-check")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = 0;
            var window = new MainWindow(smokeTest: true);
            window.Show();
            try { await window.RunNativeTerminalCheckAsync(e.Args[1]); }
            catch (Exception ex) { code = 1; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "native-terminal-check.txt"), ex.ToString()); }
            window.Close(); Shutdown(code); return;
        }
        if (e.Args.Contains("--smoke-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = 0;
            try
            {
                YubiEnroll.UI.MainWindow.TraceStartup("Constructing window");
                var window = new MainWindow(smokeTest: true);
                window.Show();
                YubiEnroll.UI.MainWindow.TraceStartup("Window shown");
                if (e.Args.Length != 2) throw new ArgumentException("Supply the mock CLI executable for startup testing.");
                await window.RunStartupSmokeTestsAsync(e.Args[1]);
                window.Close();
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup-smoke-test.txt"),
                    "PASS: first-run setup, tenant form validation/save/cancel, malformed TOML, invalid executable and mock CLI startup.");
            }
            catch (Exception ex)
            {
                code = 1;
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup-smoke-test.txt"), ex.ToString());
            }
            Shutdown(code);
            return;
        }
        MainWindow = new MainWindow(policy: policy);
        MainWindow.Show();
    }
}
