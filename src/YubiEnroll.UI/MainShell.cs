using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private void ResizePreparation()
    {
        var compact = ActualWidth < 880;
        SidebarColumn.Width = new GridLength(compact ? 120 : 210);
        BrandTitle.FontSize = compact ? 13 : 22;
        BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarKeyCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OverviewButton.Content = compact ? "Home" : "Overview";
        PrepareButton.Content = compact ? "Prepare" : "Prepare Key";
        KeyToolsButton.Content = compact ? "Keys" : "Key Management";

        var narrow = ActualWidth < 1000;
        PreparationColumns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(PreparationCard, narrow ? 0 : 1);
        Grid.SetRow(PreparationCard, narrow ? 1 : 0);
        UserCard.Margin = narrow ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 8, 0);
        PreparationCard.Margin = narrow ? new Thickness(0) : new Thickness(8, 0, 0, 0);
    }
    private Window? _hostedWindow;
    internal bool EmbeddedTerminal { get; set; } = true; // External console remains an explicit fallback.
    private void LeaveHostedPage()
    {
        if (_hostedWindow is SettingsWindow settings)
        {
            EmbeddedTerminal = settings.EmbeddedTerminal;
            if (settings.Changed) LoadConfiguration(_configPath);
        }
        HostedPage.Content = null;
        if (_hostedWindow != null) { _hostedWindow.Owner = null; _hostedWindow.Close(); }
        _hostedWindow = null;
    }
    private void ShowPage(FrameworkElement page)
    {
        if (_busy || (_hostedWindow is KeyToolsWindow keyTools && keyTools.IsOperationActive)) return;
        if (page != HostedPage) LeaveHostedPage();
        foreach (var item in new FrameworkElement[] { OverviewPage, PreparePage, HostedPage, DetailsPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.FontWeight = page == OverviewPage ? FontWeights.Bold : FontWeights.Normal;
        PrepareButton.FontWeight = page == PreparePage ? FontWeights.Bold : FontWeights.Normal;
        foreach (var button in new[] { OverviewButton, PrepareButton, KeyToolsButton, SettingsButton })
        {
            var active = button == OverviewButton && page == OverviewPage || button == PrepareButton && page == PreparePage ||
                button == KeyToolsButton && page == HostedPage && _hostedWindow is KeyToolsWindow ||
                button == SettingsButton && (page == DetailsPage || page == HostedPage && _hostedWindow is SettingsWindow);
            button.Background = active ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 237, 238)) : System.Windows.Media.Brushes.Transparent;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
    private async void Overview_Click(object sender, RoutedEventArgs e) { ShowPage(OverviewPage); await RefreshOverviewAsync(); }
    private void Prepare_Click(object sender, RoutedEventArgs e) => ShowPage(PreparePage);
    private void Details_Click(object sender, RoutedEventArgs e) => ShowPage(DetailsPage);
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => OpenSettings(2);
    private void About_Click(object sender, RoutedEventArgs e) => OpenSettings(4);
    private void Host(Window window)
    {
        LeaveHostedPage();
        _hostedWindow = window;
        window.Owner = this;
        var content = window.Content; window.Content = null;
        HostedPage.Content = content;
        ShowPage(HostedPage);
    }
    private void OpenSettings(int tab = 0)
    {
        if (_busy) return;
        LeaveHostedPage();
        var settings = new SettingsWindow(_configPath, _exePath, _versionText) { EmbeddedTerminal = EmbeddedTerminal };
        settings.SettingsTabs.SelectedIndex = tab;
        settings.ActivityRequested += () => ShowPage(DetailsPage);
        settings.Finished += async () =>
        {
            var elevate = settings.ElevationRequested;
            var recheck = settings.RecheckRequested;
            ShowPage(OverviewPage);
            if (recheck) { await InitializeAsync(); await RefreshOverviewAsync(); }
            if (elevate) Elevate_Click(this, new RoutedEventArgs());
        };
        Host(settings);
    }
    private async void RefreshKeys_Click(object sender, RoutedEventArgs e) => await RefreshOverviewAsync();
    private async Task RefreshOverviewAsync()
    {
        if (_busy) return;
        var previousSerial = _selectionSerial;
        await RunAsync(async () =>
        {
            _readingKeys = true;
            OverviewKeys.ItemsSource = null;
            _readingKeys = false;
            KeyInfo.Text = "Reading connected keys…";
            ReadinessText.Text = !IsAdministrator() ? "Restart as administrator to inspect FIDO PIN status and reset keys." : "";
            if (!_cliReady) ReadinessText.Text += "\nInstall YubiEnroll to prepare keys.";
            ReadinessCard.Visibility = ReadinessText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OverviewElevate.Visibility = IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0) { SetSelectedKey(null); KeyPinStatus.Text = ""; OverviewKeys.Visibility = Visibility.Collapsed; KeyInfo.Text = "Install YubiKey Manager CLI to inspect and manage connected keys. Tenant and profile settings remain available."; return; }
            var runner = new CliRunner(path);
            var service = new KeyResetService(a => runner.CaptureAsync(a), _ => throw new InvalidOperationException());
            try
            {
                var version = await service.CheckVersionAsync();

                var inventory = await service.DiscoverAsync();
                var entries = new List<KeyStatus>();
                foreach (var key in inventory.Keys)
                {
                    var pin = FidoPinState.Unknown;
                    if (IsAdministrator()) try { pin = await service.ReadPinStateAsync(key); } catch { }
                    entries.Add(new(key, pin));
                }
                OverviewKeys.ItemsSource = entries;
                OverviewKeys.SelectedItem = entries.FirstOrDefault(x => x.Key.Serial == previousSerial);
                // Restore by serial, and never substitute another device for a previously selected key.
                if (previousSerial == null && entries.Count == 1) OverviewKeys.SelectedIndex = 0;
                OverviewKey_Changed(this, null!);
                OverviewKeys.Visibility = entries.Count > 1 || (entries.Count == 1 && OverviewKeys.SelectedItem == null) ? Visibility.Visible : Visibility.Collapsed;
                if (OverviewKeys.SelectedItem == null) KeyInfo.Text = entries.Count == 0 ? "No supported key detected. Connect a USB security key to get started." : "Choose a key to inspect its status.";
                if (inventory.Unavailable > 0) KeyInfo.Text += "\nSome devices could not be inspected. Check device access and FIDO support.";
            }
            catch { SetSelectedKey(null); KeyPinStatus.Text = ""; KeyInfo.Text = "Unable to inspect keys. Check prerequisites, close other key tools and refresh."; Record("Key inspection unavailable.", "Warning"); }
        }, "Inspect connected keys");
    }
    private KeyStatus? _selectedKey;
    private string? _selectionSerial;
    private ResetKey? _lastKnownKey;
    private bool _readingKeys;
    private void SetSelectedKey(KeyStatus? key)
    {
        var changed = _selectedKey?.Key.Serial != key?.Key.Serial || _selectedKey?.Pin != key?.Pin;
        _selectedKey = key;
        if (key != null) { _selectionSerial = key.Key.Serial; _lastKnownKey = key.Key; }
        SelectedKeyCard.Text = key == null ? "No key selected" : "Connected\n" + key.Key.Model + "\nSerial " + key.Key.Serial;
        if (changed) Record(key == null ? "Selected key is unavailable." : $"Key selected · serial {key.Key.Serial} · {key.PinLabel}");
    }
    private void OverviewKey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedKeyCard == null || KeyPinStatus == null || _readingKeys) return;
        var key = OverviewKeys.SelectedItem as KeyStatus;
        SetSelectedKey(key);
        KeyPinStatus.Text = key?.PinLabel ?? "";
        KeyPinStatus.Foreground = key?.Pin == FidoPinState.Blocked ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkSlateGray;
        if (key != null) KeyInfo.Text = key.Key.Model + "\nSerial number: " + key.Key.Serial;
    }
    private System.Windows.Threading.DispatcherTimer? _deviceRefresh;
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_smokeTest) return;
        Closed += (_, _) => _deviceRefresh?.Stop();
        _deviceRefresh = new() { Interval = TimeSpan.FromSeconds(1) };
        _deviceRefresh.Tick += async (_, _) =>
        {
            if (_busy || _hostedWindow is KeyToolsWindow { IsOperationActive: true }) return;
            _deviceRefresh.Stop();
            if (_hostedWindow is KeyToolsWindow tools) await tools.RefreshAsync();
            else await RefreshOverviewAsync();
        };
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(DeviceMessage);
    }
    private IntPtr DeviceMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DEVICECHANGE: debounce bursts, do not inspect while a native operation owns the key.
        if (message == 0x0219) { _deviceRefresh?.Stop(); _deviceRefresh?.Start(); }
        return IntPtr.Zero;
    }
    private string _lastInteractiveFailure = "";
    private async Task<int> RunInteractiveAsync(string executable, string[] args)
    {
        _lastInteractiveFailure = "";
        if (!EmbeddedTerminal) return await new CliRunner(executable).InteractiveAsync(args);
        var enrollment = Path.GetFileName(executable).Equals("yubienroll.exe", StringComparison.OrdinalIgnoreCase) && args.Take(2).SequenceEqual(new[] { "credentials", "add" });
        var login = args.SequenceEqual(new[] { "login" });
        var terminal = new TerminalPanel(enrollment ? "Prepare Key — interactive operation" : login ? "Connect to tenant" : "Key Management — interactive operation", enrollment, login);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = Visibility.Visible;
        try { var code = await terminal.RunAsync(executable, args); _lastInteractiveFailure = terminal.FailureDetail; return code; }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
    }
}
