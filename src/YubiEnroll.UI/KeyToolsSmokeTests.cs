using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private static void AssertResetLayout(KeyToolsWindow dialog)
    {
        // The work-area clamp intentionally permits scrolling on small screens.
        // Verify access to all warning content, while keeping actions outside that scroll area.
        dialog.BodyScroll.ScrollToBottom(); dialog.UpdateLayout();
        Assert(dialog.BodyScroll.ViewportHeight > 100 && Math.Abs(dialog.BodyScroll.VerticalOffset - dialog.BodyScroll.ScrollableHeight) < 1,
            "Reset warning remains reachable by scrolling at the available screen height");
        var root = (System.Windows.FrameworkElement)dialog.Content;
        var bottom = dialog.CloseButton.TranslatePoint(new System.Windows.Point(0, dialog.CloseButton.ActualHeight), root);
        Assert(bottom.Y <= root.ActualHeight && dialog.CloseButton.ActualHeight > 0, "Reset action footer stays inside the visible window");
        dialog.BodyScroll.ScrollToTop(); dialog.UpdateLayout();
    }

    private async Task TestKeyToolsAsync()
    {
        const string row = "YubiKey 5 NFC (5.7.1) [OTP+FIDO+CCID] Serial: 12345678";
        var inventory = row;
        var launched = 0;
        var pinOutput = "PIN: 8 attempt(s) remaining";
        var infoFails = false;
        var listFails = false;
        Action? onLaunch = null;
        var pending = new TaskCompletionSource<int>();
        var service = new KeyResetService(args => Task.FromResult(args[0] switch
        {
            "--version" => new CliResult(0, "YubiKey Manager (ykman) version: 5.9.2", ""),
            "list" => new CliResult(listFails ? 1 : 0, inventory, ""),
            "--device" => new CliResult(infoFails ? 1 : 0, pinOutput, ""),
            _ => throw new InvalidOperationException("Unexpected mock command")
        }), _ => { launched++; onLaunch?.Invoke(); return pending.Task; });

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
            Assert(((KeyStatus)dialog.Keys.Items[0]).Pin == FidoPinState.Set, "Initial discovery reads PIN status without changing the key");
            dialog.Keys.SelectedIndex = 0;
            Assert(dialog.ResetButton.IsEnabled, "Explicit device selection enables reset without tenant login");
            CaptureWindow(dialog, "key-tools.png");
            AssertResetLayout(dialog);
            await dialog.ResetSelectedAsync(_ => false);
            Assert(launched == 0 && dialog.ResetButton.IsEnabled && dialog.StatusText.Text.Contains("cancelled"), "Cancelled reset refreshes and preserves selection without changing the key");
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
            pinOutput = "PIN: Not set";
            pending.SetResult(0);
            await reset;
            Assert(launched == 1 && dialog.StatusText.Text.Contains("Read-back: FIDO PIN is not set") && dialog.ResetButton.IsEnabled && dialog.CloseButton.IsEnabled &&
                dialog.Keys.SelectedItem is KeyStatus { Pin: FidoPinState.NotSet, Key.Serial: "12345678" },
                "Successful reset reloads PIN status and restores the same key without repeating reset");
            CaptureWindow(dialog, "key-tools-after-reset.png");
            AssertResetLayout(dialog);
            pending = new TaskCompletionSource<int>();
            pending.SetResult(1);
            await dialog.RefreshAsync();
            dialog.Keys.SelectedIndex = 0;
            await dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 2 && dialog.StatusText.Text.Contains("not confirmed") && !dialog.StatusText.Text.Contains("Read-back:"),
                "An unset PIN does not turn a failed reset into verified success");

            pending = new TaskCompletionSource<int>();
            pending.SetResult(0);
            onLaunch = () => infoFails = true;
            await dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 3 && dialog.Keys.SelectedItem is KeyStatus { Pin: FidoPinState.Unknown } &&
                dialog.StatusText.Text.Contains("could not be verified"), "Read-back failure never reports PIN removed");
            infoFails = false;
            pinOutput = "PIN: 8 attempt(s) remaining";
            onLaunch = null;
            await dialog.RefreshAsync();
            await dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 4 && dialog.StatusText.Text.Contains("PIN is still set"), "PIN still present after exit zero requires inspection");

            onLaunch = () => inventory = row.Replace("12345678", "87654321");
            await dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 5 && dialog.Keys.Items.Count == 1 && dialog.Keys.SelectedItem == null && !dialog.ResetButton.IsEnabled &&
                dialog.StatusText.Text.Contains("no longer available"), "A different connected key is listed but never substituted for the reset target");

            inventory = row;
            onLaunch = null;
            await dialog.RefreshAsync();
            dialog.Keys.SelectedIndex = 0;
            onLaunch = () => listFails = true;
            await dialog.ResetSelectedAsync(_ => true);
            Assert(launched == 6 && !dialog.ResetButton.IsEnabled && dialog.StatusText.Text.Contains("reported a successful") &&
                dialog.StatusText.Text.Contains("Could not reload"), "Inventory reload failure preserves CLI result and offers refresh without retrying reset");
            listFails = false;
            onLaunch = null;
            await dialog.RefreshAsync();
            Assert(dialog.Keys.Items.Count == 1, "Refresh recovers device discovery after a read failure");
        });
    }
}
