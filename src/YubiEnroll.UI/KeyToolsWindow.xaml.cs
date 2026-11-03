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
    public bool ElevationRequested { get; private set; }

    public KeyToolsWindow(bool administrator) : this(CreateService(), administrator, true) { }

    // Injected only by the in-process smoke tests; no CLI/config switches can enable simulated resets.
    internal KeyToolsWindow(KeyResetService? service, bool administrator, bool autoRefresh)
    {
        _service = service;
        _administrator = administrator;
        _autoRefresh = autoRefresh;
        InitializeComponent();
        ElevateButton.Visibility = administrator ? Visibility.Collapsed : Visibility.Visible;
        PrerequisiteText.Text = "Requires YubiKey Manager CLI 5.9.2+ (5.x), installed separately, and administrator rights.";
    }

    private static KeyResetService? CreateService()
    {
        var path = KeyResetService.FindExecutable();
        if (path.Length == 0) return null;
        var runner = new CliRunner(path);
        return new(args => runner.CaptureAsync(args), args => runner.InteractiveAsync(args));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_autoRefresh) await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    internal async Task RefreshAsync() => await RunAsync(async () =>
    {
        _ready = false;
        Keys.ItemsSource = null;
        if (_service == null)
        {
            StatusText.Text = "YubiKey Manager CLI was not found. Install it in the standard Yubico folder under Program Files, then reopen Key tools. Enrollment does not require this extra tool.";
            return;
        }
        if (!_administrator)
        {
            StatusText.Text = "Restart as administrator to access FIDO keys. After restarting, open Key tools and select the key again.";
            return;
        }
        var version = await _service.CheckVersionAsync();
        PrerequisiteText.Text = $"YubiKey Manager CLI {version} · Administrator";
        var inventory = await _service.DiscoverAsync();
        Keys.ItemsSource = inventory.Keys;
        _ready = true;
        StatusText.Text = inventory.Keys.Count == 0 ? "No supported key found. Connect a USB YubiKey and Refresh." : "Select the key by its serial number. No tenant login is needed.";
        if (inventory.Unavailable > 0) StatusText.Text += " Some devices could not be selected (no readable serial, FIDO disabled, access denied or unrecognized output).";
    });

    private void Key_Changed(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async void Reset_Click(object sender, RoutedEventArgs e) => await ResetSelectedAsync(key =>
        MessageBox.Show(this, $"{key.Label}\n\nPermanently erase ALL FIDO2/U2F credentials and the FIDO PIN on this key?\n\n" +
            "This affects every account registered with this key. Entra and other service registrations remain.\n\n" +
            "Continue to the native confirmation and physical touch prompts?", "Confirm FIDO reset",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

    internal async Task ResetSelectedAsync(Func<ResetKey, bool> confirm)
    {
        if (_busy || !_administrator || !_ready || _service == null || Keys.SelectedItem is not ResetKey selected) return;
        await RunAsync(async () =>
        {
            _ready = false;
            StatusText.Text = "Checking the selected key…";
            try
            {
                var exit = await _service.ResetAsync(selected, key =>
                {
                    if (!confirm(key)) return false;
                    StatusText.Text = "Follow the console prompts to confirm, reinsert and touch this key. Press Enter after the command finishes.";
                    return true;
                });
                StatusText.Text = exit switch
                {
                    null => "Reset cancelled. No reset command was started. Refresh to select a key again.",
                    0 => "YubiKey Manager reported a successful FIDO reset. Entra and other service registrations remain. Refresh before any further action.",
                    _ => "Reset failed, was cancelled or interrupted. Its outcome is not confirmed. Inspect the key before trying again; the app will not retry automatically."
                };
            }
            finally { Keys.ItemsSource = null; }
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception ex)
        {
            _ready = false;
            Keys.ItemsSource = null;
            StatusText.Text = "Operation stopped. " + ex.Message + " No automatic retry was performed.";
        }
        finally { _busy = false; UpdateControls(); }
    }

    private void UpdateControls()
    {
        // SelectionChanged can run during XAML construction.
        if (ResetButton == null) return;
        ResetButton.IsEnabled = !_busy && _ready && _administrator && Keys.SelectedItem is ResetKey;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Elevate_Click(object sender, RoutedEventArgs e) { ElevationRequested = true; Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
}
