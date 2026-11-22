using System.IO;
using System.Windows;
using YubiEnroll.Core;
using System.Windows.Media;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private RequirementReport? _requirements;
    private bool _checkingRequirements;
    private bool _startupChecksComplete;
    private bool RequirementsBlockTasks => (!_smokeTest || _startupChecksComplete) && (_requirements?.Ready != true || _checkingRequirements);

    private async Task StartApplicationAsync()
    {
        ShowPage(StartupPage);
        Navigation.IsEnabled = SettingsButton.IsEnabled = false;
        try { await ApplyStartupPolicyAsync(_policy, () => ConfigReader.FindPath()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupPolicyStatus.Text = "Could not apply organization policy.";
            App.StopForPolicyError(ex);
            return;
        }
        await InitializeAsync();
        await CheckRequirementsAsync();
        _startupChecksComplete = true;
    }

    // A requirement refresh (including returning from an installer) never reapplies policy.
    private async Task ApplyStartupPolicyAsync(DeskPolicy? policy, Func<string> resolvePath)
    {
        if (policy == null) return;
        StartupPolicyStep.Visibility = Visibility.Visible;
        if (!policy.ManagesTenants && !policy.ManagesProfiles)
        {
            StartupPolicyStatus.Text = "App settings applied. No TOML synchronization needed.";
            return;
        }
        StartupPolicyStatus.Text = "Policy detected. Synchronizing the YubiEnroll TOML…";
        // Allow the startup page to paint before starting file I/O; no artificial delay.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        var result = await Task.Run(() => DeskPolicy.Synchronize(policy, resolvePath));
        StartupPolicyStatus.Text = result switch
        {
            PolicySyncResult.TomlCreated => "YubiEnroll TOML created from policy.",
            PolicySyncResult.TomlUpdated => "YubiEnroll TOML updated from policy.",
            PolicySyncResult.TomlUnchanged => "YubiEnroll TOML already up to date.",
            PolicySyncResult.NoTomlNeeded => "Policy loaded. No TOML file needed for an empty tenant list.",
            _ => throw new InvalidOperationException("Unexpected policy synchronization result.")
        };
    }

    private async void Window_Activated(object? sender, EventArgs e)
    {
        if (_smokeTest || !_startupChecksComplete || _busy || _checkingRequirements) return;
        // Returning from a browser/installer refreshes requirements, not keys, configuration or tenant login.
        if (StartupPage.Visibility == Visibility.Visible ||
            _hostedWindow is SettingsWindow { AboutTab.IsSelected: true })
            await CheckRequirementsAsync();
    }

    private async Task CheckRequirementsAsync(Func<Task<RequirementReport>>? probe = null)
    {
        if (_checkingRequirements || _busy) return;
        _checkingRequirements = true;
        Navigation.IsEnabled = SettingsButton.IsEnabled = false;
        StartupElevate.IsEnabled = StartupRequirements.IsEnabled = false;
        try
        {
            var report = await (probe?.Invoke() ?? Requirements.ProbeAsync(IsAdministrator(), Requirements.FindEnrollment(), YubiEnroll.Core.KeyResetService.FindExecutable()));
            _exePath = report.EnrollmentPath; _versionText = report.Enrollment.Detail; _cliReady = report.Enrollment.Ready;
            _requirements = report;
            RenderRequirements(report);
            if (_hostedWindow is SettingsWindow settings) settings.SetRequirements(report);
        }
        finally
        {
            _checkingRequirements = false;
            Navigation.IsEnabled = SettingsButton.IsEnabled = true;
            StartupElevate.IsEnabled = StartupRequirements.IsEnabled = true;
            UpdateSummary();
        }
        if (_requirements?.Ready == true && StartupPage.Visibility == Visibility.Visible) ShowPage(OverviewPage);
    }

    private void RenderRequirements(RequirementReport report)
    {
        StartupSummary.Text = report.Ready ? "Everything is ready. Opening Home…" : "Complete these requirements before working with a key.";
        StartupAdminStatus.Text = report.Administrator ? "Ready" : "Administrator access is required";
        StartupEnrollStatus.Text = report.Enrollment.Detail;
        StartupManagerStatus.Text = report.Manager.Detail;
        var normal = new SolidColorBrush(Color.FromRgb(38, 50, 56));
        var attention = new SolidColorBrush(Color.FromRgb(133, 64, 24));
        StartupAdminStatus.Foreground = report.Administrator ? normal : attention;
        StartupEnrollStatus.Foreground = report.Enrollment.Ready ? normal : attention;
        StartupManagerStatus.Foreground = report.Manager.Ready ? normal : attention;
        StartupElevate.Visibility = report.Administrator ? Visibility.Collapsed : Visibility.Visible;
        StartupRequirements.Visibility = report.Enrollment.Ready && report.Manager.Ready ? Visibility.Collapsed : Visibility.Visible;
        StartupFeedback.Text = report.Administrator ? "" : "Restarting opens the Windows permission prompt. Using a different administrator account loads that account’s configuration and sign-in. ";
    }
}
