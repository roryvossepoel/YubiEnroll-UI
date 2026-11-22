using System.Windows;
using YubiEnroll.Core;
namespace YubiEnroll.UI;

public partial class MainWindow
{
    private async Task TestLocalPasskeyPageAsync()
    {
        var usb = new KeyStatus(new("12345678","YubiKey 5C NFC (5.4.3)"),FidoPinState.Set);
        var nfc = new KeyStatus(new("87654321","YubiKey 5C NFC (5.4.3)","Test OMNIKEY 5022"),FidoPinState.Set);
        LocalPasskey[] rows = [new("abcd","login.example","01","alex@example.test","Alex"),new("abce","github.example","02","alex","")];
        int calls = 0;
        Task<IReadOnlyList<LocalPasskey>> Read(ResetKey key, string pin, LocalPasskey? target)
        {
            Assert(key == nfc.Key && target == null && pin == "ci-only", "Local viewer reads only the selected NFC key");
            calls++;
            return Task.FromResult<IReadOnlyList<LocalPasskey>>(rows);
        }
        ApplyInventory([usb,nfc],0);
        Local_Click(this, new RoutedEventArgs()); UpdateLayout();
        Assert(LocalPage.IsVisible && !LocalView.IsEnabled && LocalKeys.SelectedItem == null && _localList == null,
            "Local page opens from Home without tenant sign-in and requires explicit key selection");
        LocalKeys.SelectedIndex = 1;
        LocalView_Click(LocalView, new RoutedEventArgs()); UpdateLayout();
        Assert(_localStep == 1 && LocalUnlock.IsVisible && !LocalView.IsEnabled && LocalProgress.ColumnDefinitions.Count == 5, "Choosing the key only opens the PIN step");
        await LocalOperationAsync(null, (_,_)=>"ci-only",Read);
        Assert(calls == 1 && LocalRows.Items.Count == 2 && LocalTitle.Text == "Passkeys on this YubiKey" && LocalIntro.Text.Contains("87654321"),
            "Unlocked list shows the verified serial and only on-key credentials");
        CaptureWindow(this, "local-passkeys.png");
        var removal = Dispatcher.InvokeAsync(() =>
        {
            var dialog = OwnedWindows.OfType<LocalPinWindow>().Single();
            try
            {
                Assert(dialog.CancelAction.IsDefault && !dialog.Proceed.IsEnabled && dialog.Origin.Text == "login.example" &&
                    dialog.Target.Text.Contains("87654321") && dialog.CredentialId.Text == "abcd",
                    "Local removal identifies the key and exact credential; Cancel is default and an empty PIN cannot confirm");
                CaptureWindow(dialog, "local-passkey-review.png");
            }
            finally { dialog.Close(); }
        });
        await LocalOperationAsync(rows[0], RequestLocalPin, Read); await removal.Task;
        Assert(calls == 1 && _localList != null, "Cancelling local removal invokes no key operation");
        await LocalOperationAsync(rows[0], (_,_)=> { InvalidateDeviceInventory(); return "ci-only"; }, Read);
        Assert(calls == 1 && _localList == null && LocalRows.Items.Count == 0 && !LocalView.IsEnabled,
            "Device change during confirmation clears the list and blocks deletion");
        ShowPage(OverviewPage);
        ApplyInventory([],0);
    }
}
