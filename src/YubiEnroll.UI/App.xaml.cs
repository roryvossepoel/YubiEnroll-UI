using System.IO;
using System.Windows;
namespace YubiEnroll.UI;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        TaskbarIdentity.Initialize();
        base.OnStartup(e);
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
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
