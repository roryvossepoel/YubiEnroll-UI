using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private string[] BatchStepLabels => ["Tenant", "Profile", "Users", "Review", "Set up keys", "Results"];

    private enum BatchStage { Setup, Profile, Users, Review, Issue, Results }
    private BatchStage _batchStage;
    private EnrollmentBatch? _batch;
    private IReadOnlyList<UserCandidate> _verified = [];
    private ProfileSetup? _batchPolicy;
    private bool _batchLoading;
    private void Stage(BatchStage stage)
    {
        _batchStage = stage;
        BatchScroll.ScrollToTop();
        RenderBatch();
    }

    private async void BatchOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (RequirementsBlockTasks) { ShowPage(StartupPage); return; }
        if (_addPasskey) { _addPasskey = false; BeginNextKey(); }
        _batchLoading = true;
        try
        {
            BatchTenant.ItemsSource = _config?.Providers;
            BatchTenant.SelectedItem = _batch?.Tenant;
            BatchProfile.ItemsSource = _config?.Profiles;
            BatchProfile.SelectedItem = _batch?.Profile;
            if (_batch != null) Tenants.SelectedItem = _config?.Providers.FirstOrDefault(p => p == _batch.Tenant);
        }
        finally { _batchLoading = false; }
        if (_batch == null) { _verified = []; _batchStage = BatchStage.Setup; ReadBatchProfile(); }
        BatchKeys.SelectedItem = null;
        ShowPage(BatchPage);
        if (_batchStage == BatchStage.Issue && !_smokeTest && (!_inventoryLoaded || _inventoryDirty)) await RefreshOverviewAsync();
    }

    private void ReadBatchProfile()
    {
        _batchPolicy = null;
        if (BatchProfile.SelectedItem is not EnrollmentProfile profile) { BatchPolicy.Text = "Choose a profile for all keys in this batch."; return; }
        try
        {
            var setup = ProfileConfigWriter.Read(File.ReadAllText(_configPath), profile.Name).Validate();
            if (setup.Reset == null) { BatchPolicy.Text = "Choose an explicit FIDO reset setting in the profile."; return; }
            if (setup.RandomPin == null || setup.RequireAlwaysUv == null || setup.RequireEa == null || setup.ForcePinChange == null ||
                setup.RandomPin == true && setup.RandomPinLength == null)
            { BatchPolicy.Text = "Choose a profile with explicit PIN and FIDO settings."; return; }
            _batchPolicy = setup;
            BatchPolicy.Text = (setup.RandomPin == true ? $"Generate a {setup.RandomPinLength}-character initial PIN." : "Follow the native prompts for the initial PIN.") +
                (setup.ForcePinChange == true ? " The user must change it on first use." : "") + (setup.Reset == true ? " Confirm a FIDO reset for each key before setup." : " No FIDO reset.");
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex) || ex is InvalidDataException)
        { BatchPolicy.Text = "This profile could not be read. Check Settings → Profiles."; }
    }
    private void BatchTenant_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_batchLoading || _batch != null || BatchBack == null) return;
        Tenants.SelectedItem = BatchTenant.SelectedItem; _verified = []; RenderBatch();
    }
    private void BatchProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_batchLoading || _batch != null || BatchBack == null) return;
        ReadBatchProfile(); _verified = []; RenderBatch();
    }
    private bool BatchSignedIn => IsConnected && _connectedProvider == (_batch?.Tenant ?? BatchTenant.SelectedItem as Provider);
    private async void BatchConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var provider = _batch?.Tenant ?? BatchTenant.SelectedItem as Provider;
        if (provider == null || _config?.Providers.Contains(provider) != true) return;
        Tenants.SelectedItem = provider;
        await LoginAsync();
        RenderBatch();
    }
    private void BatchToProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && BatchSignedIn && _batchStage == BatchStage.Setup) Stage(BatchStage.Profile);
    }
    private void BatchToUsers_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && BatchSignedIn && _batchPolicy != null && _batchStage == BatchStage.Profile) Stage(BatchStage.Users);
    }
    private void BatchText_Changed(object sender, TextChangedEventArgs e)
    {
        _verified = [];
        if (BatchProblems != null) BatchProblems.Text = "";
        RenderBatch();
    }
    private void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "CSV file|*.csv|Text file|*.txt", Title = "Import batch users" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > BatchRoster.MaxCharacters * 4L) throw new ArgumentException("The selected file is too large.");
            BatchText.Text = string.Join(Environment.NewLine, BatchRoster.Import(File.ReadAllText(picker.FileName)));
            BatchProblems.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { BatchProblems.Text = ex is ArgumentException ? ex.Message : "Could not read the file. Check access and try again."; }
    }

    private async Task<IReadOnlyList<UserCandidate>> LookupBatchUserAsync(Provider provider, string upn)
    {
        void VerifyTenant()
        {
            var config = ConfigReader.Read(_configPath);
            ConfigReader.EnsureUnchanged(config, provider, null);
            if (!IsConnected || SelectedProvider != provider || config.ActiveProvider != provider.Name)
                throw new InvalidOperationException("The active tenant changed. Reconnect before checking this account.");
        }
        VerifyTenant();
        var result = await Runner.CaptureAsync("users", "--", upn);
        RequireSuccess(result, "Batch user lookup");
        VerifyTenant();
        return UserSearch.Parse(result.Output);
    }

    private async void BatchValidate_Click(object sender, RoutedEventArgs e) =>
        await ValidateBatchAsync(ActivateAsync, LookupBatchUserAsync);

    private async Task ValidateBatchAsync(Func<Provider, EnrollmentProfile, Task> activate, Func<Provider, string, Task<IReadOnlyList<UserCandidate>>> lookup)
    {
        if (_busy || !BatchSignedIn || _batchStage != BatchStage.Users || BatchProfile.SelectedItem is not EnrollmentProfile profile) return;
        await RunAsync(async () =>
        {
            _verified = [];
            string[] upns;
            try { upns = BatchRoster.Import(BatchText.Text); }
            catch (ArgumentException ex) { BatchProblems.Text = ex.Message; return; }
            var provider = SelectedProvider;
            await activate(provider, profile);
            var users = new List<UserCandidate>();
            var problems = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<Guid>();
            for (var i = 0; i < upns.Length; i++)
            {
                var upn = upns[i]; var prefix = $"Row {i + 1} · {upn}: ";
                if (!BatchRoster.ValidUpn(upn)) { problems.Add(prefix + "enter the full user principal name."); continue; }
                if (!names.Add(upn)) { problems.Add(prefix + "duplicate sign-in name."); continue; }
                BatchProblems.Text = $"Checking {i + 1} of {upns.Length}…";
                var user = BatchRoster.Exact(upn, await lookup(provider, upn));
                if (user == null) problems.Add(prefix + "no unique exact match in this tenant.");
                else if (!ids.Add(Guid.Parse(user.Id))) problems.Add(prefix + "this account already appears in the list.");
                else users.Add(user);
            }
            if (problems.Count != 0) { BatchProblems.Text = "Edit or remove these rows, then check again:\n" + string.Join("\n", problems); return; }
            BatchProblems.Text = "";
            _verified = users.ToArray(); BatchReviewUsers.ItemsSource = _verified;
            BatchReviewCount.Text = $"{users.Count} verified recipient(s). Starting the batch opens the first recipient; nothing is written until you register a key.";
            Stage(BatchStage.Review);
        }, "Check batch recipients");
    }
    private async void BatchStart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !BatchSignedIn || _batch != null || _batchStage != BatchStage.Review || _verified.Count == 0 ||
            BatchProfile.SelectedItem is not EnrollmentProfile profile || _batchPolicy == null) return;
        var started = await RunWithResultAsync(() =>
        {
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), SelectedProvider, profile);
            _batch = new(SelectedProvider, profile, _verified);
            Record($"Batch started · tenant {SelectedProvider.Name} · {_verified.Count} recipient(s).");
            _registrationResultMessage = ""; BatchKeys.SelectedItem = null;
            Stage(BatchStage.Issue);
            return Task.CompletedTask;
        }, "Start batch");
        if (started && !_smokeTest && (!_inventoryLoaded || _inventoryDirty)) await RefreshOverviewAsync();
    }

    private string? BatchKeyProblem(EnrollmentBatch batch, KeyStatus? candidate)
    {
        if (!BatchSignedIn) return "Reconnect to the reviewed tenant before registering a key.";
        if (_inventoryDirty) return "Checking connected keys…";
        if (batch.StillConnected(_knownKeys.Select(k => k.Key)) is { } previous)
            return $"Serial {previous} was already attempted in this batch. Remove it before issuing the next YubiKey.";
        if (candidate == null || !_knownKeys.Contains(candidate)) return "Connect a new YubiKey by USB or place it on your NFC reader, then select it.";
        if (!EnrollmentBatch.AllowsPin(_batchPolicy?.Reset, candidate.Pin)) return _batchPolicy?.Reset == false ? "This profile requires a key without a FIDO PIN." : "Cannot verify the FIDO PIN state on this key.";
        if (!EnrollmentTargetGuard.CanStart(candidate.Key, _knownKeys.Select(k => k.Key).ToArray(), _unavailableKeys))
            return EnrollmentTargetGuard.Explain(candidate.Key, _knownKeys.Select(k => k.Key).ToArray(), _unavailableKeys);
        if (_batchPolicy == null) return "Check the reviewed profile before proceeding.";
        var compatible = ProfileCompatibility.Evaluate(_batchPolicy, candidate.Capabilities, candidate.Pin);
        return compatible.Compatible ? null : string.Join(" ", compatible.Issues);
    }
    private void BatchKey_Changed(object sender, SelectionChangedEventArgs e) => RenderBatch();
    private async void BatchRegister_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch is not { Current: { Status: IssueStatus.Pending } row } batch ||
            BatchKeys.SelectedItem is not KeyStatus key || BatchKeyProblem(batch, key) != null) return;
        if (_batchPolicy?.Reset == true && MessageBox.Show(this,
            $"Erase all FIDO passkeys and the FIDO PIN on {key.Key.Model} (serial {key.Key.Serial}), then register it for {row.User.UserPrincipalName}?",
            "Reset and register YubiKey", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await ExecuteEnrollmentAsync(batch.Tenant, batch.Profile, row.User, key.Key, key.Key.RegistrationName, batch);
        }, "Register batch YubiKey");
        RenderBatch();
    }
    private void BatchSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch?.Current?.Status != IssueStatus.Pending) return;
        Record($"Batch recipient skipped · tenant {_batch.Tenant.Name} · user {_batch.Current.User.Id}.");
        _batch.Skip(); BatchRowChanged();
    }
    private void BatchNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch?.Current?.Status is not (IssueStatus.Registered or IssueStatus.CheckRequired)) return;
        Record($"Batch handoff reviewed · tenant {_batch.Tenant.Name} · user {_batch.Current.User.Id} · {_batch.Current.Status}.");
        _batch.Advance(); BatchRowChanged();
    }
    private void BatchRowChanged()
    {
        BatchKeys.SelectedItem = null; _registrationResultMessage = "";
        if (_batch?.Current == null) { Record("Batch finished."); Stage(BatchStage.Results); } else RenderBatch();
    }
    private void BatchFinish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch == null) return;
        if (MessageBox.Show(this, "Finish this batch now? Remaining users will be listed as Not started.", "End batch",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) { Record($"Batch ended early · tenant {_batch.Tenant.Name}."); Stage(BatchStage.Results); }
    }
    private void BatchRetryLog_Click(object sender, RoutedEventArgs e) { RetryRegistrationLog_Click(sender, e); RenderBatch(); }
    private void BatchBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_batchStage == BatchStage.Profile) Stage(BatchStage.Setup);
        else if (_batchStage == BatchStage.Users) Stage(BatchStage.Profile);
        else if (_batchStage == BatchStage.Review) Stage(BatchStage.Users);
        else ShowPage(OverviewPage);
    }
    private void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        if (_batch == null || _batchStage != BatchStage.Results) return;
        var dialog = new SaveFileDialog { Filter = "CSV file|*.csv", DefaultExt = ".csv", FileName = $"yubikey-batch-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, _batch.Csv(CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? ";" : ","), new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Could not save this CSV. Check the destination and try again."); }
    }
    private void BatchNew_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Results) return;
        if (MessageBox.Show(this, "Start a new batch and clear this results view? Export the CSV first if you need a copy.",
            "New batch", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _batch = null; _verified = []; BatchReviewUsers.ItemsSource = null; BatchRows.ItemsSource = null; BatchText.Clear();
        BatchOpen_Click(sender, e);
    }

    private void RenderBatch()
    {
        if (BatchFinish == null || BatchPage.Visibility != Visibility.Visible) return;
        var stage = _batchStage;
        RenderProgress(BatchProgress, BatchStepLabels, (int)stage);
        BatchTitle.Text = stage switch { BatchStage.Setup => "Connect to a tenant", BatchStage.Profile => "Choose a configuration profile", BatchStage.Users => "Add users", BatchStage.Review => "Review the batch", BatchStage.Issue => "Issue YubiKeys", _ => "Batch results" };
        BatchProfileStep.Visibility = BatchToUsers.Visibility = Visible(stage == BatchStage.Profile);
        BatchToProfile.Visibility = Visible(stage == BatchStage.Setup);
        BatchToProfile.IsEnabled = !_busy && BatchSignedIn;
        BatchCheck.Visibility = Visible(stage == BatchStage.Users);
        BatchStart.Visibility = Visible(stage == BatchStage.Review);
        BatchSetup.Visibility = Visible(stage == BatchStage.Setup); BatchInput.Visibility = Visible(stage == BatchStage.Users);
        BatchReview.Visibility = Visible(stage == BatchStage.Review); BatchIssue.Visibility = Visible(stage == BatchStage.Issue);
        BatchResults.Visibility = Visible(stage == BatchStage.Results);
        BatchContext.Text = stage == BatchStage.Setup ? "Choose the tenant for this batch and sign in." :
            stage == BatchStage.Profile ? "Choose one profile to apply to every YubiKey in this batch." :
            $"{_batch?.Tenant.Name ?? (BatchTenant.SelectedItem as Provider)?.Name}  ·  {_batch?.Profile.Name ?? (BatchProfile.SelectedItem as EnrollmentProfile)?.Name}";
        BatchConnection.Text = BatchSignedIn ? $"Connected to {_connectedProvider!.Name}" : "Sign in to verify the recipients.";
        BatchConnect.IsEnabled = !_busy && BatchTenant.SelectedItem != null && _cliReady;
        BatchConnect.Content = BatchSignedIn ? "Reconnect" : "Connect";
        BatchToUsers.IsEnabled = !_busy && BatchSignedIn && _batchPolicy != null;
        BatchCheck.IsEnabled = !_busy && BatchSignedIn && !string.IsNullOrWhiteSpace(BatchText.Text);
        SetBackAction(BatchBack, stage is BatchStage.Profile or BatchStage.Users or BatchStage.Review ? "Back" : null,
            stage == BatchStage.Issue ? "Pause and return to Home" : "Back to Home");
        BatchFinish.Visibility = Visible(stage == BatchStage.Issue);
        BatchResumeConnect.Visibility = Visible(stage == BatchStage.Issue && !BatchSignedIn);
        if (_batch is not { } batch) return;
        if (stage == BatchStage.Results)
        {
            BatchRows.ItemsSource = batch.Rows;
            BatchTotals.Text = string.Join("  ·  ", batch.Rows.GroupBy(r => r.StatusLabel).Select(g => $"{g.Count()} {g.Key}"));
            return;
        }
        if (batch.Current is not { } row || stage != BatchStage.Issue) return;
        BatchNumber.Text = $"Recipient {batch.Position + 1} of {batch.Rows.Count}";
        BatchRecipient.Text = string.IsNullOrWhiteSpace(row.User.DisplayName) ? row.User.UserPrincipalName : row.User.DisplayName;
        BatchUpn.Text = row.User.UserPrincipalName;
        var pending = row.Status == IssueStatus.Pending;
        BatchChoose.Visibility = Visible(pending); BatchHandoff.Visibility = Visible(!pending);
        BatchLogNotice.Text = _registrationResultMessage;
        BatchRetryLog.Visibility = Visible(_registrationJournal.PendingCount > 0);
        if (pending)
        {
            var key = BatchKeys.SelectedItem as KeyStatus;
            var problem = BatchKeyProblem(batch, key);
            BatchKeyHint.Text = problem ?? $"Register serial {key!.Key.Serial} for {row.User.UserPrincipalName}. Keep this key in place until the command finishes.";
            BatchKeyHint.Visibility = Visible(_knownKeys.Count > 0);
            BatchRegister.IsEnabled = !_busy && problem == null;
        }
        else
        {
            BatchOutcome.Text = $"{row.StatusLabel} · Serial {row.Serial} · {row.User.UserPrincipalName}";
            BatchInstructions.Text = row.Status == IssueStatus.Registered ? "Label and hand over this YubiKey and its initial PIN before moving on." :
                "Check the registration in Entra before attempting another enrollment. This serial cannot be reused in this batch.";
            BatchNext.Content = batch.Position + 1 == batch.Rows.Count ? "View results" : "Next user";
        }
    }
}
