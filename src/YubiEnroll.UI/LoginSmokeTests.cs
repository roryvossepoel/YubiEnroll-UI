using System.Windows;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private async Task TestLoginCancellationAsync(string executable)
    {
        var tenant = new TenantSetup("Example tenant", "11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "http://localhost/yubienroll-redirect", "", false);
        var launched = 0;
        var panel = new LoginPanel(tenant.Name, _ => launched++);
        TerminalOverlay.Content = panel; TerminalOverlay.Visibility = Visibility.Visible;
        var run = panel.RunAsync(executable, tenant, ["--login-probe"]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (launched == 0 && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert(launched == 1 && !run.IsCompleted, "Login automatically confirms only the matching tenant and opens its browser URL");
        panel.Launch();
        Assert(launched == 2 && !run.IsCompleted, "Reopening the browser reuses the same sign-in session");
        CaptureWindow(this, "login-browser.png");
        panel.Cancel();
        Assert(await run.WaitAsync(TimeSpan.FromSeconds(8)) == 125 && !panel.IsRunning, "Cancelling login terminates the waiting process and unblocks the app");
        var timed = new LoginPanel(tenant.Name, _ => { });
        TerminalOverlay.Content = timed;
        Assert(await timed.RunAsync(executable, tenant, ["--login-probe"], TimeSpan.FromMilliseconds(700)).WaitAsync(TimeSpan.FromSeconds(8)) == 125 && timed.FailureDetail.Contains("timed out"), "Unfinished browser sign-in has a bounded wait");
        var retry = new LoginPanel(tenant.Name, _ => { });
        TerminalOverlay.Content = retry;
        Assert(await retry.RunAsync(executable, tenant, ["--version"]).WaitAsync(TimeSpan.FromSeconds(8)) == 0, "A fresh sign-in session can finish after cancellation");
        TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed;
    }
}
