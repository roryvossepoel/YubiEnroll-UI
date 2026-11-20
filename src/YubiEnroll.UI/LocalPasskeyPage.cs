using System.Windows;
using System.Windows.Controls;
using YubiEnroll.Core;
namespace YubiEnroll.UI;

public partial class MainWindow
{
    private sealed record LocalListState(ResetKey Key, IReadOnlyList<LocalPasskey> Rows);
    private LocalListState? _localList;

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
        _localList = null; LocalRows.ItemsSource = null;
        LocalKeys.SelectedItem = null;
        LocalFeedback.Text = ""; RenderLocalPage();
    }

    private void RenderLocalPage()
    {
        if (LocalBack == null) return;
        var key = LocalKeys.SelectedItem as KeyStatus;
        LocalChooser.Visibility = Visible(_localList == null); LocalList.Visibility = Visible(_localList != null);
        LocalTitle.Text = _localList == null ? "Passkeys on a YubiKey" : "Passkeys on this YubiKey";
        LocalIntro.Text = _localList?.Key.Label ?? "Choose a connected YubiKey to view its stored passkeys with its FIDO PIN. No tenant sign-in is required.";
        LocalView.IsEnabled = !_inventoryDirty && key != null && _knownKeys.Contains(key) &&
            key.Pin is FidoPinState.Set or FidoPinState.Unknown;
        SetBackAction(LocalBack, _localList == null ? null : "Choose another YubiKey");
    }

    private void LocalBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_localList == null) ShowPage(OverviewPage); else ResetLocalPage();
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

    private async void LocalView_Click(object sender, RoutedEventArgs e) => await LocalOperationAsync(null, RequestLocalPin, RunLocalTool);
    private async void LocalRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LocalPasskey row }) await LocalOperationAsync(row, RequestLocalPin, RunLocalTool);
    }

    private async Task LocalOperationAsync(LocalPasskey? row, Func<ResetKey, LocalPasskey?, string?> confirm,
        Func<ResetKey,string,LocalPasskey?,Task<IReadOnlyList<LocalPasskey>>> run)
    {
        var list = _localList;
        if (_busy || _inventoryDirty || !LocalPage.IsVisible || LocalKeys.SelectedItem is not KeyStatus choice ||
            !LocalView.IsEnabled || !_knownKeys.Contains(choice) || (list != null && list.Key != choice.Key) ||
            (row != null && (list == null || !list.Rows.Any(r => ReferenceEquals(r, row))))) return;
        int generation = _deviceGeneration;
        bool Current() => LocalPage.IsVisible && !_inventoryDirty && generation == _deviceGeneration &&
            ReferenceEquals(list, _localList) && ReferenceEquals(choice, LocalKeys.SelectedItem);
        var pin = confirm(choice.Key, row);
        if (pin == null || !Current()) return;
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
            _localList = new(choice.Key, updated);
            LocalRows.ItemsSource = updated; RenderLocalPage();
            LocalFeedback.Text = (row == null ? "" : "Removed one passkey from this YubiKey. ") +
                (updated.Count == 0 ? "No discoverable passkeys are stored on this YubiKey." :
                $"{updated.Count} stored {(updated.Count == 1 ? "passkey" : "passkeys")}.");
        }, "Manage local YubiKey passkeys", false);
        if (failure != null && Current()) { ResetLocalPage(); LocalFeedback.Text = failure; }
        else if (row != null && _localList == null && LocalPage.IsVisible)
            LocalFeedback.Text = "The key connection changed. Unlock the list to check the removal outcome.";
    }
}
