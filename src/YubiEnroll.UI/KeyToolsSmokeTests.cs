using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private async Task TestKeyToolsAsync()
    {
        const string row = "YubiKey 5 NFC (5.7.1) [OTP+FIDO+CCID] Serial: 12345678";
        var inventory = row;
        var launched = 0;
        var pending = new TaskCompletionSource<int>();
        var service = new KeyResetService(args => Task.FromResult(args[0] switch
        {
            "--version" => new CliResult(0, "YubiKey Manager (ykman) version: 5.9.2", ""),
            "list" => new CliResult(0, inventory, ""),
            "--device" => new CliResult(0, "PIN: Set", ""),
            _ => throw new InvalidOperationException("Unexpected mock command")
        }), _ => { launched++; return pending.Task; });

        async Task ShowCase(KeyToolsWindow dialog, Func<KeyToolsWindow, Task> check)
        {
            dialog.Owner = this;
            var operation = dialog.Dispatcher.InvokeAsync(async () =>
            {
                try { await check(dialog); }
                finally { dialog.Close(); }
            });
            dialog.ShowDialog();
            await operation.Task.Unwrap();
        }

        await ShowCase(new KeyToolsWindow(null, true, false), async dialog =>
        {
            await dialog.RefreshAsync();
            Assert(!dialog.ResetButton.IsEnabled && dialog.StatusText.Text.Contains("not found"), "Missing optional ykman disables reset with installation guidance");
        });
        await ShowCase(new KeyToolsWindow(service, false, false), async dialog =>
        {
            await dialog.RefreshAsync();
            await dialog.ResetSelectedAsync(_ => throw new Exception("Must not ask for confirmation without admin"));
            Assert(!dialog.ResetButton.IsEnabled && dialog.ElevateButton.IsVisible && launched == 0, "Standard user cannot reset and can request elevation");
        });
        await ShowCase(new KeyToolsWindow(service, true, false), async dialog =>
        {
            await dialog.RefreshAsync();
            Assert(dialog.Keys.Items.Count == 1 && !dialog.ResetButton.IsEnabled, "Device discovery requires explicit key selection");
            dialog.Keys.SelectedIndex = 0;
            Assert(dialog.ResetButton.IsEnabled, "Explicit device selection enables reset without tenant login");
            CaptureWindow(dialog, "key-tools.png");
            await dialog.ResetSelectedAsync(_ => false);
            Assert(launched == 0 && !dialog.ResetButton.IsEnabled && dialog.StatusText.Text.Contains("cancelled"), "Cancelled reset makes no change and clears selection");
            await dialog.RefreshAsync();
            dialog.Keys.SelectedIndex = 0;
            await dialog.ResetSelectedAsync(_ => { inventory = ""; return true; });
            Assert(launched == 0 && !dialog.ResetButton.IsEnabled && dialog.StatusText.Text.Contains("No reset was started"), "Key removed during confirmation blocks reset in UI");
            inventory = row;
            await dialog.RefreshAsync();
            dialog.Keys.SelectedIndex = 0;
            var reset = dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 1 && !dialog.ResetButton.IsEnabled && !dialog.Keys.IsEnabled && !dialog.CloseButton.IsEnabled,
                "Active reset locks selection, duplicate actions and closing");
            dialog.Close();
            Assert(dialog.IsVisible, "Window close is refused during active reset");
            await dialog.ResetSelectedAsync(_ => throw new Exception("Duplicate action must not run"));
            pending.SetResult(0);
            await reset;
            Assert(launched == 1 && dialog.StatusText.Text.Contains("reported a successful") && !dialog.ResetButton.IsEnabled && dialog.CloseButton.IsEnabled,
                "CLI-reported reset success requires a fresh selection for further action");
            pending = new TaskCompletionSource<int>();
            pending.SetResult(1);
            await dialog.RefreshAsync();
            dialog.Keys.SelectedIndex = 0;
            await dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 2 && dialog.StatusText.Text.Contains("not confirmed"), "Interrupted reset has unknown outcome and no automatic retry");
        });
    }
}
