using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private Window? _hostedWindow;
    internal bool EmbeddedTerminal { get; set; } = true;
    private KeyStatus? _selectedKey;
    private string? _selectionSerial;
    private ResetKey? _lastKnownKey;
    private bool _readingKeys;
    private IReadOnlyList<KeyStatus> _knownKeys = Array.Empty<KeyStatus>();
    private int _unavailableKeys;
    private bool _inventoryDirty = true;
    private bool _inventoryLoaded;
    private bool _choosingTarget = true;
    private string _suggestedName = "";
    private System.Windows.Threading.DispatcherTimer? _deviceRefresh;
    private string _lastInteractiveFailure = "";

    private void ResizePreparation()
    {
        Users.MinHeight = 80;
        Users.MaxHeight = ActualHeight < 800 ? 140 : 220;
        var compact = ActualWidth < 950;
        SidebarColumn.Width = new GridLength(compact ? 160 : 230);
        BrandTitle.FontSize = compact ? 13 : 19;
        BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HomeTasks.Columns = ActualWidth < 950 ? 1 : 2;
        HomeBody.Margin = compact ? new Thickness(28) : new Thickness(44, 64, 44, 32);
    }
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
        if ((page == OverviewPage || page == PreparePage || page == ResetPage) && RequirementsBlockTasks) page = StartupPage;
        if (page != HostedPage) LeaveHostedPage();
        foreach (var item in new FrameworkElement[] { OverviewPage, PreparePage, ResetPage, HostedPage, StartupPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { OverviewButton, PrepareButton, ResetButton, SettingsButton })
        {
            var active = button == OverviewButton && page == OverviewPage || button == PrepareButton && page == PreparePage ||
                button == ResetButton && page == ResetPage || button == SettingsButton && page == HostedPage;
            button.Tag = active ? "active" : null;
        }
        // A clean Home has no technical footer. Errors/requirements remain discoverable in Settings.
        StatusBar.Visibility = page == OverviewPage || page == HostedPage || page == StartupPage ? Visibility.Collapsed : Visibility.Visible;
        RenderWizard();
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(PreparePage);
        if (!RequirementsBlockTasks && !_inventoryLoaded && !_busy) await RefreshOverviewAsync();
    }
    private async void ResetPage_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ResetPage);
        if (!RequirementsBlockTasks && !_inventoryLoaded && !_busy) await RefreshOverviewAsync();
    }
    private void Details_Click(object sender, RoutedEventArgs e) => OpenSettings(5);
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => OpenSettings(2);
    private void About_Click(object sender, RoutedEventArgs e) => OpenSettings(4);
    private void Host(Window window)
    {
        LeaveHostedPage(); _hostedWindow = window; window.Owner = this; window.ShowInTaskbar = false;
        var content = window.Content; window.Content = null; HostedPage.Content = content;
        ShowPage(HostedPage);
    }
    private void OpenSettings(int tab = 0)
    {
        if (_busy) return;
        LeaveHostedPage();
        var settings = new SettingsWindow(_configPath, _exePath, _versionText) { EmbeddedTerminal = EmbeddedTerminal };
        settings.SettingsTabs.SelectedIndex = tab; settings.ActivityHost.Content = _activityView;
        if (_requirements != null) settings.SetRequirements(_requirements);
        Host(settings);
        if (!_smokeTest && _startupChecksComplete) _ = CheckRequirementsAsync();
    }
    private async void RefreshKeys_Click(object sender, RoutedEventArgs e) => await RefreshOverviewAsync();
    private async Task RefreshOverviewAsync()
    {
        if (_busy) return;
        InvalidateDeviceInventory();
        await RunAsync(async () =>
        {
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0)
            {
                ApplyInventory(Array.Empty<KeyStatus>(), 0);
                SetInventoryMessage("Install YubiKey Manager CLI, then return to Settings → Prerequisites."); return;
            }
            var runner = new CliRunner(path);
            var service = new KeyResetService(a => runner.CaptureAsync(a), _ => throw new InvalidOperationException());
            try
            {
                await service.CheckVersionAsync();
                var inventory = await service.DiscoverAsync();
                var entries = new List<KeyStatus>();
                foreach (var key in inventory.Keys)
                {
                    var pin = FidoPinState.Unknown;
                    if (IsAdministrator()) try { pin = await service.ReadPinStateAsync(key); } catch { }
                    entries.Add(new(key, pin));
                }
                ApplyInventory(entries, inventory.Unavailable);
            }
            catch
            {
                ApplyInventory(Array.Empty<KeyStatus>(), 0);
                SetInventoryMessage("Unable to read the connected keys. Check Settings → Prerequisites, close other key tools and Refresh.");
                Record("Key inspection unavailable.", "Warning");
            }
        }, "Inspect connected keys");
    }
    private void SetInventoryMessage(string text)
    {
        KeyInfo.Text = ResetInventoryInfo.Text = text;
    }
    private void SetSelectedKey(KeyStatus? key)
    {
        var changed = _selectedKey?.Key != key?.Key || _selectedKey?.Pin != key?.Pin;
        _selectedKey = key;
        if (key != null) { _selectionSerial = key.Key.Serial; _lastKnownKey = key.Key; }
        SuggestRegistrationName(); UpdateSummary();
        if (changed) Record(key == null ? "Selected key is unavailable." : $"Key selected · serial {key.Key.Serial} · {key.PinLabel}");
    }
    private void ApplyInventory(IReadOnlyList<KeyStatus> entries, int unavailable)
    {
        _inventoryDirty = false; _inventoryLoaded = true; _knownKeys = entries; _unavailableKeys = unavailable;
        // Preserve an unconfirmed choice across a refresh, but never make the first choice for the operator.
        var candidate = (EnrollmentKeys.SelectedItem as KeyStatus)?.Key ?? _lastKnownKey;
        _readingKeys = true;
        try
        {
            OverviewKeys.ItemsSource = EnrollmentKeys.ItemsSource = entries;
            OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = entries.FirstOrDefault(x => x.Key == candidate);
        }
        finally { _readingKeys = false; }
        SetInventoryMessage(unavailable > 0 ? "Some devices could not be identified. Check device access and FIDO support." :
            entries.Count == 0 ? "No supported key detected. Connect the target key and Refresh." : "");
        SetSelectedKey(entries.FirstOrDefault(x => x.Key == _lastKnownKey));
    }
    private void OverviewKey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EnrollmentKeys == null || OverviewKeys == null || _readingKeys) return;
        var candidate = (sender as Selector)?.SelectedItem as KeyStatus;
        _readingKeys = true;
        try { OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = candidate; }
        finally { _readingKeys = false; }
        _choosingTarget = candidate?.Key != _selectedKey?.Key || candidate == null;
        // A changed candidate invalidates previous review/acknowledgment immediately.
        if (_choosingTarget) { _enrollStep = 0; _resetStep = 0; ResetAcknowledged.IsChecked = false; _resetReviewTarget = null; }
        UpdateSummary();
    }
    private bool CommitTarget()
    {
        if (_busy || _inventoryDirty || EnrollmentKeys.SelectedItem is not KeyStatus key || !_knownKeys.Contains(key)) return false;
        if (_lastKnownKey != null && _lastKnownKey != key.Key)
        {
            ResetSelection(); Query.Clear(); KeyName.Clear(); _suggestedName = "";
            _enrollStep = 0; _resetStep = 0; ResetAcknowledged.IsChecked = false; _resetReviewTarget = null;
        }
        _choosingTarget = false; SetSelectedKey(key);
        StatusText.Text = "Target · " + key.Key.Label;
        return true;
    }
    private void UseTargetKey_Click(object sender, RoutedEventArgs e) => CommitTarget();
    private void ChangeTargetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _choosingTarget = true; _enrollStep = 0; _resetStep = 0;
        ResetAcknowledged.IsChecked = false; _resetReviewTarget = null; UpdateSummary();
    }
    private void RenderSelectedKey() => RenderWizard();
    private void UpdateEnrollmentGuidance()
    {
        if (EnrollmentTargetNotice == null) return;
        var message = _inventoryDirty ? "Checking connected keys…" :
            EnrollmentTargetGuard.Explain(_selectedKey?.Key, _knownKeys.Select(x => x.Key).ToArray(), _unavailableKeys, IsConnected);
        EnrollmentTargetNotice.Text = ReviewTargetNotice.Text = message;
        RenderWizard();
    }
    private void InvalidateDeviceInventory()
    {
        _inventoryDirty = true;
        EnrollButton.IsEnabled = ResetTargetButton.IsEnabled = EnrollNext.IsEnabled = ResetNext.IsEnabled = false;
        // A USB change invalidates a destructive acknowledgment even if the same key returns.
        ResetAcknowledged.IsChecked = false;
        SetInventoryMessage("Checking connected keys…"); UpdateEnrollmentGuidance();
    }
    private void SuggestRegistrationName()
    {
        if (KeyName == null) return;
        var next = _selectedKey?.Key.RegistrationName ?? "";
        if (string.IsNullOrWhiteSpace(KeyName.Text) || KeyName.Text == _suggestedName) KeyName.Text = next;
        _suggestedName = next;
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e); TaskbarIdentity.Apply(this);
        if (_smokeTest) return;
        _deviceRefresh = new() { Interval = TimeSpan.FromSeconds(1) };
        _deviceRefresh.Tick += async (_, _) =>
        {
            if (_busy) return;
            _deviceRefresh.Stop();
            if (_inventoryLoaded) await RefreshOverviewAsync();
        };
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(DeviceMessage);
    }
    private IntPtr DeviceMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0219) { InvalidateDeviceInventory(); _deviceRefresh?.Stop(); _deviceRefresh?.Start(); }
        return IntPtr.Zero;
    }
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
