using System.Windows;
using System.Windows.Controls;
using YubiEnroll.Core;
namespace YubiEnroll.UI;

public partial class MainWindow
{
    private sealed record LocalListState(ResetKey Key, IReadOnlyList<LocalPasskey> Rows);
    private LocalListState? _localList;
    private int _localStep;

    private async void Local_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || LocalPage.IsVisible) return;
        ShowPage(LocalPage);
        if (!LocalPage.IsVisible) return;
        ResetLocalPage();
        if (!_inventoryLoaded || _inventoryDirty) await RefreshOverviewAsync();
    }

    private void ResetLocalPage()
    {
        if (LocalBack == null) return;
        _localStep = 0; _localList = null; LocalPin.Clear(); LocalRows.ItemsSource = null;
        LocalKeys.SelectedItem = null;
        LocalFeedback.Text = ""; RenderLocalPage();
    }

    private void RenderLocalPage()
    {
        if (LocalBack == null) return;
        var key = LocalKeys.SelectedItem as KeyStatus;
        RenderProgress(LocalProgress, ["Key", "Unlock", "Passkeys"], _localStep);
        LocalChooser.Visibility = Visible(_localStep == 0);
        LocalUnlock.Visibility = Visible(_localStep == 1); LocalList.Visibility = Visible(_localStep == 2);
        LocalTitle.Text = _localStep switch { 0 => "Choose your YubiKey", 1 => "Unlock the passkey list", _ => "Passkeys on this YubiKey" };
        LocalIntro.Text = _localStep == 0 ? "Choose a connected YubiKey to view its stored passkeys. No tenant sign-in is required." : key?.Key.Label ?? "";
        LocalView.Content = _localStep == 0 ? "Continue to unlock" : _localStep == 1 ? "Unlock" : "Done";
        LocalView.ContentTemplate = _localStep == 0 ? (DataTemplate)FindResource("ForwardContent") : null;
        using var secret = LocalPin.SecurePassword;
        LocalView.IsEnabled = CanReadLocal(key) && (_localStep != 1 || secret.Length > 0);
        SetBackAction(LocalBack, _localStep switch { 0 => null, 1 => "Choose key", _ => "Choose another YubiKey" });
    }
    private bool CanReadLocal(KeyStatus? key) => !_inventoryDirty && key != null && !key.ListingUnsupported && _knownKeys.Contains(key) &&
        key.Pin is FidoPinState.Set or FidoPinState.Unknown;
    private void LocalBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_localStep == 0) ShowPage(OverviewPage); else ResetLocalPage();
    }
    private void LocalPin_Changed(object sender, RoutedEventArgs e)
    {
        if (LocalBack != null && LocalFeedback != null && _localStep == 1) RenderLocalPage();
    }
    private void LocalPin_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && _localStep == 1 && LocalView.IsEnabled)
        { e.Handled = true; LocalView_Click(LocalView, new RoutedEventArgs()); }
    }
    private void LocalSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LocalBack == null) return;
        LocalFeedback.Text = LocalKeys.SelectedItem is KeyStatus k && k.Pin is not (FidoPinState.Set or FidoPinState.Unknown)
            ? k.PinLabel : "";
        RenderLocalPage();
    }
    private string? RequestLocalPin(ResetKey key, LocalPasskey? row)
    {
        var dialog = new LocalPinWindow(this, key, row);
        try { return dialog.ShowDialog() == true ? dialog.Pin.Password : null; }
        finally { dialog.Pin.Clear(); }
    }
    private static Task<IReadOnlyList<LocalPasskey>> RunLocalTool(ResetKey key, string pin, LocalPasskey? row)
    {
        var path = KeyResetService.FindExecutable();
        if (path.Length == 0) throw new InvalidOperationException("Install YubiKey Manager CLI and refresh the connected keys.");
        var runner = new CliRunner(path);
        return row == null ? LocalPasskeys.ListAsync(runner, key, pin) : LocalPasskeys.RemoveAsync(runner, key, pin, row);
    }

    private async void LocalView_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !LocalPage.IsVisible || !LocalView.IsEnabled) return;
        if (_localStep == 0) { _localStep = 1; LocalFeedback.Text = ""; RenderLocalPage(); LocalPin.Focus(); return; }
        if (_localStep == 2) { ShowPage(OverviewPage); return; }
        await LocalOperationAsync(null, (_, _) => { var pin = LocalPin.Password; LocalPin.Clear(); return pin; }, RunLocalTool);
    }
    private async void LocalRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LocalRows.SelectedItem is LocalPasskey row) await LocalOperationAsync(row, RequestLocalPin, RunLocalTool);
    }

    private async Task LocalOperationAsync(LocalPasskey? row, Func<ResetKey, LocalPasskey?, string?> confirm,
        Func<ResetKey,string,LocalPasskey?,Task<IReadOnlyList<LocalPasskey>>> run)
    {
        var list = _localList;
        if (_busy || _inventoryDirty || !LocalPage.IsVisible || LocalKeys.SelectedItem is not KeyStatus choice ||
            !CanReadLocal(choice) || (row == null ? _localStep != 1 : _localStep != 2) || (list != null && list.Key != choice.Key) ||
            (row != null && (list == null || !list.Rows.Any(r => ReferenceEquals(r, row))))) return;
        int generation = _deviceGeneration;
        bool Current() => LocalPage.IsVisible && !_inventoryDirty && generation == _deviceGeneration &&
            ReferenceEquals(list, _localList) && ReferenceEquals(choice, LocalKeys.SelectedItem);
        var pin = confirm(choice.Key, row);
        if (pin == null || !Current())
        {
            if (row != null) Record("On-key passkey removal cancelled · serial " + choice.Key.Serial + ".");
            return;
        }
        string? failure = null;
        LocalFeedback.Text = row == null ? "Reading passkeys…" : "Checking and removing the selected passkey…";
        // Never send the PIN, account information or raw native errors to the activity/diagnostic log.
        await RunWithResultAsync(async () =>
        {
            IReadOnlyList<LocalPasskey> updated;
            try { updated = await run(choice.Key, pin, row); }
            catch (Exception ex)
            {
                failure = ex is InvalidOperationException or ArgumentException ? ex.Message :
                    row == null ? "Could not read the passkeys. Check the connection and try again." :
                    "Removal could not be verified. Unlock the list to check; no deletion was retried.";
                return;
            }
            if (!Current()) return;
            _localStep = 2; _localList = new(choice.Key, updated);
            Record((row == null ? "On-key passkeys read" : "Passkey removed from YubiKey") + " · serial " + choice.Key.Serial + ".");
            LocalRows.ItemsSource = updated; RenderLocalPage();
            LocalFeedback.Text = (row == null ? "" : "Removed one passkey from this YubiKey. ") +
                (updated.Count == 0 ? "No discoverable passkeys are stored on this YubiKey." :
                $"{updated.Count} stored {(updated.Count == 1 ? "passkey" : "passkeys")}.");
        }, "Manage local YubiKey passkeys", false);
        if (failure != null || !Current() && _localList == null)
            Record((row == null ? "On-key passkey read did not complete" : "On-key removal outcome unverified") + " · serial " + choice.Key.Serial + ".", "Warning");
        if (failure != null && Current())
        {
            _localList = null; LocalRows.ItemsSource = null; _localStep = 1; LocalPin.Clear();
            LocalFeedback.Text = failure; RenderLocalPage(); LocalPin.Focus();
        }
        else if (row != null && _localList == null && LocalPage.IsVisible)
            LocalFeedback.Text = "The key connection changed. Unlock the list to check the removal outcome.";
    }
}
