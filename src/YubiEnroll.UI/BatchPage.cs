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
    private enum BatchStage { Setup, Users, Review, Issue, Results }
    private BatchStage _batchStage;
    private EnrollmentBatch? _batch;
    private IReadOnlyList<UserCandidate> _verified = [];
    private ProfileSetup? _batchPolicy;
    private bool _batchLoading;
    private void Stage(BatchStage stage)
    {
        _batchStage = stage;
        BatchPage.ScrollToTop();
        RenderBatch();
    }

    private async void BatchOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _addPasskey || RequirementsBlockTasks) return;
        _batchLoading = true;
        try
        {
            BatchTenant.ItemsSource = _config?.Providers;
            BatchTenant.SelectedItem = _batch?.Tenant ?? Tenants.SelectedItem;
            BatchProfile.ItemsSource = _config?.Profiles;
            BatchProfile.SelectedItem = _batch?.Profile ?? (Profiles.SelectedItem as ProfileChoice)?.Profile;
            if (_batch != null) Tenants.SelectedItem = _config?.Providers.FirstOrDefault(p => p == _batch.Tenant);
        }
        finally { _batchLoading = false; }
        if (_batch == null) { _verified = []; _batchStage = BatchStage.Setup; ReadBatchProfile(); }
        BatchKeys.SelectedItem = null;
        ShowPage(BatchPage);
        if (_batchStage == BatchStage.Issue && !_smokeTest) await RefreshOverviewAsync();
    }

    private void ReadBatchProfile()
    {
        _batchPolicy = null;
        if (BatchProfile.SelectedItem is not EnrollmentProfile profile) { BatchPolicy.Text = "Choose a profile for all keys in this batch."; return; }
        try
        {
            var setup = ProfileConfigWriter.Read(File.ReadAllText(_configPath), profile.Name).Validate();
            if (setup.Reset != false) { BatchPolicy.Text = "Choose a profile with Reset FIDO before setup turned off. Batch issuance never resets keys."; return; }
            if (setup.RandomPin == null || setup.RequireAlwaysUv == null || setup.RequireEa == null || setup.ForcePinChange == null ||
                setup.RandomPin == true && setup.RandomPinLength == null)
            { BatchPolicy.Text = "Choose a profile with explicit PIN and FIDO settings."; return; }
            _batchPolicy = setup;
            BatchPolicy.Text = (setup.RandomPin == true ? $"Generate a {setup.RandomPinLength}-character initial PIN." : "Follow the native prompts for the initial PIN.") +
                (setup.ForcePinChange == true ? " The user must change it on first use." : "") + " No FIDO reset.";
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
    private void BatchToUsers_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && BatchSignedIn && _batchPolicy != null && _batchStage == BatchStage.Setup) Stage(BatchStage.Users);
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
            BatchProfile.SelectedItem is not EnrollmentProfile profile || _batchPolicy?.Reset != false) return;
        var started = await RunWithResultAsync(() =>
        {
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), SelectedProvider, profile);
            _batch = new(SelectedProvider, profile, _verified);
            _registrationResultMessage = ""; BatchKeys.SelectedItem = null;
            Stage(BatchStage.Issue);
            return Task.CompletedTask;
        }, "Start batch");
        if (started && !_smokeTest) await RefreshOverviewAsync();
    }

    private string? BatchKeyProblem(EnrollmentBatch batch, KeyStatus? candidate)
    {
        if (!BatchSignedIn) return "Reconnect to the reviewed tenant before registering a key.";
        if (_inventoryDirty) return "Checking connected keys…";
        if (candidate == null || !_knownKeys.Contains(candidate)) return "Connect a new YubiKey by USB or place it on your NFC reader, then select it.";
        if (batch.Used(candidate.Key.Serial)) return "This serial number was already attempted in this batch. Remove it and select a different YubiKey.";
        if (candidate.Pin != FidoPinState.NotSet) return "Only a YubiKey verified to have no FIDO PIN can be issued here.";
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
        await RunAsync(async () =>
        {
            await ExecuteEnrollmentAsync(batch.Tenant, batch.Profile, row.User, key.Key, key.Key.RegistrationName, batch);
        }, "Register batch YubiKey");
        RenderBatch();
    }
    private void BatchSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch?.Current?.Status != IssueStatus.Pending) return;
        _batch.Skip(); BatchRowChanged();
    }
    private void BatchNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch?.Current?.Status is not (IssueStatus.Registered or IssueStatus.CheckRequired)) return;
        _batch.Advance(); BatchRowChanged();
    }
    private void BatchRowChanged()
    {
        BatchKeys.SelectedItem = null; _registrationResultMessage = "";
        if (_batch?.Current == null) Stage(BatchStage.Results); else RenderBatch();
    }
    private void BatchFinish_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch == null) return;
        if (MessageBox.Show(this, "Finish this batch now? Remaining users will be listed as Not started.", "End batch",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) Stage(BatchStage.Results);
    }
    private void BatchRetryLog_Click(object sender, RoutedEventArgs e) { RetryRegistrationLog_Click(sender, e); RenderBatch(); }
    private void BatchBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_batchStage == BatchStage.Users) Stage(BatchStage.Setup);
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
        RenderProgress(BatchProgress, ["Setup", "Users", "Review", "Issue", "Results"], (int)stage);
        BatchSetup.Visibility = Visible(stage == BatchStage.Setup); BatchInput.Visibility = Visible(stage == BatchStage.Users);
        BatchReview.Visibility = Visible(stage == BatchStage.Review); BatchIssue.Visibility = Visible(stage == BatchStage.Issue);
        BatchResults.Visibility = Visible(stage == BatchStage.Results);
        BatchContext.Text = stage == BatchStage.Setup ? "Choose one tenant and profile. Each recipient gets one new YubiKey." :
            $"{_batch?.Tenant.Name ?? (BatchTenant.SelectedItem as Provider)?.Name}  ·  {_batch?.Profile.Name ?? (BatchProfile.SelectedItem as EnrollmentProfile)?.Name}";
        BatchConnection.Text = BatchSignedIn ? $"Connected to {_connectedProvider!.Name}" : "Sign in to verify the recipients.";
        BatchConnect.IsEnabled = !_busy && BatchTenant.SelectedItem != null && _cliReady;
        BatchConnect.Content = BatchSignedIn ? "Reconnect" : "Connect";
        BatchToUsers.IsEnabled = !_busy && BatchSignedIn && _batchPolicy != null;
        BatchCheck.IsEnabled = !_busy && BatchSignedIn && !string.IsNullOrWhiteSpace(BatchText.Text);
        BatchBack.Content = stage == BatchStage.Issue ? "Pause and return to Home" :
            stage is BatchStage.Setup or BatchStage.Results ? "Back to Home" : "←  Back";
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
