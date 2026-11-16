using System.Windows;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private readonly RegistrationJournal _registrationJournal;
    private string _registrationResultMessage = "";

    private RegistrationAttempt? BeginRegistrationRecord(Provider tenant, UserCandidate user, ResetKey target, ProfileSetup profile, string name)
    {
        if (!_registrationJournal.Preferences.Enabled && _registrationJournal.SettingsError == null) return null;
        try { return _registrationJournal.Begin(RegistrationRecord.Start(tenant, user, target, profile, name)); }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex))
        {
            throw new InvalidOperationException("Enrollment has not started: the registration log could not be initialized. Check Settings → Registration log, restore folder access or explicitly turn logging off.");
        }
    }

    private void CompleteRegistrationRecord(RegistrationAttempt? attempt, int? code, string? generatedPin = null)
    {
        var saved = _registrationJournal.Complete(attempt, code, generatedPin);
        _registrationResultMessage = attempt == null ? "Registration logging was off for this enrollment." : saved ? "Enrollment record saved." :
            code == 0 ? "Enrollment succeeded according to YubiEnroll, but its registration record was not saved. Keep the app open and retry saving." :
            "The enrollment outcome could not be saved. Keep the app open and retry saving; do not repeat enrollment without checking Entra.";
        if (saved && attempt?.SaveGeneratedPin == true && !_addPasskey)
            _registrationResultMessage += generatedPin != null ? " Generated initial PIN saved." : " No generated PIN was reported; the PIN field is empty.";
        RenderRegistrationLogStatus();
    }

    private void RenderRegistrationLogStatus()
    {
        if (RegistrationLogStatus == null) return;
        RegistrationLogStatus.Text = _registrationResultMessage + (_registrationJournal.PendingCount > 0 ? $"\n{_registrationJournal.PendingCount} result(s) still need to be saved." : "");
        RetryRegistrationLog.Visibility = Visible(_registrationJournal.PendingCount > 0);
        RegistrationReviewNote.Text = _registrationJournal.SettingsError ?? (_registrationJournal.Preferences.Enabled
            ? (_registrationJournal.Preferences.SaveGeneratedPin && !_addPasskey ? "Registration logging enabled · generated PIN included when available." : "Registration logging enabled.")
            : "Registration logging disabled.");
    }

    private void RetryRegistrationLog_Click(object sender, RoutedEventArgs e)
    {
        _registrationJournal.RetryPending();
        if (_registrationJournal.PendingCount == 0) _registrationResultMessage = "All pending enrollment records have been saved. Enrollment was not repeated.";
        RenderRegistrationLogStatus();
    }
}
