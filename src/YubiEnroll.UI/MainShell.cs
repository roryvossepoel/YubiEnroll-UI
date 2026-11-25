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
        var compact = ActualWidth < 950;
        SidebarColumn.Width = new GridLength(compact ? 205 : 260);
        BrandTitle.FontSize = compact ? 13 : 19;
        var stackedCards = ActualWidth < 1100;
        HomeTasks.Columns = stackedCards ? 1 : 2;
        var cards = new[] { HomeSetupCard, HomeAdditionCard, HomeLocalCard, HomeManageCard, HomeResetCard, HomeBulkCard, HomeSearchCard };
        var layouts = new[] { HomeSetupLayout, HomeAdditionLayout, HomeLocalLayout, HomeManageLayout, HomeResetLayout, HomeBulkLayout, HomeSearchLayout };
        var icons = new[] { HomeSetupIcon, HomeAdditionIcon, HomeLocalIcon, HomeManageIcon, HomeResetIcon, HomeBulkIcon, HomeSearchIcon };
        var labels = new[] { HomeSetupText, HomeAdditionText, HomeLocalText, HomeManageText, HomeResetText, HomeBulkText, HomeSearchText };
        for (var i = 0; i < cards.Length; i++)
        {
            bool setup = i == 0 || i == 5;
            cards[i].Padding = new Thickness(14);
            cards[i].Margin = new Thickness(setup && !stackedCards && i == 5 ? 5 : 0, 0, setup && !stackedCards && i == 0 ? 5 : 0, 8);
            layouts[i].ColumnDefinitions[0].Width = GridLength.Auto;
            layouts[i].ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            layouts[i].ColumnDefinitions[2].Width = GridLength.Auto;
            icons[i].Margin = new Thickness(0,0,14,0);
            Grid.SetColumn(labels[i], 1); Grid.SetRow(labels[i], 0);
        }
        HomeBody.Margin = compact ? new Thickness(28) : new Thickness(36, 32, 36, 24);
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
        if ((page == OverviewPage || page == PreparePage || page == ResetPage || page == BatchPage) && RequirementsBlockTasks) page = StartupPage;
        if (page == LocalPage && (!_smokeTest || _startupChecksComplete) &&
            (_requirements?.Administrator != true || !_requirements.Manager.Ready)) page = StartupPage;
        if (TenantSearchPage.Visibility == Visibility.Visible && page != TenantSearchPage) ClearTenantSearch();
        if (LocalPage.Visibility == Visibility.Visible && page != LocalPage) ResetLocalPage();
        if (ManagePage.Visibility == Visibility.Visible && page != ManagePage) ResetManagement();
        if (page != HostedPage && _hostedWindow is SettingsWindow current && !current.ConfirmLeavePreferences()) return;
        if (page != HostedPage) LeaveHostedPage();
        if (_batchReviewRow != null && page == BatchPage) _batchReviewRow = null;
        if (page != BatchPage && (BatchPage.IsVisible || _batchReviewRow != null) &&
            !(page == ManagePage && _batchReviewRow != null)) ResetBatch();
        if (page.Visibility != Visibility.Visible) StatusText.Text = "";
        if (page != PreparePage && PreparePage.IsVisible && _enrollStep == EnrollmentResultStep) BeginNextKey();
        if (page != ResetPage && ResetPage.IsVisible && _resetStep == 2) { StopPinRead(); _resultKey = null; BeginNextKey(); }
        foreach (var item in new FrameworkElement[] { OverviewPage, PreparePage, BatchPage, ResetPage, LocalPage, ManagePage, TenantSearchPage, HostedPage, StartupPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { OverviewButton, PrepareButton, BulkNav, AddPasskeyButton, ResetButton, LocalButton, ManageButton, TenantSearchNav, SettingsButton })
        {
            var active = button == OverviewButton && page == OverviewPage || button == PrepareButton && page == PreparePage && !_addPasskey || button == BulkNav && page == BatchPage ||
                button == AddPasskeyButton && page == PreparePage && _addPasskey ||
                button == TenantSearchNav && page == TenantSearchPage || button == LocalButton && page == LocalPage || button == ManageButton && page == ManagePage || button == ResetButton && page == ResetPage || button == SettingsButton && page == HostedPage;
            button.Tag = active ? "active" : null;
        }
        // A clean Home has no technical footer. Errors/requirements remain discoverable in Settings.
        StatusBar.Visibility = page == OverviewPage || page == HostedPage || page == StartupPage || page == ManagePage || page == TenantSearchPage ? Visibility.Collapsed : Visibility.Visible;
        RenderWizard();
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_addPasskey) { _addPasskey = false; BeginNextKey(); }
        ShowPage(PreparePage);
        if (_enrollStep != EnrollmentResultStep && !RequirementsBlockTasks && (!_inventoryLoaded || _inventoryDirty) && !_busy) await RefreshOverviewAsync();
    }
    private async void AddPasskey_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_addPasskey) { _addPasskey = true; BeginNextKey(); }
        ShowPage(PreparePage);
        if (_enrollStep != EnrollmentResultStep && !RequirementsBlockTasks && (!_inventoryLoaded || _inventoryDirty)) await RefreshOverviewAsync();
    }
    private async void ResetPage_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ResetPage);
        if (_resetStep != 2 && !RequirementsBlockTasks && (!_inventoryLoaded || _inventoryDirty) && !_busy) await RefreshOverviewAsync();
    }
    private void Details_Click(object sender, RoutedEventArgs e) => OpenSettings(s => s.ActivityTab);
    private void Prerequisites_Click(object sender, RoutedEventArgs e) => OpenSettings(s => s.AboutTab);
    private void About_Click(object sender, RoutedEventArgs e) => OpenSettings(s => s.AboutTab);
    private void Host(Window window)
    {
        LeaveHostedPage(); _hostedWindow = window; window.Owner = this; window.ShowInTaskbar = false;
        var content = window.Content; window.Content = null; HostedPage.Content = content;
        ShowPage(HostedPage);
    }
    private KeyStatus? ProfileKeyForSettings() => LocalPage.IsVisible ? LocalKeys.SelectedItem as KeyStatus :
        BatchPage.IsVisible ? BatchKeys.SelectedValue as KeyStatus : EnrollmentKeys.SelectedItem as KeyStatus ?? _selectedKey;
    private void OpenSettings(Func<SettingsWindow, TabItem>? initialTab = null)
    {
        if (_busy) return;
        if (_hostedWindow is SettingsWindow current) { if (initialTab != null) current.SettingsTabs.SelectedItem = initialTab(current); return; }
        LeaveHostedPage();
        var settings = new SettingsWindow(_configPath, _exePath, _versionText, _registrationJournal, _activityJournal, _inventoryDirty ? null : ProfileKeyForSettings(), _policy) { EmbeddedTerminal = EmbeddedTerminal };
        settings.ActivityRecorded += text => Record(text);
        settings.SettingsTabs.SelectedItem = initialTab?.Invoke(settings) ?? settings.TenantsTab; settings.ActivityHost.Content = _activityView;
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
                    SetInventoryMessage("Install YubiKey Manager CLI, then return to Settings → Help & About → Required software.");
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
                SetInventoryMessage("Unable to read the connected keys. Check Settings → Help & About → Required software, close other key tools and Refresh.");
                Record("Key inspection unavailable.", "Warning");
            }
        }, "Inspect connected keys");
    }
    private void SetInventoryMessage(string text)
    {
        KeyInfo.Text = ResetInventoryInfo.Text = LocalInventoryHint.Text = text;
        var empty = _knownKeys.Count == 0;
        var hint = _inventoryDirty ? "Looking for connected keys." : EnrollmentReaderStatus.Visibility == Visibility.Visible
            ? "Connect a YubiKey by USB or place it on the NFC reader." : "Connect a YubiKey by USB.";
        foreach (var card in new[] { EnrollmentKeysPlaceholder, OverviewKeysPlaceholder, LocalKeysPlaceholder, BatchKeysPlaceholder })
        {
            card.Visibility = Visible(empty);
            card.Tag = _inventoryDirty ? "Checking for YubiKeys…" : text.Length > 0 ? "Key inspection unavailable" : "No YubiKey detected";
            card.Content = !_inventoryDirty && text.Length > 0 ? text : hint;
        }
        KeyInfo.Visibility = ResetInventoryInfo.Visibility = LocalInventoryHint.Visibility = Visible(!empty && !string.IsNullOrWhiteSpace(text));
        foreach (var list in new[] { EnrollmentKeys, OverviewKeys, LocalKeys, BatchKeys }) list.Visibility = Visible(!empty);
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
        UpdateBatchKeyCards();
        LocalKeys.ItemsSource = entries;
        RenderReaderInventory(readers ?? Array.Empty<NfcReaderStatus>());
        SetInventoryMessage(unavailable > 0 ? "Some USB devices could not be identified. Check device access and FIDO support." :
            "");
        SetSelectedKey(entries.FirstOrDefault(x => x.Key == _lastKnownKey));
        (_hostedWindow as SettingsWindow)?.UpdateProfileKey(ProfileKeyForSettings());
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
        var canUseTarget = !_inventoryDirty && EnrollmentTargetGuard.CanStart(_selectedKey?.Key, _knownKeys.Select(x => x.Key).ToArray(), _unavailableKeys);
        if (canUseTarget && _selectedKey?.Key is { IsNfc: true } key)
            message = $"Keep this YubiKey on {NfcReaderStatus.DisplayName(key.Reader!)}. Remove and replace it only when prompted.";
        EnrollmentTargetNotice.Text = ReviewTargetNotice.Text = message;
        TenantNoticeBox.Visibility = Visible(message.Length > 0);
        TenantNoticeBox.Tag = canUseTarget ? null : "Warning";
        RenderWizard();
    }
    private void InvalidateDeviceInventory()
    {
        _inventoryDirty = true;
        SetInventoryMessage("");
        (_hostedWindow as SettingsWindow)?.UpdateProfileKey(null);
        ResetLocalPage();
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
            var needsKeys = PreparePage.IsVisible && _enrollStep != EnrollmentResultStep ||
                (ResetPage.IsVisible && _resetStep != 2) || LocalPage.IsVisible || BatchPage.IsVisible && _batchStage == BatchStage.Issue;
            if (needsKeys && _inventoryDirty && _inventoryLoaded) await RefreshOverviewAsync();
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
    private async Task<int> RunInteractiveAsync(string executable, string[] args, ResetKey? explicitTarget = null, Func<int, string?, string?, Task<string?>>? nativeFinished = null, bool single = false, ProfileSetup? setup = null)
    {
        _lastInteractiveFailure = "";
        if (args.SequenceEqual(new[] { "login" }))
        {
            var provider = SelectedProvider;
            var tenant = TenantConfigWriter.Read(File.ReadAllText(_configPath), provider.Name);
            var batch = BatchPage.Visibility == Visibility.Visible;
            var loginPanel = new LoginPanel(provider.Name,
                eyebrow: batch ? "SET UP MULTIPLE YUBIKEYS" : EnrollmentTaskLabel.Text,
                title: batch ? BatchTitle.Text : EnrollHeading.Text,
                drawSteps: grid => {
                    if (batch) RenderProgress(grid, BatchStepLabels, (int)_batchStage);
                    else RenderProgress(grid, EnrollmentStepLabels, Math.Min(_enrollStep, ReviewStep));
                });
            _activeLogin = loginPanel;
            TerminalOverlay.Content = loginPanel; TerminalOverlay.Visibility = Visibility.Visible;
            try { var result = await loginPanel.RunAsync(executable, tenant); _lastInteractiveFailure = result == 0 ? "" : loginPanel.FailureDetail; return result; }
            finally { _activeLogin = null; TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
        }
        if (!EmbeddedTerminal && !_captureGeneratedPin)
        {
            var exit = await new CliRunner(executable).InteractiveAsync(args);
            if (nativeFinished != null) await nativeFinished(exit, null, null);
            return exit;
        }
        var enrollment = Path.GetFileName(executable).Equals("yubienroll.exe", StringComparison.OrdinalIgnoreCase) && args.Take(2).SequenceEqual(new[] { "credentials", "add" });
        var login = args.SequenceEqual(new[] { "login" });
        var terminalTarget = explicitTarget ?? _selectedKey?.Key;
        var terminal = new TerminalPanel(enrollment ? (_addPasskey ? "Add a passkey · serial " : "Set up a YubiKey · serial ") + terminalTarget?.Serial : "Clear passkeys & PIN · serial " + terminalTarget?.Serial, enrollment, login, _addPasskey, _captureGeneratedPin ? terminalTarget?.Serial : null, terminalTarget?.IsNfc == true, single, _captureGeneratedPin && InitialPinLogging, setup?.Name);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = Visibility.Visible;
        terminal.NativeFinished = nativeFinished;
        try { var code = await terminal.RunAsync(executable, args); _lastInteractiveFailure = terminal.FailureDetail; return code; }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
    }
}
