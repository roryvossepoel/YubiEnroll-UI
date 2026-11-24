using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private async Task TestBatchPageAsync()
    {
        var e = new RoutedEventArgs();
        _addPasskey = false; _enrollStep = 0;
        ShowPage(PreparePage);
        BatchOpen_Click(this, e);
        UpdateLayout();
        Assert(BatchTenant.SelectedItem == null && !BatchConnect.IsEnabled && !BatchToProfile.IsEnabled, "New batch requires explicit tenant selection");
        BatchTenant.SelectedItem = _config!.Providers.Single();
        _connectedProvider = SelectedProvider; UpdateSummary(); BatchToProfile_Click(this,e);
        UpdateLayout();
        Assert(_batchStage == BatchStage.Profile && BatchProfileStep.IsVisible, $"Profile is a separate step after tenant connection (stage={_batchStage}, page={BatchPage.IsVisible}, profile={BatchProfileStep.Visibility}, signedIn={BatchSignedIn}, busy={_busy})");
        BatchProfile.SelectedValue = _config!.Profiles.Single(p => p.Name == "standard");
        Assert(BatchToUsers.IsEnabled && BatchProfile.SelectedItem is ProfileChoice { Pills.Count: > 0, Compatible: true }, "Batch setup accepts the test profile with FIDO reset before selecting a key");
        BatchProfile.SelectedValue = _config.Profiles.Single(p => p.Name == "Second profile");
        _connectedProvider = SelectedProvider;
        UpdateSummary(); BatchToUsers_Click(this, e);
        var a = new UserCandidate("aaaaaaaa-1111-2222-3333-444444444444", "") { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test" };
        var b = new UserCandidate("bbbbbbbb-1111-2222-3333-444444444444", "") { DisplayName = "Robin Example", UserPrincipalName = "robin@example.test" };
        Task<IReadOnlyList<UserCandidate>> Lookup(Provider _, string upn) => Task.FromResult<IReadOnlyList<UserCandidate>>([a, b]);
        BatchText.Text = a.UserPrincipalName + "\n" + a.UserPrincipalName;
        await ValidateBatchAsync((_, _) => Task.CompletedTask, Lookup);
        Assert(_batchStage == BatchStage.Users && BatchProblems.Text.Contains("duplicate"), "Duplicate input blocks review until corrected");
        BatchText.Text = a.UserPrincipalName + "\n" + b.UserPrincipalName;
        await ValidateBatchAsync((_, _) => Task.CompletedTask, Lookup);
        Assert(_batchStage == BatchStage.Review && BatchReviewUsers.Items.Count == 2, "Batch review lists exactly the two verified people");
        BatchStart_Click(this, e);
        var batch = _batch ?? throw new Exception("Batch did not start");
        var key = new ResetKey("12345678", "YubiKey 5C NFC (5.7.1)");
        var status = new KeyStatus(key, FidoPinState.NotSet, new FidoCapabilities(key.Serial, "5.7.1", true, true, true, true, false, false, 4, 63, false, true, false));
        ApplyInventory([status], 0);
        BatchKeys.SelectedValue = status; UpdateLayout();
        Assert(BatchRegister.IsEnabled && BatchKeyHint.Text.Contains(a.UserPrincipalName) && BatchKeyHint.Text.Contains(key.Serial),
            "Issuance binds the visible serial and UPN before the operator can register");
        var incompatible = status with { Key = key with { Serial = "321" }, Capabilities = null };
        ApplyInventory([incompatible], 0);
        Assert(BatchKeys.Items.OfType<BatchKeyChoice>().Single() is { Eligible: false, Problem.Length: > 0 } &&
            !BatchRegister.IsEnabled, "An incompatible key explains the profile problem before selection");
        ApplyInventory([status], 0);
        BatchKeys.SelectedValue = status;
        CaptureWindow(this, "batch-issue.png");
        // Exercise the real queue, handoff and device-change handlers without a native write or tenant.
        var preferences = _registrationJournal.Preferences;
        _registrationJournal.Configure(true, preferences.Directory, false, preferences.RetentionDays);
        batch.Start(a, key, status.Pin, _batchPolicy! with { RandomPin = true });
        var attempt = BeginRegistrationRecord(batch.Tenant, a, key, _batchPolicy!, key.RegistrationName)!;
        batch.Complete(0, "004815");
        Assert(CompleteRegistrationRecord(attempt, 0) && _registrationJournal.Read()
            .Single(record => record.AttemptId == attempt.Record.AttemptId).GeneratedPin == null,
            "The journal omits the initial PIN when PIN logging is off");
        RenderBatch();
        Assert(BatchHandoff.IsVisible && !BatchRegister.IsVisible && BatchOutcome.Text.Contains(key.Serial), "Handoff stays on the completed recipient");
        BatchNext_Click(this, e); BatchKeys.SelectedValue = status;
        Assert(!BatchRegister.IsEnabled && BatchRecipient.Text == b.DisplayName && BatchKeyHint.Text.Contains("already attempted"),
            "Leaving the first key on the reader cannot register it for the next person");
        var second = status with { Key = key with { Serial = "98765432", Reader = "Test NFC 0" }, Capabilities = status.Capabilities! with { Serial = "98765432", IsNfc = true } };
        ApplyInventory([status, second], 0); BatchKeys.SelectedValue = second;
        Assert(!BatchRegister.IsEnabled, "An issued USB key still connected blocks the next NFC target");
        ApplyInventory([second], 0); BatchKeys.SelectedValue = second;
        Assert(BatchRegister.IsEnabled, "Removing the issued key unlocks the new serial for the next recipient");
        BatchSkip_Click(this, e);
        Assert(_batchStage == BatchStage.Results && BatchRows.Items.Count == 2 && BatchTotals.Text.Contains("Skipped"),
            "Results distinguish an issued YubiKey from a skipped user");
        UpdateLayout();
        var eye = VisualDescendants<ToggleButton>(BatchRows).Single(button => button.Name == "Reveal" && button.IsVisible);
        var value = VisualDescendants<TextBlock>(BatchRows).Single(text => text.Name == "PinValue" && text.IsVisible);
        Assert(value.Text == "••••••" && ReferenceEquals(eye.DataContext, batch.Rows[0]), "The verified session PIN is masked in batch results with PIN logging off");
        eye.IsChecked = true;
        UpdateLayout();
        Assert(value.Text == "004815" && BatchResultPinNotice.IsVisible,
            "The PIN remains available for handoff with a notice to record or export it before leaving");
        bool? ChooseExport(bool keepPins)
        {
            bool wasChecked = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = OwnedWindows.Cast<Window>().Single(window => window.Title == "Export batch results");
                var choice = VisualDescendants<CheckBox>(dialog).Single();
                wasChecked = choice.IsChecked == true;
                choice.IsChecked = keepPins;
                dialog.DialogResult = true;
            }));
            var include = ChooseBatchPinExport();
            Assert(wasChecked, "Each export dialog starts with Include initial PINs checked");
            return include;
        }
        Assert(ChooseExport(false) == false && !batch.Csv(";", false).Contains("004815") &&
            ChooseExport(true) == true && batch.Csv(";", true).Contains("004815"),
            "The operator can export batch results with or without PINs while journal PIN logging is off");
        eye.IsChecked = false;
        CaptureWindow(this, "batch-results.png");
        BatchOpen_Click(this, e);
        Assert(batch.Rows.All(row => row.GeneratedPin == null), "Navigation discards all PINs in the previous batch");
        Assert(_batch == null && _batchStage == BatchStage.Setup && BatchTenant.SelectedIndex == -1 && BatchText.Text == "",
            "Starting from the menu clears the completed batch");
        _registrationJournal.Configure(preferences.Enabled, preferences.Directory, preferences.SaveGeneratedPin, preferences.RetentionDays);
        _connectedProvider = null;
        BatchRows.ItemsSource = null; BatchReviewUsers.ItemsSource = null; BatchText.Clear();
        ShowPage(OverviewPage); BeginNextKey();
    }
}
