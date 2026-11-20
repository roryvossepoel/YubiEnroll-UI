using System.Windows;
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
        await InitializeAsync();
        await CheckRequirementsAsync();
        _startupChecksComplete = true;
    }

    private async void Window_Activated(object? sender, EventArgs e)
    {
        if (_smokeTest || !_startupChecksComplete || _busy || _checkingRequirements) return;
        // Returning from a browser/installer refreshes requirements, not keys, configuration or tenant login.
        if (StartupPage.Visibility == Visibility.Visible ||
            _hostedWindow is SettingsWindow { PrerequisitesTab.IsSelected: true })
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
        StartupFeedback.Text = report.Administrator ? "" : "Restarting opens the Windows permission prompt. Using a different administrator account loads that account’s configuration and sign-in.";
    }
}
