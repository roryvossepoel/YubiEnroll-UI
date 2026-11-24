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
    private const string PinNotSavedWarning = "Temporary PINs are not saved. Record each PIN securely before continuing.";
    private bool InitialPinLogging => _registrationJournal.Preferences.Enabled && _registrationJournal.Preferences.SaveGeneratedPin;
    private sealed record BatchKeyChoice(KeyStatus Device, string? Problem)
    {
        public bool Eligible => Problem == null;
    }
    private sealed record BatchReviewRow(int Number, UserCandidate User);
    private void Stage(BatchStage stage)
    {
        _batchStage = stage;
        BatchScroll.ScrollToTop();
        RenderBatch();
    }

    private void BatchOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (RequirementsBlockTasks) { ShowPage(StartupPage); return; }
        if (_addPasskey) { _addPasskey = false; BeginNextKey(); }
        ResetBatch();
        _batchLoading = true;
        try
        {
            BatchTenant.ItemsSource = _config?.Providers;
            BatchTenant.SelectedIndex = -1;
            BatchProfile.ItemsSource = _config?.Profiles.Select(BatchProfileCard).ToArray();
            BatchProfile.SelectedIndex = -1;
        }
        finally { _batchLoading = false; }
        ReadBatchProfile();
        BatchKeys.SelectedItem = null;
        ShowPage(BatchPage);
    }

    private ProfileChoice BatchProfileCard(EnrollmentProfile profile)
    {
        var card = new ProfileChoice(profile);
        try
        {
            var setup = DeskPolicy.ReadProfile(File.ReadAllText(_configPath), profile.Name, _policy).Validate();
            var issues = new List<string>();
            if (setup.Reset == null) issues.Add("Choose an explicit FIDO reset setting in Settings → Profiles.");
            if (setup.RandomPin == null || setup.RequireAlwaysUv == null || setup.RequireEa == null || setup.ForcePinChange == null ||
                setup.RandomPin == true && setup.RandomPinLength == null)
                issues.Add("Choose explicit PIN and FIDO settings in Settings → Profiles.");
            card.Set(new(issues), setup);
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex) || ex is InvalidDataException or ArgumentException)
        {
            card.Set(new(["This profile could not be read. Check Settings → Profiles."]), null);
        }
        return card;
    }

    private void ReadBatchProfile()
    {
        var card = BatchProfile.SelectedItem as ProfileChoice;
        _batchPolicy = card is { Compatible: true } ? card.Setup : null;
        BatchPolicy.Text = _batchPolicy?.RandomPin == true && !InitialPinLogging ? PinNotSavedWarning : "";
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
        _invalidBatchLines.Clear(); _gutterFirst = -1;
        if (BatchProblems != null) BatchProblems.Text = "";
        RenderBatch();
    }
    private readonly HashSet<int> _invalidBatchLines = [];
    private int _gutterFirst = -1, _gutterLast = -1;
    private double _gutterTop = double.NaN;

    private void BatchEditor_LayoutUpdated(object? sender, EventArgs e)
    {
        if (BatchText?.IsVisible != true || !BatchText.IsArrangeValid || BatchText.LineCount < 1) return;
        var first = BatchText.GetFirstVisibleLineIndex();
        var last = BatchText.GetLastVisibleLineIndex();
        if (first < 0 || last < first) return;
        var top = BatchText.GetRectFromCharacterIndex(BatchText.GetCharacterIndexFromLineIndex(first)).Top;
        if (!double.IsFinite(top) || (first == _gutterFirst && last == _gutterLast && top == _gutterTop)) return;
        (_gutterFirst, _gutterLast, _gutterTop) = (first, last, top);
        BatchLineNumbers.Children.Clear();
        for (var line = first; line <= last; line++)
        {
            var rect = BatchText.GetRectFromCharacterIndex(BatchText.GetCharacterIndexFromLineIndex(line));
            if (rect.IsEmpty) continue;
            var number = new TextBlock
            {
                Text = (line + 1).ToString(CultureInfo.CurrentCulture),
                Foreground = _invalidBatchLines.Contains(line + 1)
                    ? System.Windows.Media.Brushes.SaddleBrown : (System.Windows.Media.Brush)FindResource("Muted")
            };
            Canvas.SetTop(number, rect.Top);
            Canvas.SetRight(number, 8);
            BatchLineNumbers.Children.Add(number);
        }
    }

    private void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "CSV file|*.csv|Text file|*.txt", Title = "Import batch users" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > BatchRoster.MaxCharacters * 4L) throw new ArgumentException("The selected file is too large.");
            var imported = string.Join(Environment.NewLine, BatchRoster.Import(File.ReadAllText(picker.FileName)));
            var prior = BatchText.Text.TrimEnd('\r', '\n');
            var combined = prior.Length == 0 ? imported : prior + Environment.NewLine + imported;
            if (combined.Length > BatchRoster.MaxCharacters) throw new ArgumentException("The combined user list is too large.");
            BatchText.Text = combined;
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
        if (_busy || !BatchSignedIn || _batchStage != BatchStage.Users || BatchProfile.SelectedValue is not EnrollmentProfile profile) return;
        await RunAsync(async () =>
        {
            _verified = [];
            var sourceLines = new List<int>();
            string[] upns;
            try { upns = BatchRoster.Import(BatchText.Text, sourceLines); }
            catch (ArgumentException ex) { BatchProblems.Text = ex.Message; return; }
            var provider = SelectedProvider;
            await activate(provider, profile);
            var users = new List<UserCandidate>();
            var problems = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<Guid>();
            for (var i = 0; i < upns.Length; i++)
            {
                var upn = upns[i];
                void Problem(string reason)
                {
                    _invalidBatchLines.Add(sourceLines[i]);
                    problems.Add($"Row {sourceLines[i]} · {upn}: {reason}");
                    _gutterFirst = -1;
                }
                if (!BatchRoster.ValidUpn(upn)) { Problem("enter the full user principal name."); continue; }
                if (!names.Add(upn)) { Problem("duplicate sign-in name."); continue; }
                BatchProblems.Text = $"Checking {i + 1} of {upns.Length}…";
                var user = BatchRoster.Exact(upn, await lookup(provider, upn));
                if (user == null) Problem("no unique exact match in this tenant.");
                else if (!ids.Add(Guid.Parse(user.Id))) Problem("this account already appears in the list.");
                else users.Add(user);
            }
            if (problems.Count != 0) { BatchProblems.Text = "Edit or remove these rows, then check again:\n" + string.Join("\n", problems); return; }
            BatchProblems.Text = "";
            _verified = users.ToArray(); BatchReviewUsers.ItemsSource = users.Select((user, index) => new BatchReviewRow(index + 1, user)).ToArray();
            BatchReviewCount.Text = $"{users.Count} verified recipient(s). Starting the batch opens the first recipient.";
            Stage(BatchStage.Review);
        }, "Check batch recipients");
    }
    private async void BatchStart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !BatchSignedIn || _batch != null || _batchStage != BatchStage.Review || _verified.Count == 0 ||
            BatchProfile.SelectedValue is not EnrollmentProfile profile || _batchPolicy == null) return;
        var started = await RunWithResultAsync(() =>
        {
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), SelectedProvider, profile);
            _batch = new(SelectedProvider, profile, _verified);
            Record($"Batch started · tenant {SelectedProvider.Name} · {_verified.Count} recipient(s).");
            _registrationResultMessage = ""; BatchKeys.SelectedItem = null;
            UpdateBatchKeyCards();
            Stage(BatchStage.Issue);
            return Task.CompletedTask;
        }, "Start batch");
        if (started && !_smokeTest && (!_inventoryLoaded || _inventoryDirty)) await RefreshOverviewAsync();
    }

    private string? BatchProfileProblem(KeyStatus device)
    {
        if (_batchPolicy == null) return null;
        if (!EnrollmentBatch.AllowsPin(_batchPolicy.Reset, device.Pin))
            return _batchPolicy.Reset == false ? "This profile requires a key without a FIDO PIN." : "Cannot verify the FIDO PIN state on this key.";
        var result = ProfileCompatibility.Evaluate(_batchPolicy, device.Capabilities, device.Pin);
        return result.Compatible ? null : string.Join(" ", result.Issues);
    }

    private void UpdateBatchKeyCards()
    {
        var selected = (BatchKeys.SelectedValue as KeyStatus)?.Key;
        var cards = _knownKeys.Select(device => new BatchKeyChoice(device, BatchProfileProblem(device))).ToArray();
        BatchKeys.ItemsSource = cards;
        BatchKeys.SelectedItem = cards.FirstOrDefault(c => c.Eligible && c.Device.Key == selected);
    }

    private string? BatchKeyProblem(EnrollmentBatch batch, KeyStatus? candidate)
    {
        if (!BatchSignedIn) return "Reconnect to the reviewed tenant before registering a key.";
        if (_inventoryDirty) return "Checking connected keys…";
        if (batch.StillConnected(_knownKeys.Select(k => k.Key)) is { } previous)
            return $"Serial {previous} was already attempted in this batch. Remove it before issuing the next YubiKey.";
        if (candidate == null || !_knownKeys.Contains(candidate)) return "Connect a new YubiKey by USB or place it on your NFC reader, then select it.";
        if (!EnrollmentTargetGuard.CanStart(candidate.Key, _knownKeys.Select(k => k.Key).ToArray(), _unavailableKeys))
            return EnrollmentTargetGuard.Explain(candidate.Key, _knownKeys.Select(k => k.Key).ToArray(), _unavailableKeys);
        if (_batchPolicy == null) return "Check the reviewed profile before proceeding.";
        return BatchProfileProblem(candidate);
    }
    private void BatchKey_Changed(object sender, SelectionChangedEventArgs e) => RenderBatch();
    private async void BatchRegister_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _batchStage != BatchStage.Issue || _batch is not { Current: { Status: IssueStatus.Pending } row } batch ||
            BatchKeys.SelectedValue is not KeyStatus key || BatchKeyProblem(batch, key) != null) return;
        if (_batchPolicy?.Reset == true && !ConfirmBatchReset(key.Key, row.User)) return;
        await RunAsync(async () =>
        {
            await ExecuteEnrollmentAsync(batch.Tenant, batch.Profile, row.User, key.Key, key.Key.RegistrationName, batch);
        }, "Register batch YubiKey");
        StatusText.Text = "";
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
        BatchKeys.SelectedItem = null; _registrationResultMessage = ""; StatusText.Text = "";
        if (_batch?.Current == null) { Record("Batch finished."); Stage(BatchStage.Results); } else RenderBatch();
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
    private void ResetBatch()
    {
        if (_batch is { Current: not null } batch && _batchStage == BatchStage.Issue)
        {
            var finished = batch.Rows.All(row => row.Status is IssueStatus.Registered or IssueStatus.Skipped or IssueStatus.CheckRequired);
            Record(finished ? "Batch finished." : "Batch cancelled.");
        }
        _batch = null;
        _batchStage = BatchStage.Setup;
        _batchPolicy = null;
        _verified = [];
        BatchRows.ItemsSource = BatchReviewUsers.ItemsSource = null;
        BatchText.Clear();
        BatchKeys.SelectedIndex = -1;
        _registrationResultMessage = "";
    }

    private void RenderBatch()
    {
        if (BatchExport == null || BatchPage.Visibility != Visibility.Visible) return;
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
            $"{_batch?.Tenant.Name ?? (BatchTenant.SelectedItem as Provider)?.Name}  ·  {_batch?.Profile.Name ?? (BatchProfile.SelectedValue as EnrollmentProfile)?.Name}";
        BatchConnection.Text = BatchSignedIn ? $"Connected to {_connectedProvider!.Name}" : "Sign in to verify the recipients.";
        BatchConnect.IsEnabled = !_busy && BatchTenant.SelectedItem != null && _cliReady;
        BatchConnect.Content = BatchSignedIn ? "Reconnect" : "Connect";
        BatchToUsers.IsEnabled = !_busy && BatchSignedIn && _batchPolicy != null;
        BatchCheck.IsEnabled = !_busy && BatchSignedIn && !string.IsNullOrWhiteSpace(BatchText.Text);
        var issuing = stage == BatchStage.Issue;
        var pendingRecipient = issuing && _batch?.Current?.Status == IssueStatus.Pending;
        var handoff = issuing && _batch?.Current?.Status is IssueStatus.Registered or IssueStatus.CheckRequired;
        var lastHandoff = handoff && _batch!.Position + 1 == _batch.Rows.Count;
        BatchSkip.Visibility = BatchRegister.Visibility = Visible(pendingRecipient);
        BatchNext.Visibility = Visible(handoff);
        BatchBack.Visibility = Visible(!lastHandoff);
        BatchExport.Visibility = Visible(stage == BatchStage.Results);
        BatchPinWarning.Text = _batchPolicy?.RandomPin == true && !InitialPinLogging ? PinNotSavedWarning : "";
        BatchPinWarning.Visibility = Visible(BatchPinWarning.Text.Length != 0);
        SetBackAction(BatchBack, stage is BatchStage.Profile or BatchStage.Users or BatchStage.Review ? "Back" : null,
            issuing ? "Cancel batch" : "Back to Home");
        BatchResumeConnect.Visibility = Visible(stage == BatchStage.Issue && !BatchSignedIn);
        if (_batch is not { } batch) return;
        if (stage == BatchStage.Results)
        {
            BatchRows.ItemsSource = batch.Rows;
            BatchTotals.Text = $"Batch complete · {batch.Rows.Count} of {batch.Rows.Count} recipients processed.  " + string.Join("  ·  ", batch.Rows.GroupBy(r => r.StatusLabel).Select(g => $"{g.Count()} {g.Key}"));
            return;
        }
        if (batch.Current is not { } row || stage != BatchStage.Issue) return;
        if (lastHandoff) BatchTitle.Text = $"Batch complete · {batch.Rows.Count} of {batch.Rows.Count} recipients processed";
        BatchNumber.Text = $"Recipient {batch.Position + 1} of {batch.Rows.Count}";
        BatchRecipient.Text = string.IsNullOrWhiteSpace(row.User.DisplayName) ? row.User.UserPrincipalName : row.User.DisplayName;
        BatchUpn.Text = row.User.UserPrincipalName;
        var pending = row.Status == IssueStatus.Pending;
        BatchChoose.Visibility = Visible(pending); BatchHandoff.Visibility = Visible(!pending);
        BatchLogNotice.Text = _registrationResultMessage;
        BatchRetryLog.Visibility = Visible(_registrationJournal.PendingCount > 0);
        if (pending)
        {
            var key = BatchKeys.SelectedValue as KeyStatus;
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
