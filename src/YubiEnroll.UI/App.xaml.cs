using System.IO;
using System.Windows;
namespace YubiEnroll.UI;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
                    "PASS: missing folder, missing file, malformed TOML, invalid executable, valid config and mock CLI startup.");
            }
            catch (Exception ex)
            {
                code = 1;
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup-smoke-test.txt"), ex.ToString());
            }
            Shutdown(code);
            return;
        }
        new MainWindow().Show();
    }
}
