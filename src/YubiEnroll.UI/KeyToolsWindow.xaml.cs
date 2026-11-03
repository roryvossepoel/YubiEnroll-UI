using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class KeyToolsWindow : Window
{
    private readonly KeyResetService? _service;
    private readonly bool _administrator;
    private readonly bool _autoRefresh;
    private bool _busy;
    private bool _ready;
    public bool IsOperationActive => _busy;
    public event Action<bool>? BusyChanged;
    public event Action? Finished;
    private Window DialogOwner => Owner ?? this;
    public bool ElevationRequested { get; private set; }

    public KeyToolsWindow(bool administrator, Func<string, string[], Task<int>>? interactive = null) : this(CreateService(interactive), administrator, true) { }

    // Injected only by the in-process smoke tests; no CLI/config switches can enable simulated resets.
    internal KeyToolsWindow(KeyResetService? service, bool administrator, bool autoRefresh)
    {
        _service = service;
        _administrator = administrator;
        _autoRefresh = autoRefresh;
        InitializeComponent();
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
        ElevateButton.Visibility = administrator ? Visibility.Collapsed : Visibility.Visible;
        PrerequisiteText.Text = "Requires YubiKey Manager CLI 5.9.2+ (5.x), installed separately, and administrator rights.";
    }

    private static KeyResetService? CreateService(Func<string, string[], Task<int>>? interactive)
    {
        var path = KeyResetService.FindExecutable();
        if (path.Length == 0) return null;
        var runner = new CliRunner(path);
        return new(args => runner.CaptureAsync(args), args => interactive == null ? runner.InteractiveAsync(args) : interactive(path, args));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_autoRefresh) await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    internal async Task RefreshAsync() => await RunAsync(async () =>
    {
        var selected = (Keys.SelectedItem as KeyStatus)?.Key;
        _ready = false;
        Keys.ItemsSource = null;
        StatusText.Text = "Reading connected keys and FIDO PIN status…";
        if (_service == null)
        {
            StatusText.Text = "YubiKey Manager CLI was not found. Install it in the standard Yubico folder under Program Files, then reopen Key Management. Enrollment does not require this extra tool.";
            return;
        }
        if (!_administrator)
        {
            StatusText.Text = "Restart as administrator to access FIDO keys. After restarting, open Key Management and select the key again.";
            return;
        }
        var version = await _service.CheckVersionAsync();
        PrerequisiteText.Text = $"YubiKey Manager CLI {version} · Administrator";
        var inventory = await LoadKeysAsync(selected);
        StatusText.Text = inventory.Keys.Count == 0 ? "No supported key found. Connect a USB YubiKey and Refresh." : "Select the key by its serial number. No tenant login is needed.";
        if (inventory.Unavailable > 0) StatusText.Text += " Some devices could not be selected (no readable serial, FIDO disabled, access denied or unrecognized output).";
    });

    // Rebuild from fresh device data; only restore the exact previous identity, never another key.
    private async Task<KeyInventory> LoadKeysAsync(ResetKey? selected)
    {
        _ready = false;
        Keys.ItemsSource = null;
        var inventory = await _service!.DiscoverAsync();
        var entries = new List<KeyStatus>();
        foreach (var key in inventory.Keys)
        {
            FidoPinState pin;
            try { pin = await _service.ReadPinStateAsync(key); }
            catch { pin = FidoPinState.Unknown; }
            entries.Add(new(key, pin));
        }
        Keys.ItemsSource = entries;
        Keys.SelectedItem = entries.FirstOrDefault(item => item.Key == selected);
        _ready = true;
        return inventory;
    }

    private void Key_Changed(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async void Reset_Click(object sender, RoutedEventArgs e) => await ResetSelectedAsync(key =>
        MessageBox.Show(DialogOwner, $"{key.Label}\n\nPermanently erase ALL FIDO2/U2F credentials and the FIDO PIN on this key?\n\n" +
            "This affects every account registered with this key. Entra and other service registrations remain.\n\n" +
            "Continue to the native confirmation and physical touch prompts?", "Confirm FIDO reset",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

    internal async Task ResetSelectedAsync(Func<ResetKey, bool> confirm)
    {
        if (_busy || !_administrator || !_ready || _service == null || Keys.SelectedItem is not KeyStatus selectedItem) return;
        var selected = selectedItem.Key;
        await RunAsync(async () =>
        {
            _ready = false;
            StatusText.Text = "Checking the selected key…";
            int? exit;
            try
            {
                exit = await _service.ResetAsync(selected, key =>
                {
                    if (!confirm(key)) return false;
                    StatusText.Text = "Follow the console prompts to confirm, reinsert and touch this key. When it finishes, choose Done in the app or press Enter in the external console.";
                    return true;
                });
            }
            catch
            {
                Keys.ItemsSource = null;
                throw;
            }
            var outcome = exit switch
            {
                null => "Reset cancelled. No reset command was started.",
                0 => "YubiKey Manager reported a successful FIDO reset. Entra and other service registrations remain.",
                _ => "Reset failed, was cancelled or interrupted. Its outcome is not confirmed. Inspect the key before trying again; the app will not retry automatically."
            };
            StatusText.Text = outcome + " Reading keys and PIN status…";
            try
            {
                await LoadKeysAsync(selected);
                var refreshed = Keys.SelectedItem as KeyStatus;
                if (refreshed == null)
                    outcome += " The selected key is no longer available. Reconnect it and Refresh.";
                else if (exit == 0)
                    outcome += refreshed.Pin switch
                    {
                        FidoPinState.NotSet => " Read-back: FIDO PIN is not set.",
                        FidoPinState.Set or FidoPinState.Blocked => " Read-back: a FIDO PIN is still set. Inspect the reset result.",
                        _ => " The FIDO PIN status could not be verified. Refresh to check again."
                    };
            }
            catch
            {
                _ready = false;
                Keys.ItemsSource = null;
                outcome += " Could not reload the keys. Refresh to check again; no reset was repeated.";
            }
            StatusText.Text = outcome;
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyChanged?.Invoke(true);
        UpdateControls();
        try { await action(); }
        catch (Exception ex)
        {
            _ready = false;
            Keys.ItemsSource = null;
            StatusText.Text = "Operation stopped. " + ex.Message + " No automatic retry was performed.";
        }
        finally { _busy = false; UpdateControls(); BusyChanged?.Invoke(false); }
    }

    private void UpdateControls()
    {
        // SelectionChanged can run during XAML construction.
        if (ResetButton == null) return;
        ResetButton.IsEnabled = !_busy && _ready && _administrator && Keys.SelectedItem is KeyStatus;
        Keys.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;
        CloseButton.IsEnabled = !_busy;
        DownloadButton.IsEnabled = !_busy;
        ElevateButton.IsEnabled = !_busy;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developers.yubico.com/yubikey-manager/") { UseShellExecute = true });
        }
        catch { StatusText.Text = "Open https://developers.yubico.com/yubikey-manager/ in your browser to get the official Windows installer."; }
    }

    private void Finish() { if (IsVisible) Close(); else Finished?.Invoke(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Finish();
    private void Elevate_Click(object sender, RoutedEventArgs e) { ElevationRequested = true; Finish(); }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
}
