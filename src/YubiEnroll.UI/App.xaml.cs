using System.Windows;
namespace YubiEnroll.UI;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke-test"))
        {
            var window = new MainWindow(smokeTest: true);
            window.Show();
            window.Close();
            Shutdown(0);
            return;
        }
        new MainWindow().Show();
    }
}
