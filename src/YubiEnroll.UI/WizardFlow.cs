using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private string[] EnrollmentStepLabels => _addPasskey ? ["Key", "Tenant", "User", "Review"] : ["Key", "Profile", "Tenant", "User", "Review"];

    private bool _addPasskey;
    private int TenantStepIndex => _addPasskey ? 1 : 2;
    private int UserStepIndex => _addPasskey ? 2 : 3;
    private int ReviewStep => _addPasskey ? 3 : 4;
    private int EnrollmentResultStep => ReviewStep + 1;
    private sealed class ProfileChoice(EnrollmentProfile profile) : System.ComponentModel.INotifyPropertyChanged
    {
        public EnrollmentProfile Profile { get; } = profile;
        public bool Compatible { get; private set; }
        public string Message { get; private set; } = "Choose a key to check compatibility.";
        public ProfileSetup? Setup { get; private set; }
        public List<ProfileEffect> Pills { get; private set; } = [];
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void Set(ProfileCompatibilityResult result, ProfileSetup? setup)
        {
            if (Setup != setup)
            {
                Setup = setup; Pills = setup == null ? [] : ProfileEffect.Describe(setup);
                PropertyChanged?.Invoke(this, new(nameof(Pills)));
            }
            var message = result.Compatible ? "" : string.Join(" ", result.Issues);
            if (Compatible == result.Compatible && Message == message) return;
            Compatible = result.Compatible; Message = message;
            PropertyChanged?.Invoke(this, new(nameof(Compatible))); PropertyChanged?.Invoke(this, new(nameof(Message)));
        }
    }
    private int _enrollStep;
    private int _resetStep;
    private ResetKey? _resetReviewTarget;
    private bool _reviewValid;
    private static Visibility Visible(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    private bool TargetReady => !_inventoryDirty && !_choosingTarget && _selectedKey != null && _knownKeys.Any(k => k.Key == _selectedKey.Key);
    private bool CandidateReady => !_inventoryDirty && EnrollmentKeys.SelectedItem is KeyStatus key && _knownKeys.Contains(key);
    private bool EnrollmentReady => TargetReady && IsConnected && _cliReady && Users.SelectedItem != null && (_addPasskey || Profiles.SelectedItem != null) &&
        EnrollmentTargetGuard.CanStart(_selectedKey?.Key, _knownKeys.Select(k => k.Key).ToArray(), _unavailableKeys);

    private void SetBackAction(Button button, string? stepLabel, string homeLabel = "Back to Home")
    {
        button.Content = stepLabel ?? homeLabel;
        button.ContentTemplate = stepLabel == null ? null : (DataTemplate)FindResource("BackContent");
    }

    private void RenderProgress(Grid panel, string[] labels, int step)
    {
        panel.ColumnDefinitions.Clear();
        panel.Children.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border { Background = i <= step ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(234, 237, 238)),
                CornerRadius = new CornerRadius(18), Width = 32, Height = 32,
                Child = new TextBlock { Text = i < step ? "✓" : (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold } });
            Grid.SetColumn(row, 2 * i);
            panel.Children.Add(row);
            var label = new TextBlock { Text = labels[i], Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(i == step ? "Heading" : "Muted"), FontWeight = i == step ? FontWeights.SemiBold : FontWeights.Normal };
            // Reserve both text weights so changing the active step cannot move adjacent markers.
            var measure = new TextBlock { Text = labels[i], FontWeight = FontWeights.SemiBold };
            row.Children.Add(measure); // Inherit the same font and size as the visible label.
            var available = new Size(double.PositiveInfinity, double.PositiveInfinity);
            measure.Measure(available);
            var boldWidth = measure.DesiredSize.Width;
            measure.FontWeight = FontWeights.Normal;
            measure.Measure(available);
            label.MinWidth = Math.Ceiling(Math.Max(boldWidth, measure.DesiredSize.Width));
            row.Children.Remove(measure);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (i < labels.Length - 1)
            {
                var connector = new Border
                {
                    Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 16, 0),
                    Background = i < step ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(221, 226, 228))
                };
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(connector, 2 * i + 1);
                panel.Children.Add(connector);
            }
            else panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
    }
    private void RenderWizard()
    {
        if (ResetHeading == null || EnrollHeading == null || ResetAcknowledged == null) return;
        RenderBatch();
        RenderRegistrationLogStatus();
        ReadReviewWarning();
        ReviewNotice.Tag = ReviewWarningBox.Visibility == Visibility.Visible || Equals(TenantNoticeBox.Tag, "Warning") ? "Warning" : null;
        ReviewNotice.Visibility = Visible(ReviewWarningBox.Visibility == Visibility.Visible || ReviewTargetNotice.Text.Length > 0);
        EnrollmentKeys.Tag = _addPasskey ? "Add" : null;
        RenderProgress(EnrollProgress, EnrollmentStepLabels, _enrollStep == EnrollmentResultStep && _receiptSuccess ? EnrollmentResultStep : Math.Min(_enrollStep, ReviewStep));
        RenderProgress(ResetProgress, ["Key", "Confirm", "Result"], _resetStep);
        EnrollmentTaskLabel.Text = _addPasskey ? "ADD A PASSKEY" : "SET UP A YUBIKEY";
        EnrollHeading.Text = _enrollStep == 0 ? (_addPasskey ? "Choose your existing key" : "Choose the key to set up") :
            _enrollStep == TenantStepIndex ? "Connect to a tenant" : _enrollStep == UserStepIndex ? "Choose the user" :
            _enrollStep == ReviewStep ? (_addPasskey ? "Review and add passkey" : "Review and enroll") :
            _enrollStep == EnrollmentResultStep ? "Registration summary" : "Choose a configuration profile";
        EnrollDescription.Text = _enrollStep == 0 ? (_addPasskey ? "Use a key with an existing FIDO PIN. Its passkeys and PIN will be kept." : "Select the physical key you want to give to the user.") :
            _enrollStep == TenantStepIndex ? "Sign in with your administrator account." : _enrollStep == UserStepIndex ? "Select the account to register on this key." :
            _enrollStep == ReviewStep ? "Check these details before starting." : _enrollStep == EnrollmentResultStep ? "Check the outcome before continuing." :
            "Only profiles supported by the selected key can be chosen.";
        EnrollDescription.Visibility = Visible(_enrollStep < ReviewStep);
        if (PreparePage.IsVisible) StatusBar.Visibility = Visible(_enrollStep < ReviewStep || !string.IsNullOrEmpty(StatusText.Text));
        EnrollmentChooser.Visibility = Visible(_enrollStep == 0);
        TenantStep.Visibility = Visible(_enrollStep == TenantStepIndex); UserCard.Visibility = Visible(_enrollStep == UserStepIndex);
        ProfileStep.Visibility = Visible(!_addPasskey && _enrollStep == 1);
        EnrollmentScroll.Visibility = Visible(_enrollStep != UserStepIndex);
        UserContext.Text = $"Tenant: {(Tenants.SelectedItem as Provider)?.Name}   ·   {_selectedKey?.Key.Label}";
        PreparationCard.Visibility = Visible(_enrollStep == ReviewStep); EnrollmentResult.Visibility = Visible(_enrollStep == EnrollmentResultStep);
        FooterTargetText.Visibility = Visible(_enrollStep > 0 && _enrollStep < ReviewStep);
        FooterTargetText.Text = _selectedKey != null ? "Target · " + _selectedKey.Key.Label : "Target unavailable";
        if (_enrollStep == ReviewStep) RenderReviewSummary();
        EnrollButton.Content = _addPasskey ? "Add passkey" : "Start enrollment";
        EnrollButton.Visibility = Visible(_enrollStep == ReviewStep);
        EnrollButton.IsEnabled = !_busy && _enrollStep == ReviewStep && EnrollmentReady && _reviewValid;
        EnrollNext.Visibility = Visible(_enrollStep != ReviewStep);
        EnrollNext.Content = _enrollStep == 0 ? (_addPasskey ? "Continue to tenant" : "Continue to profile") :
            !_addPasskey && _enrollStep == 1 ? "Continue to tenant" : _enrollStep == TenantStepIndex ? "Continue to user" :
            _enrollStep == EnrollmentResultStep ? (_addPasskey ? "Add another passkey" : "Set up another key") : "Review registration";
        EnrollNext.ContentTemplate = _enrollStep == EnrollmentResultStep ? null : (DataTemplate)FindResource("ForwardContent");
        EnrollNext.IsEnabled = !_busy && (_enrollStep == 0 ? CandidateReady && (!_addPasskey || AdditionCandidateReady) :
            !_addPasskey && _enrollStep == 1 ? TargetReady && _reviewValid :
            _enrollStep == TenantStepIndex ? TargetReady && IsConnected :
            _enrollStep == UserStepIndex ? TargetReady && IsConnected && Users.SelectedItem != null && _reviewValid :
            _enrollStep == EnrollmentResultStep);
        SetBackAction(EnrollBack, _enrollStep == 0 || _enrollStep == EnrollmentResultStep ? null : "Back");
        EnrollmentInventoryProgress.Visibility = ResetInventoryProgress.Visibility = Visible(_inventoryDirty && _knownKeys.Count > 0);
        FooterInstruction.Text = _enrollStep >= EnrollmentResultStep ? "" : _inventoryDirty ? (_enrollStep == 0 ? "" : "Checking connected keys…") :
            _enrollStep == 0 && _addPasskey && CandidateReady && !AdditionCandidateReady ? string.Join(" ", PasskeyAddition.Evaluate((EnrollmentKeys.SelectedItem as KeyStatus)?.Capabilities, (EnrollmentKeys.SelectedItem as KeyStatus)?.Pin ?? FidoPinState.Unknown).Issues.Where(issue => issue != PasskeyAddition.PinChangeIssue)) :
            _enrollStep > 0 && !TargetReady ? "The target is unavailable. Select or reconnect it." :
            (_enrollStep == ReviewStep || _enrollStep == UserStepIndex) && !_reviewValid ? ProfileCompatibilityText.Text : "";
        FooterInstruction.Visibility = Visible(!string.IsNullOrWhiteSpace(FooterInstruction.Text));
        ResetHeading.Text = new[] { "Choose the key to reset", "Review the FIDO reset", "Reset result" }[_resetStep];
        ResetDescription.Text = new[] { "The reset clears this YubiKey’s FIDO passkeys and PIN. Online registrations are unchanged.", "Confirm the physical key before erasing its FIDO credentials.", "Check the reported outcome and the target’s PIN status." }[_resetStep];
        OverviewChooser.Visibility = Visible(_resetStep == 0); ResetReview.Visibility = Visible(_resetStep == 1); ResetResult.Visibility = Visible(_resetStep == 2);
        ResetNext.Visibility = Visible(_resetStep != 1); ResetTargetButton.Visibility = Visible(_resetStep == 1);
        ResetNext.Content = _resetStep == 2 ? "Reset another key" : "Review reset";
        ResetNext.ContentTemplate = _resetStep == 2 ? null : (DataTemplate)FindResource("ForwardContent");
        ResetNext.IsEnabled = !_busy && (_resetStep == 2 || CandidateReady);
        SetBackAction(ResetBack, _resetStep == 1 ? "Back" : null, _resetStep == 2 ? "Home" : "Back to Home");
        ResetDescription.Visibility = Visible(_resetStep != 2);
        if (ResetPage.IsVisible) StatusBar.Visibility = Visible(_resetStep != 2);
        ResetTargetText.Text = _resetReviewTarget?.Label ?? "No target selected";
        KeyPinStatus.Text = _selectedKey is { } currentKey && currentKey.Key == _resetReviewTarget ? currentKey.PinLabel : "Target unavailable";
        ResetConnectionHelp.Text = _resetReviewTarget?.IsNfc == true
            ? "Keep this key on the NFC reader. Remove and place the same key back only when the native tool asks."
            : "Follow the native prompts to unplug, reinsert and touch this same key.";
        ResetAvailability.Text = !IsAdministrator() ? "Restart as administrator from the startup requirements screen to reset this key." :
            !TargetReady || _selectedKey?.Key != _resetReviewTarget ? "The reviewed target is unavailable. Go back, reconnect it and Refresh." :
            _selectedKey?.Key.IsNfc == true && _selectedKey.Capabilities?.ResetAllowed != true ? "FIDO reset over NFC is unavailable or could not be verified for this key. Connect it by USB and select it again." : "";
        ResetAcknowledged.IsEnabled = !_busy && TargetReady && _selectedKey?.Key == _resetReviewTarget;
        ResetTargetButton.IsEnabled = !_busy && _resetStep == 1 && TargetReady && _selectedKey?.Key == _resetReviewTarget && IsAdministrator() && (_selectedKey?.Key.IsNfc != true || _selectedKey?.Capabilities?.ResetAllowed == true) && ResetAcknowledged.IsChecked == true;
    }
    private bool AdditionCandidateReady => EnrollmentKeys.SelectedItem is KeyStatus k && PasskeyAddition.Evaluate(k.Capabilities, k.Pin).Compatible;
    private void ReadReviewWarning()
    {
        _reviewValid = false;
        ReviewWarningBox.Visibility = Visibility.Collapsed;
        var caps = TargetReady ? _selectedKey?.Capabilities : null;
        var pin = _selectedKey?.Pin ?? FidoPinState.Unknown;
        if (_addPasskey)
        {
            var result = PasskeyAddition.Evaluate(caps, pin);
            _reviewValid = result.Compatible;
            ProfileCompatibilityText.Text = string.Join(" ", result.Issues);
            return;
        }
        try
        {
            var model = Tomlyn.Toml.ToModel(File.ReadAllText(_configPath));
            var current = ConfigReader.Parse(model, _configPath);
            foreach (var choice in Profiles.Items.OfType<ProfileChoice>())
            {
                if (!current.Profiles.Any(p => p.Name == choice.Profile.Name && p.Fingerprint == choice.Profile.Fingerprint))
                {
                    choice.Set(new(["Profile settings changed. Reload the configuration before continuing."]), null);
                    continue;
                }
                try
                {
                    var setup = DeskPolicy.ReadProfile(model, choice.Profile.Name, _policy);
                    var result = ProfileCompatibility.Evaluate(setup, caps, pin);
                    choice.Set(result.Compatible ? result : new(["Not supported: " + string.Join(" ", result.Issues)]), setup);
                }
                catch (InvalidDataException ex) { choice.Set(new([ex.Message]), null); }
            }
        }
        catch
        {
            foreach (var choice in Profiles.Items.OfType<ProfileChoice>())
                choice.Set(new(["Cannot read the configuration. Check the TOML file and reload."]), null);
        }
        var selected = Profiles.SelectedItem as ProfileChoice;
        _reviewValid = selected?.Compatible == true;
        ProfileCompatibilityText.Text = selected?.Message ?? (Profiles.Items.Count == 0 ? "Create a configuration profile in Settings → Profiles." : "Choose a profile to continue.");
        if (selected?.Setup is { } reviewed)
        {
            ReviewWarningBox.Visibility = Visible(reviewed.Reset != false);
            ReviewWarning.Text = reviewed.Reset == true ? $"Enrollment will erase all FIDO passkeys and the PIN on YubiKey {_selectedKey?.Key.Serial}." : "Reset behavior is unspecified. Choose an explicit profile before continuing.";
        }
    }

    private string _nameBeforeEditing = "";
    private void SetNameEditing(bool editing)
    {
        if (editing) _nameBeforeEditing = KeyName.Text;
        if (!editing) { KeyName.Text = KeyName.Text.Trim(); SuggestRegistrationName(); }
        KeyName.Visibility = Visible(editing); ReviewName.Visibility = Visible(!editing);
        EditNameButton.Content = editing ? "Done" : "Edit";
    }

    private void EditName_Click(object sender, RoutedEventArgs e)
    {
        var editing = KeyName.Visibility != Visibility.Visible;
        SetNameEditing(editing);
        if (editing) { KeyName.Focus(); KeyName.SelectAll(); }
    }

    private void RenderReviewSummary()
    {
        ReviewSummary.Children.Clear();
        var user = Users.SelectedItem as UserCandidate;
        RegistrationRows(ReviewSummary, _selectedKey?.Key, _addPasskey, (Profiles.SelectedItem as ProfileChoice)?.Profile.Name, Tenants.SelectedItem as Provider, user);
        ReviewRecipient.Text = user?.DisplayName ?? user?.Label ?? "";
    }
    private void KeyName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape)) return;
        if (e.Key == System.Windows.Input.Key.Escape) KeyName.Text = _nameBeforeEditing;
        SetNameEditing(false); EditNameButton.Focus(); e.Handled = true;
    }

    private void RegistrationRows(StackPanel group, ResetKey? key, bool adding, string? profile, Provider? tenant, UserCandidate? user)
    {
        SummaryRow(group, "YubiKey", key == null ? "Unavailable" : $"{key.Model} · Serial {key.Serial}");
        SummaryRow(group, adding ? "Action" : "Profile", adding ? "Add a passkey · keep existing passkeys, PIN and key settings" : profile ?? "—");
        var tenantFields = new WrapPanel();
        tenantFields.Children.Add(SummaryValue(tenant?.Name ?? "—"));
        if (Guid.TryParse(tenant?.TenantId, out var tenantId))
        {
            var id = new TextBox { Text = tenantId.ToString(), IsReadOnly = true, ToolTip = "Tenant ID", MinHeight = 0,
                Padding = new Thickness(0), Margin = new Thickness(10,0,0,0), VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12, Foreground = (Brush)FindResource("Muted"), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            System.Windows.Automation.AutomationProperties.SetName(id, "Tenant ID");
            tenantFields.Children.Add(id);
        }
        SummaryRow(group, "Tenant", tenantFields);
        var account = new[] { user?.DisplayName, user?.UserPrincipalName }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase);
        var display = string.Join(" · ", account);
        SummaryRow(group, "User", display.Length > 0 ? display : user?.Label ?? "—");
    }
    private static TextBlock SummaryValue(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private void SummaryRow(StackPanel group, string label, string value) => SummaryRow(group, label, SummaryValue(value));
    private void SummaryRow(StackPanel group, string label, FrameworkElement value)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted") });
        Grid.SetColumn(value, 1); row.Children.Add(value); group.Children.Add(row);
    }

    private async void EnrollNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var previousStep = _enrollStep;
        if (_enrollStep == 0 && (!_addPasskey || AdditionCandidateReady) && CommitTarget()) _enrollStep = 1;
        else if (!_addPasskey && _enrollStep == 1 && TargetReady && _reviewValid) _enrollStep = TenantStepIndex;
        else if (_enrollStep == TenantStepIndex && TargetReady && IsConnected) _enrollStep = UserStepIndex;
        else if (_enrollStep == UserStepIndex && TargetReady && IsConnected && Users.SelectedItem != null && _reviewValid) _enrollStep = ReviewStep;
        else if (_enrollStep == EnrollmentResultStep)
        {
            BeginNextKey();
            if (_inventoryDirty || !_inventoryLoaded) await RefreshOverviewAsync();
        }
        if (previousStep != _enrollStep) { StatusText.Text = ""; if (_enrollStep == ReviewStep) SetNameEditing(false); }
        EnrollmentScroll.ScrollToTop(); UpdateSummary();
        if (_enrollStep == UserStepIndex) Query.Focus();
    }
    private void EnrollBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_enrollStep == 0 || _enrollStep == EnrollmentResultStep) ShowPage(OverviewPage);
        else { _enrollStep--; StatusText.Text = ""; EnrollmentScroll.ScrollToTop(); UpdateSummary(); }
    }
    private async void ResetNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_resetStep == 2) { BeginNextKey(); if (!_smokeTest) await RefreshOverviewAsync(); }
        else if (_resetStep == 0 && CommitTarget())
        {
            _resetReviewTarget = _selectedKey!.Key; ResetAcknowledged.IsChecked = false; _resetStep = 1;
        }
        UpdateSummary();
    }
    private void ResetBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ResetAcknowledged.IsChecked = false;
        if (_resetStep == 1) { _resetStep = 0; _resetReviewTarget = null; UpdateSummary(); }
        else ShowPage(OverviewPage);
    }
    private void ResetAcknowledged_Changed(object sender, RoutedEventArgs e) => RenderWizard();
    private void BeginNextKey()
    {
        StopPinRead(); _resultKey = null; ClearReceipt();
        _enrollStep = _resetStep = 0; _resetReviewTarget = null; ResetAcknowledged.IsChecked = false;
        ResetSelection(); Query.Clear(); KeyName.Clear(); _suggestedName = "";
        _selectedKey = null; _lastKnownKey = null; _selectionSerial = null; _choosingTarget = true;
        _readingKeys = true;
        try { OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = null; }
        finally { _readingKeys = false; }
        UpdateSummary();
    }
    private sealed record Receipt(ResetKey Key, Provider Tenant, UserCandidate User, ProfileSetup Profile, string Name, bool Adding);
    private Receipt? _receipt;
    private bool _receiptSuccess, _pinVisible;
    private string? _receiptPin;
    private void ClearReceipt()
    {
        _receipt = null; _receiptSuccess = false; _receiptPin = null; _pinVisible = false;
        ReceiptPin.Text = ""; ReceiptPinPanel.Visibility = Visibility.Collapsed; ReceiptDetails.Children.Clear();
        ReceiptNextStep.Text = ""; RevealPin.Content = "Show PIN";
    }
    private void RevealPin_Click(object sender, RoutedEventArgs e)
    {
        if (_receiptPin == null || !_receiptSuccess || _enrollStep != EnrollmentResultStep) return;
        _pinVisible = !_pinVisible; ReceiptPin.Text = _pinVisible ? _receiptPin : "••••••";
        RevealPin.Content = _pinVisible ? "Hide PIN" : "Show PIN";
    }
    private void ShowEnrollmentResult(int? code, string detail = "")
    {
        _receiptSuccess = code == 0;
        var (level, title, message) = code switch
        {
            0 => ("Success", "", "Registration successful · reported by YubiEnroll"),
            125 when detail == InteractiveFailure.OperatorInterrupted => ("Warning", "Registration interrupted", "Check the YubiKey and its Entra registration before retrying."),
            null or 125 or < 0 => ("Warning", "Registration outcome unknown", "Check the YubiKey and its Entra registration before retrying."),
            _ => ("Error", "Passkey registration failed", detail.Contains(InteractiveFailure.PinAuthInvalid, StringComparison.Ordinal)
                ? $"The YubiKey rejected PIN authorization ({InteractiveFailure.PinAuthInvalid}). Check the passkeys on this YubiKey before retrying."
                : "YubiEnroll reported an error. Check the YubiKey and its Entra registration before retrying.")
        };
        ReceiptOutcomeBox.Tag = level; ReceiptOutcomeTitle.Text = title; ReceiptOutcomeTitle.Visibility = Visible(title.Length > 0);
        _enrollStep = EnrollmentResultStep; EnrollmentResultText.Text = message; StatusText.Text = "";
        ReceiptDetails.Children.Clear(); ReceiptNextStep.Text = "";
        if (_receipt is { } r)
        {
            RegistrationRows(ReceiptDetails, r.Key, r.Adding, r.Profile.Name, r.Tenant, r.User);
            SummaryRow(ReceiptDetails, "Registration name", r.Name);
            SummaryRow(ReceiptDetails, "Finished", DateTimeOffset.Now.ToString("g"));
            if (_receiptSuccess && !r.Adding)
            {
                if (r.Profile.ForcePinChange == true) ReceiptNextStep.Text = "You must change this PIN on first use.";
                if (r.Profile.RandomPin == true && _receiptPin == null)
                    ReceiptNextStep.Text = ("Temporary PIN unavailable in this summary. " + ReceiptNextStep.Text).TrimEnd();
            }
        }
        ReceiptNextStep.Visibility = Visible(ReceiptNextStep.Text.Length > 0);
        ReceiptPinPanel.Visibility = Visible(_receiptSuccess && _receiptPin != null);
        _pinVisible = false; ReceiptPin.Text = _receiptPin == null ? "" : "••••••"; RevealPin.Content = "Show PIN";
        EnrollmentScroll.ScrollToTop(); RenderWizard();
    }
}
