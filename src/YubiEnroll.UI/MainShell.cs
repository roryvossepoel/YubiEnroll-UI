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
        AboutButton.Content = compact ? "About" : "Help & About";
        var narrow = ActualWidth < 1000;
        PreparationColumns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(PreparationCard, narrow ? 0 : 1);
        Grid.SetRow(PreparationCard, narrow ? 1 : 0);
        UserCard.Margin = narrow ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 8, 0);
        PreparationCard.Margin = narrow ? new Thickness(0) : new Thickness(8, 0, 0, 0);
    }
    private Window? _hostedWindow;
    internal bool EmbeddedTerminal { get; set; } // Opt-in preview until physical-key acceptance is complete.
    private void LeaveHostedPage()
    {
        if (_hostedWindow is SettingsWindow settings)
        {
            EmbeddedTerminal = settings.EmbeddedTerminal;
            if (settings.Changed) LoadConfiguration(_configPath);
        }
        HostedPage.Content = null;
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
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
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
        var previous = (OverviewKeys.SelectedItem as KeyStatus)?.Key;
        await RunAsync(async () =>
        {
            OverviewKeys.ItemsSource = null;
            KeyInfo.Text = "Reading connected keys…";
            ReadinessText.Text = $"YubiEnroll: {_versionText}\nAdministrator: {(IsAdministrator() ? "Yes" : "No — required for FIDO access")}\nConfiguration: {_config?.Providers.Count ?? 0} tenant(s), {_config?.Profiles.Count ?? 0} profile(s)";
            var path = KeyResetService.FindExecutable();
            if (path.Length == 0) { KeyInfo.Text = "Install YubiKey Manager CLI to inspect and manage connected keys. Tenant and profile settings remain available."; return; }
            var runner = new CliRunner(path);
            var service = new KeyResetService(a => runner.CaptureAsync(a), _ => throw new InvalidOperationException());
            try
            {
                var version = await service.CheckVersionAsync();
                ReadinessText.Text += $"\nYubiKey Manager CLI: {version}";
                var inventory = await service.DiscoverAsync();
                var entries = new List<KeyStatus>();
                foreach (var key in inventory.Keys)
                {
                    var pin = FidoPinState.Unknown;
                    if (IsAdministrator()) try { pin = await service.ReadPinStateAsync(key); } catch { }
                    entries.Add(new(key, pin));
                }
                OverviewKeys.ItemsSource = entries;
                OverviewKeys.SelectedItem = entries.FirstOrDefault(x => x.Key == previous);
                // A single key can be inspected automatically; reset still requires explicit selection/review.
                if (previous == null && entries.Count == 1) OverviewKeys.SelectedIndex = 0;
                if (OverviewKeys.SelectedItem == null) KeyInfo.Text = entries.Count == 0 ? "Connect a USB YubiKey to get started. Only keys with FIDO enabled and a readable serial can be inspected." : "Choose a key to inspect its status.";
            }
            catch { KeyInfo.Text = "Could not inspect the keys. Check prerequisites, close other key tools and refresh."; }
        }, "Inspect connected keys");
    }
    private void OverviewKey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedKeyCard == null) return;
        if (OverviewKeys.SelectedItem is not KeyStatus key) { SelectedKeyCard.Text = "No key selected"; return; }
        SelectedKeyCard.Text = "Inspecting\n" + key.Label;
        KeyInfo.Text = key.Label + "\n" + key.PinLabel + "\nStatus is read-only. It does not verify whether all credentials have been erased.";
    }
    private async Task<int> RunInteractiveAsync(string executable, string[] args)
    {
        if (!EmbeddedTerminal) return await new CliRunner(executable).InteractiveAsync(args);
        var enrollment = Path.GetFileName(executable).Equals("yubienroll.exe", StringComparison.OrdinalIgnoreCase) && args.Take(2).SequenceEqual(new[] { "credentials", "add" });
        var terminal = new TerminalPanel(enrollment ? "Prepare Key — interactive operation" : "Interactive operation", enrollment);
        TerminalOverlay.Content = terminal; TerminalOverlay.Visibility = Visibility.Visible;
        try { return await terminal.RunAsync(executable, args); }
        finally { TerminalOverlay.Content = null; TerminalOverlay.Visibility = Visibility.Collapsed; }
    }
}
