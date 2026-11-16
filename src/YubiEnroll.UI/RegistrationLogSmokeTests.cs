using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private void TestRegistrationLogView(SettingsWindow settings)
    {
        var tenant = new Provider("Demo tenant", "ENTRA", "11111111-2222-3333-4444-555555555555", "Managed PIN", "fixture");
        var user = new UserCandidate("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Synthetic user")
        { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test", Email = "alex.mail@example.test" };
        var target = new ResetKey("00123456", "YubiKey 5 NFC (5.7.1)");
        var profile = ProfileCompatibility.Managed("Managed PIN") with { Reset = true };
        Assert(!_registrationJournal.Preferences.Enabled, "Registration logging starts disabled");
        _registrationJournal.Configure(true, _registrationJournal.Preferences.Directory);
        var started = BeginRegistrationRecord(tenant, user, target, profile, "Office key");
        using (var lease = new FileStream(Path.Combine(_registrationJournal.Preferences.Directory, ".journal.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            CompleteRegistrationRecord(started, 0);
            Assert(RegistrationLogStatus.Text.Contains("succeeded") && RegistrationLogStatus.Text.Contains("not saved") && RetryRegistrationLog.Visibility == Visibility.Visible,
                "Successful enrollment with failed logging offers save-only recovery");
        }
        RetryRegistrationLog_Click(this, new RoutedEventArgs());
        Assert(_registrationJournal.PendingCount == 0 && _registrationJournal.Read().Single().Outcome == RegistrationOutcome.Succeeded && RetryRegistrationLog.Visibility == Visibility.Collapsed,
            "Retry saving completes the same record without reenrollment");
        var second = BeginRegistrationRecord(tenant, user with { DisplayName = "Robin Example", UserPrincipalName = "robin@example.test" }, target with { Serial = "00987654" }, profile with { Reset = false }, "Spare key");
        CompleteRegistrationRecord(second, 7);
        BeginRegistrationRecord(tenant, user with { DisplayName = "Sam Example", UserPrincipalName = "sam@example.test", Email = "" }, target with { Serial = "00456789" }, profile, "New key");
        settings.RegistrationHost.Content = new RegistrationLogView(_registrationJournal);
        var view = (RegistrationLogView)settings.RegistrationHost.Content;
        settings.SettingsTabs.SelectedIndex = 6;
        settings.UpdateLayout();
        Assert(view.EnabledChoice.IsChecked == true && view.Records.Items.Count == 3 && view.ExportButton.IsEnabled, "Registration log shows successful, interrupted and unknown attempts");
        view.SuccessOnly.IsChecked = true;
        Assert(view.Records.Items.Count == 1, "Registration export success filter matches the visible records");
        view.FromDate.SelectedDate = DateTime.Today.AddDays(1);
        view.ThroughDate.SelectedDate = DateTime.Today;
        Assert(view.Records.Items.Count == 0 && !view.ExportButton.IsEnabled, "Invalid date range cannot export unrelated records");
        view.FromDate.SelectedDate = null; view.ThroughDate.SelectedDate = null; view.SuccessOnly.IsChecked = false;
        view.Records.SelectedIndex = 2;
        CaptureWindow(settings, "settings-registration-log.png");
        Assert(!view.LoggingPreferences.IsExpanded, "Enabled logging keeps storage preferences collapsed so records stay prominent");
        view.LoggingPreferences.IsExpanded = true;
        CaptureWindow(settings, "registration-preferences.png");
        view.LoggingPreferences.IsExpanded = false;
        var width = settings.Width; var height = settings.Height;
        settings.Width = 740; settings.Height = 600;
        CaptureWindow(settings, "registration-log-compact.png");
        settings.Width = width; settings.Height = height;
        _registrationJournal.Configure(false, _registrationJournal.Preferences.Directory);
        Assert(_registrationJournal.Read().Count == 3, "Turning registration logging off preserves previous records");
    }
}
