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
    private int _deviceGeneration;
    private bool _choosingTarget = true;
    private string _suggestedName = "";
    private System.Windows.Threading.DispatcherTimer? _deviceRefresh;
    private NfcPresenceMonitor? _nfcPresence;
    private string _lastInteractiveFailure = "";

    private void ResizePreparation()
    {
        Users.MinHeight = 80;
        Users.MaxHeight = ActualHeight < 800 ? 140 : 220;
        var compact = ActualWidth < 950;
        SidebarColumn.Width = new GridLength(compact ? 160 : 230);
        BrandTitle.FontSize = compact ? 13 : 19;
        BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var stackedCards = ActualWidth < 1100;
        HomeTasks.Columns = stackedCards ? 1 : 2;
        var cards = new[] { HomeSetupCard, HomeAdditionCard, HomeResetCard, HomeManageCard };
        var layouts = new[] { HomeSetupLayout, HomeAdditionLayout, HomeResetLayout, HomeManageLayout };
        var icons = new[] { HomeSetupIcon, HomeAdditionIcon, HomeResetIcon, HomeManageIcon };
        var labels = new[] { HomeSetupText, HomeAdditionText, HomeResetText, HomeManageText };
        var arrows = new[] { HomeSetupArrow, HomeAdditionArrow, HomeResetArrow, HomeManageArrow };
        for (var i = 0; i < cards.Length; i++)
        {
            cards[i].Padding = new Thickness(stackedCards ? 16 : 24);
            cards[i].Margin = stackedCards ? new Thickness(0, 0, 0, 12) : new Thickness(i % 2 == 0 ? 0 : 6, 0, i % 2 == 0 ? 6 : 0, 12);
            layouts[i].ColumnDefinitions[0].Width = stackedCards ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            layouts[i].ColumnDefinitions[1].Width = stackedCards ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            layouts[i].ColumnDefinitions[2].Width = stackedCards ? GridLength.Auto : new GridLength(0);
            icons[i].Margin = stackedCards ? new Thickness(0, 0, 20, 0) : new Thickness(0, 0, 0, 20);
            Grid.SetColumn(labels[i], stackedCards ? 1 : 0);
            Grid.SetRow(labels[i], stackedCards ? 0 : 1);
            Grid.SetColumn(arrows[i], stackedCards ? 2 : 0);
            Grid.SetRow(arrows[i], stackedCards ? 0 : 2);
            arrows[i].Margin = stackedCards ? new Thickness(12, 0, 0, 0) : new Thickness(0);
        }
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
        if (ManagePage.Visibility == Visibility.Visible && page != ManagePage) ResetManagement();
        if (page != HostedPage) LeaveHostedPage();
        foreach (var item in new FrameworkElement[] { OverviewPage, PreparePage, ResetPage, ManagePage, HostedPage, StartupPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { OverviewButton, PrepareButton, AddPasskeyButton, ResetButton, ManageButton, SettingsButton })
        {
            var active = button == OverviewButton && page == OverviewPage || button == PrepareButton && page == PreparePage && !_addPasskey ||
                button == AddPasskeyButton && page == PreparePage && _addPasskey ||
                button == ManageButton && page == ManagePage || button == ResetButton && page == ResetPage || button == SettingsButton && page == HostedPage;
            button.Tag = active ? "active" : null;
        }
        // A clean Home has no technical footer. Errors/requirements remain discoverable in Settings.
        StatusBar.Visibility = page == OverviewPage || page == HostedPage || page == StartupPage || page == ManagePage ? Visibility.Collapsed : Visibility.Visible;
        RenderWizard();
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_addPasskey) { _addPasskey = false; BeginNextKey(); }
        ShowPage(PreparePage);
        if (!RequirementsBlockTasks && (!_inventoryLoaded || _inventoryDirty) && !_busy) await RefreshOverviewAsync();
    }
    private async void AddPasskey_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_addPasskey) { _addPasskey = true; BeginNextKey(); }
        ShowPage(PreparePage);
        if (!RequirementsBlockTasks && (!_inventoryLoaded || _inventoryDirty)) await RefreshOverviewAsync();
    }
    private async void ResetPage_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ResetPage);
        if (!RequirementsBlockTasks && (!_inventoryLoaded || _inventoryDirty) && !_busy) await RefreshOverviewAsync();
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
        var settings = new SettingsWindow(_configPath, _exePath, _versionText, _registrationJournal) { EmbeddedTerminal = EmbeddedTerminal };
        settings.SettingsTabs.SelectedIndex = tab; settings.ActivityHost.Content = _activityView;
        settings.PrerequisitesOpened += async () =>
        {
            if (!_smokeTest && _startupChecksComplete && !_busy) await CheckRequirementsAsync();
        };
        if (_requirements != null) settings.SetRequirements(_requirements);
        Host(settings);
        if (!_smokeTest && _startupChecksComplete) _ = CheckRequirementsAsync();
    }
    private async void RefreshKeys_Click(object sender, RoutedEventArgs e) => await RefreshOverviewAsync();
    private async Task RefreshOverviewAsync()
    {
        if (_busy) return;
        InvalidateDeviceInventory();
        var generation = _deviceGeneration;
        await RunAsync(async () =>
        {
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0)
            {
                if (TryApplyInventory(Array.Empty<KeyStatus>(), 0, generation))
                    SetInventoryMessage("Install YubiKey Manager CLI, then return to Settings → Prerequisites.");
                return;
            }
            var runner = new CliRunner(path);
            var service = new KeyResetService(a => runner.CaptureAsync(a), _ => throw new InvalidOperationException(), () => NfcDiscovery.ReadAsync(runner));
            try
            {
                await service.CheckVersionAsync();
                var inventory = await service.DiscoverAsync();
                var entries = new List<KeyStatus>();
                foreach (var key in inventory.Keys)
                {
                    var pin = FidoPinState.Unknown;
                    if (IsAdministrator()) try { pin = await service.ReadPinStateAsync(key); } catch { }
                    FidoCapabilities? capabilities = null;
                    if (IsAdministrator()) try { capabilities = await FidoCapabilityProbe.ReadAsync(runner, key); } catch { }
                    entries.Add(new(key, pin, capabilities));
                }
                TryApplyInventory(entries, inventory.Unavailable, generation, inventory.ReaderStates);
            }
            catch
            {
                if (!TryApplyInventory(Array.Empty<KeyStatus>(), 0, generation)) return;
                SetInventoryMessage("Unable to read the connected keys. Check Settings → Prerequisites, close other key tools and Refresh.");
                Record("Key inspection unavailable.", "Warning");
            }
        }, "Inspect connected keys");
    }
    private void SetInventoryMessage(string text)
    {
        KeyInfo.Text = ResetInventoryInfo.Text = text;
        KeyInfo.Visibility = ResetInventoryInfo.Visibility = Visible(!string.IsNullOrWhiteSpace(text));
    }
    private void SetSelectedKey(KeyStatus? key)
    {
        var changed = _selectedKey?.Key != key?.Key || _selectedKey?.Pin != key?.Pin;
        _selectedKey = key;
        if (key != null) { _selectionSerial = key.Key.Serial; _lastKnownKey = key.Key; }
        SuggestRegistrationName(); UpdateSummary();
        if (changed) Record(key == null ? "Selected key is unavailable." : $"Key selected · serial {key.Key.Serial} · {key.PinLabel}");
    }
    private bool TryApplyInventory(IReadOnlyList<KeyStatus> entries, int unavailable, int generation, IReadOnlyList<NfcReaderStatus>? readers = null)
    {
        // A USB event during discovery makes that snapshot stale. The scheduled refresh will retry once idle.
        if (generation != _deviceGeneration) return false;
        ApplyInventory(entries, unavailable, readers);
        return true;
    }
    private void ApplyInventory(IReadOnlyList<KeyStatus> entries, int unavailable, IReadOnlyList<NfcReaderStatus>? readers = null)
    {
        var previousTarget = _selectedKey?.Key ?? _lastKnownKey;
        var targetMissing = previousTarget != null && !entries.Any(x => x.Key == previousTarget);
        _inventoryDirty = false; _inventoryLoaded = true; _knownKeys = entries; _unavailableKeys = unavailable;
        if (targetMissing)
        {
            _choosingTarget = true;
            ResetAcknowledged.IsChecked = false;
            _resetReviewTarget = null;
            if (_resetStep == 1) _resetStep = 0;
            if (_enrollStep > 0 && _enrollStep < EnrollmentResultStep) _enrollStep = 0;
            StatusText.Text = "Target unavailable · serial " + previousTarget!.Serial + ". Select a connected key to continue.";
        }
        // Preserve an unconfirmed choice across a refresh, but never make the first choice for the operator.
        var candidate = (EnrollmentKeys.SelectedItem as KeyStatus)?.Key ?? _lastKnownKey;
        _readingKeys = true;
        try
        {
            OverviewKeys.ItemsSource = EnrollmentKeys.ItemsSource = entries;
            OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = entries.FirstOrDefault(x => x.Key == candidate);
        }
        finally { _readingKeys = false; }
        RenderReaderInventory(readers ?? Array.Empty<NfcReaderStatus>());
        SetInventoryMessage(unavailable > 0 ? "Some USB devices could not be identified. Check device access and FIDO support." :
            entries.Count == 0 ? (EnrollmentReaderStatus.Visibility == Visibility.Visible
                ? "Connect a YubiKey by USB or place it on the NFC reader."
                : "No key connected. Connect a YubiKey by USB.") : "");
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
        var message = _inventoryDirty ? "" :
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
        // Keep the last inventory visible, but gate every action until the new snapshot arrives.
        UpdateEnrollmentGuidance();
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
        _nfcPresence = new(() => Dispatcher.BeginInvoke(() => { if (IsLoaded) ScheduleDeviceRefresh(); }));
        Closed += (_, _) => { _nfcPresence?.Dispose(); _deviceRefresh?.Stop(); };
        _deviceRefresh = new() { Interval = TimeSpan.FromSeconds(1) };
        _deviceRefresh.Tick += async (_, _) =>
        {
            if (_busy || _checkingRequirements) return;
            _deviceRefresh.Stop();
            if (_inventoryLoaded && ManagePage.Visibility != Visibility.Visible) await RefreshOverviewAsync();
        };
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(DeviceMessage);
    }
    private IntPtr DeviceMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0219 && (wParam.ToInt64() is 0x0007 or 0x8000 or 0x8004)) ScheduleDeviceRefresh();
        return IntPtr.Zero;
    }
    private void ScheduleDeviceRefresh()
    {
        _deviceGeneration++;
        InvalidateDeviceInventory();
        _deviceRefresh?.Stop();
        _deviceRefresh?.Start();
    }
    private LoginPanel? _activeLogin;
    private bool _closeAfterLogin;
    private bool _captureGeneratedPin;
    private string? _generatedPin;
    private async Task<int> RunInteractiveAsync(string executable, string[] args)
    {
        _lastInteractiveFailure = "";
        if (args.SequenceEqual(new[] { "login" }))
        {
            var provider = SelectedProvider;
            var tenant = TenantConfigWriter.Read(File.ReadAllText(_configPath), provider.Name);
            var loginPanel = new LoginPanel(provider.Name);
            _activeLogin = loginPanel;
            TerminalOverlay.Content = loginPanel; TerminalOverlay.Visibility = Visibility.Visible;
            try { var result = await loginPanel.RunAsync(executable, tenant); _lastInteractiveFailure = result == 0 ? "" : loginPanel.FailureDetail; return result; }
            finally { _activeLogin = null; TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
        }
        if (!EmbeddedTerminal && !_captureGeneratedPin) return await new CliRunner(executable).InteractiveAsync(args);
        var enrollment = Path.GetFileName(executable).Equals("yubienroll.exe", StringComparison.OrdinalIgnoreCase) && args.Take(2).SequenceEqual(new[] { "credentials", "add" });
        var login = args.SequenceEqual(new[] { "login" });
        var terminal = new TerminalPanel(enrollment ? (_addPasskey ? "Add a passkey · serial " : "Set up a YubiKey · serial ") + _selectedKey?.Key.Serial : "Clear passkeys & PIN · serial " + _selectedKey?.Key.Serial, enrollment, login, _addPasskey, _captureGeneratedPin ? _selectedKey?.Key.Serial : null, _selectedKey?.Key.IsNfc == true);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = Visibility.Visible;
        try { var code = await terminal.RunAsync(executable, args); _lastInteractiveFailure = terminal.FailureDetail; _generatedPin = terminal.GeneratedPin; return code; }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
    }
}
