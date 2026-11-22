using System.Windows;
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
        Assert(BatchTenant.SelectedItem == null && !BatchConnect.IsEnabled && !BatchToProfile.IsEnabled, "New batch requires explicit tenant selection");
        BatchTenant.SelectedItem = Tenants.SelectedItem;
        _connectedProvider = SelectedProvider; UpdateSummary(); BatchToProfile_Click(this,e);
        Assert(_batchStage == BatchStage.Profile && BatchProfileStep.IsVisible, "Profile is a separate step after tenant connection");
        BatchProfile.SelectedItem = _config!.Profiles.Single(p => p.Name == "standard");
        Assert(BatchToUsers.IsEnabled && BatchPolicy.Text.Contains("reset", StringComparison.OrdinalIgnoreCase), "Batch setup accepts the test profile with FIDO reset before selecting a key");
        BatchProfile.SelectedItem = _config.Profiles.Single(p => p.Name == "Second profile");
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
        BatchKeys.SelectedItem = status; UpdateLayout();
        Assert(BatchRegister.IsEnabled && BatchKeyHint.Text.Contains(a.UserPrincipalName) && BatchKeyHint.Text.Contains(key.Serial),
            "Issuance binds the visible serial and UPN before the operator can register");
        CaptureWindow(this, "batch-issue.png");
        // Exercise the real queue, handoff and device-change handlers without a native write or tenant.
        batch.Start(a, key, status.Pin, _batchPolicy!); batch.Complete(0); RenderBatch();
        Assert(BatchHandoff.IsVisible && !BatchRegister.IsVisible && BatchOutcome.Text.Contains(key.Serial), "Handoff stays on the completed recipient");
        BatchNext_Click(this, e); BatchKeys.SelectedItem = status;
        Assert(!BatchRegister.IsEnabled && BatchRecipient.Text == b.DisplayName && BatchKeyHint.Text.Contains("already attempted"),
            "Leaving the first key on the reader cannot register it for the next person");
        var second = status with { Key = key with { Serial = "98765432", Reader = "Test NFC 0" }, Capabilities = status.Capabilities! with { Serial = "98765432", IsNfc = true } };
        ApplyInventory([status, second], 0); BatchKeys.SelectedItem = second;
        Assert(!BatchRegister.IsEnabled, "An issued USB key still connected blocks the next NFC target");
        ApplyInventory([second], 0); BatchKeys.SelectedItem = second;
        Assert(BatchRegister.IsEnabled, "Removing the issued key unlocks the new serial for the next recipient");
        BatchSkip_Click(this, e);
        Assert(_batchStage == BatchStage.Results && BatchRows.Items.Count == 2 && BatchTotals.Text.Contains("Skipped"),
            "Results distinguish an issued YubiKey from a skipped user");
        CaptureWindow(this, "batch-results.png");
        _batch = null; _verified = []; _batchStage = BatchStage.Setup; _connectedProvider = null;
        BatchRows.ItemsSource = null; BatchReviewUsers.ItemsSource = null; BatchText.Clear();
        ShowPage(OverviewPage); BeginNextKey();
    }
}
