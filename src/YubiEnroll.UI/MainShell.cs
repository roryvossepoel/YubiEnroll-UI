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
        Users.MinHeight = 80;
        Users.MaxHeight = ActualHeight < 800 ? 130 : 180;
        var compact = ActualWidth < 880;
        SidebarColumn.Width = new GridLength(compact ? 120 : 210);
        BrandTitle.FontSize = compact ? 13 : 22;
        BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarKeyCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OverviewButton.Content = compact ? "Home" : "Overview";
        PrepareButton.Content = compact ? "Enroll" : "Enroll key";
    }
    private Window? _hostedWindow;
    internal bool EmbeddedTerminal { get; set; } = true; // External console remains an explicit fallback.
    private void LeaveHostedPage()
    {
        if (_hostedWindow is SettingsWindow settings)
        {
            settings.ActivityHost.Content = null;
            EmbeddedTerminal = settings.EmbeddedTerminal;
            if (settings.Changed) LoadConfiguration(_configPath);
        }
        HostedPage.Content = null;
        if (_hostedWindow != null) { _hostedWindow.Owner = null; _hostedWindow.Close(); }
        _hostedWindow = null;
    }
    private void ShowPage(FrameworkElement page)
    {
        if (_busy) return;
        if (page != HostedPage) LeaveHostedPage();
        foreach (var item in new FrameworkElement[] { OverviewPage, PreparePage, HostedPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.FontWeight = page == OverviewPage ? FontWeights.Bold : FontWeights.Normal;
        PrepareButton.FontWeight = page == PreparePage ? FontWeights.Bold : FontWeights.Normal;
        foreach (var button in new[] { OverviewButton, PrepareButton, SettingsButton })
        {
            var active = button == OverviewButton && page == OverviewPage || button == PrepareButton && page == PreparePage ||
                button == SettingsButton && page == HostedPage && _hostedWindow is SettingsWindow;
            button.Tag = active ? "active" : null;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
    private void Prepare_Click(object sender, RoutedEventArgs e) => ShowPage(PreparePage);
    private void Details_Click(object sender, RoutedEventArgs e) => OpenSettings(5);
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => OpenSettings(2);
    private void About_Click(object sender, RoutedEventArgs e) => OpenSettings(4);
    private void Host(Window window)
    {
        LeaveHostedPage();
        _hostedWindow = window;
        window.Owner = this;
        window.ShowInTaskbar = false;
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
        settings.ActivityHost.Content = _activityView;
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
        InvalidateDeviceInventory();
        await RunAsync(async () =>
        {
            KeyInfo.Text = "Reading connected keys…";
            KeyPinStatus.Text = ""; KeyRecovery.Visibility = Visibility.Collapsed;
            ReadinessText.Text = !IsAdministrator() ? "Restart as administrator to inspect FIDO PIN status and reset keys." : "";
            if (!_cliReady) ReadinessText.Text += "\nInstall YubiEnroll to enroll keys.";
            ReadinessCard.Visibility = ReadinessText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OverviewElevate.Visibility = IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0) { ApplyInventory(Array.Empty<KeyStatus>(), 0); KeyPinStatus.Text = "";  KeyInfo.Text = "Install YubiKey Manager CLI to inspect and manage connected keys. Tenant and profile settings remain available."; return; }
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
                ApplyInventory(entries, inventory.Unavailable);
                if (_selectedKey == null && _lastKnownKey == null) KeyInfo.Text = entries.Count == 0 ? "No supported key detected. Connect the target key and refresh." : "Choose the key you want to work on, then select Use this key.";
                if (inventory.Unavailable > 0) KeyInfo.Text += "\nSome devices could not be inspected. Check device access and FIDO support.";
            }
            catch { ApplyInventory(Array.Empty<KeyStatus>(), 0); KeyPinStatus.Text = ""; KeyInfo.Text = "Unable to inspect keys. Check prerequisites, close other key tools and refresh."; Record("Key inspection unavailable.", "Warning"); }
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
        SelectedKeyCard.Text = key == null ? (_lastKnownKey == null ? "No target key selected" : "Target disconnected\nSerial " + _lastKnownKey.Serial) : "Target key\n" + key.Key.Model + "\nSerial " + key.Key.Serial;
        RenderSelectedKey();
        SuggestRegistrationName();
        UpdateSummary();
        if (changed) Record(key == null ? "Selected key is unavailable." : $"Key selected · serial {key.Key.Serial} · {key.PinLabel}");
    }
    private IReadOnlyList<KeyStatus> _knownKeys = Array.Empty<KeyStatus>();
    private int _unavailableKeys;
    private bool _inventoryDirty = true;
    private bool _choosingTarget = true;
    private void ApplyInventory(IReadOnlyList<KeyStatus> entries, int unavailable)
    {
        _inventoryDirty = false;
        _knownKeys = entries;
        _unavailableKeys = unavailable;
        _readingKeys = true;
        try
        {
            OverviewKeys.ItemsSource = EnrollmentKeys.ItemsSource = entries;
            OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = entries.FirstOrDefault(x => x.Key.Serial == _selectionSerial);
        }
        finally { _readingKeys = false; }
        // Discovery never chooses a new target, even when there is just one device.
        SetSelectedKey(entries.FirstOrDefault(x => x.Key == _lastKnownKey));
    }
    private void OverviewKey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EnrollmentKeys == null || UseEnrollmentKey == null || _readingKeys) return;
        var candidate = (sender as ComboBox)?.SelectedItem as KeyStatus;
        _readingKeys = true;
        try { OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = candidate; }
        finally { _readingKeys = false; }
        UseOverviewKey.IsEnabled = UseEnrollmentKey.IsEnabled = !_inventoryDirty && candidate != null;
    }
    private void UseTargetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _inventoryDirty || OverviewKeys.SelectedItem is not KeyStatus key || !_knownKeys.Contains(key)) return;
        // A refresh keeps the recipient/name; intentionally choosing a different key starts a new assignment.
        if (_lastKnownKey != null && _lastKnownKey != key.Key)
        {
            ResetSelection();
            Query.Clear();
            KeyName.Clear();
            _suggestedName = "";
        }
        _choosingTarget = false;
        SetSelectedKey(key);
        StatusText.Text = "Target key selected · serial " + key.Key.Serial;
    }
    private void ChangeTargetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _choosingTarget = true;
        RenderSelectedKey();
        UpdateSummary();
    }
    private void RenderSelectedKey()
    {
        var key = _selectedKey;
        var choosing = _choosingTarget || key == null;
        OverviewChooser.Visibility = EnrollmentChooser.Visibility = choosing ? Visibility.Visible : Visibility.Collapsed;
        ChangeOverviewKey.Visibility = ChangeEnrollmentKey.Visibility = choosing ? Visibility.Collapsed : Visibility.Visible;
        KeyPinStatus.Text = key?.PinLabel ?? "";
        KeyPinStatus.Foreground = key?.Pin == FidoPinState.Blocked ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkSlateGray;
        KeyRecovery.Visibility = key?.Pin == FidoPinState.Blocked ? Visibility.Visible : Visibility.Collapsed;
        KeyInfo.Text = key == null ? (_lastKnownKey == null ? "No target key selected." : "Target key disconnected · serial " + _lastKnownKey.Serial) : key.Key.Model + " · Serial " + key.Key.Serial;
        EnrollmentTargetText.Text = key == null ? KeyInfo.Text : key.Key.Model + " · Serial " + key.Key.Serial;
        FooterTargetText.Text = key == null ? "No target key available" : "Target · " + key.Key.RegistrationName;
        ResetTargetButton.IsEnabled = key != null && !choosing && !_inventoryDirty && IsAdministrator();
        UseOverviewKey.IsEnabled = UseEnrollmentKey.IsEnabled = !_inventoryDirty && OverviewKeys.SelectedItem is KeyStatus;
        UpdateEnrollmentGuidance();
    }
    private void UpdateEnrollmentGuidance()
    {
        if (EnrollmentTargetNotice == null || FooterInstruction == null) return;
        var key = _selectedKey?.Key;
        var message = _inventoryDirty ? "Checking connected keys… Select Refresh keys if this does not update." :
            _choosingTarget ? "Choose the target key, then select Use this key to confirm." :
            EnrollmentTargetGuard.Explain(key, _knownKeys.Select(x => x.Key).ToArray(), _unavailableKeys, IsConnected);
        EnrollmentTargetNotice.Text = message;
        EnrollmentTargetNotice.Foreground = !_inventoryDirty && key != null && (!_choosingTarget && (!IsConnected || _knownKeys.Count == 1 && _unavailableKeys == 0))
            ? System.Windows.Media.Brushes.DarkSlateGray : System.Windows.Media.Brushes.SaddleBrown;
        FooterInstruction.Text = _inventoryDirty || _choosingTarget || key == null ? message :
            !IsConnected ? "Connect to the tenant to continue." :
            !EnrollmentTargetGuard.CanStart(key, _knownKeys.Select(x => x.Key).ToArray(), _unavailableKeys) ? message :
            Users.SelectedItem == null ? "Search for and select the user who will receive this key." :
            Profiles.SelectedItem == null ? "Choose an enrollment profile." :
            "Review the target, user and profile before starting.";
    }
    private void InvalidateDeviceInventory()
    {
        _inventoryDirty = true;
        EnrollButton.IsEnabled = ResetTargetButton.IsEnabled = UseOverviewKey.IsEnabled = UseEnrollmentKey.IsEnabled = false;
        UpdateEnrollmentGuidance();
    }
    private string _suggestedName = "";
    private void SuggestRegistrationName()
    {
        if (KeyName == null) return;
        var next = _selectedKey?.Key.RegistrationName ?? "";
        if (string.IsNullOrWhiteSpace(KeyName.Text) || KeyName.Text == _suggestedName) KeyName.Text = next;
        _suggestedName = next;
    }
    private System.Windows.Threading.DispatcherTimer? _deviceRefresh;
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TaskbarIdentity.Apply(this);
        if (_smokeTest) return;
        Closed += (_, _) => _deviceRefresh?.Stop();
        _deviceRefresh = new() { Interval = TimeSpan.FromSeconds(1) };
        _deviceRefresh.Tick += async (_, _) =>
        {
            if (_busy) return;
            _deviceRefresh.Stop();
            await RefreshOverviewAsync();
        };
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(DeviceMessage);
    }
    private IntPtr DeviceMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DEVICECHANGE: debounce bursts, do not inspect while a native operation owns the key.
        if (message == 0x0219) { InvalidateDeviceInventory(); _deviceRefresh?.Stop(); _deviceRefresh?.Start(); }
        return IntPtr.Zero;
    }
    private string _lastInteractiveFailure = "";
    private async Task<int> RunInteractiveAsync(string executable, string[] args)
    {
        _lastInteractiveFailure = "";
        if (!EmbeddedTerminal) return await new CliRunner(executable).InteractiveAsync(args);
        var enrollment = Path.GetFileName(executable).Equals("yubienroll.exe", StringComparison.OrdinalIgnoreCase) && args.Take(2).SequenceEqual(new[] { "credentials", "add" });
        var login = args.SequenceEqual(new[] { "login" });
        var terminal = new TerminalPanel(enrollment ? "Enroll key · serial " + _selectedKey?.Key.Serial : login ? "Connect to tenant" : "Reset FIDO · serial " + _selectedKey?.Key.Serial, enrollment, login);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = Visibility.Visible;
        try { var code = await terminal.RunAsync(executable, args); _lastInteractiveFailure = terminal.FailureDetail; return code; }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
    }
}
