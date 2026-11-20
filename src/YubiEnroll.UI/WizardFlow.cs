using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
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
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void Set(ProfileCompatibilityResult result)
        {
            var message = result.Compatible ? "Compatible with this key" : string.Join(" ", result.Issues);
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

    private void RenderProgress(UniformGrid panel, string[] labels, int step)
    {
        panel.Columns = labels.Length;
        panel.Children.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border { Background = i <= step ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(234, 237, 238)),
                CornerRadius = new CornerRadius(18), Width = 32, Height = 32,
                Child = new TextBlock { Text = i < step ? "✓" : (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold } });
            var label = new TextBlock { Text = labels[i], Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(i == step ? "Heading" : "Muted"), FontWeight = i == step ? FontWeights.SemiBold : FontWeights.Normal };
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
                Grid.SetColumn(connector, 2);
                row.Children.Add(connector);
            }
            panel.Children.Add(row);
        }
    }
    private void RenderWizard()
    {
        if (ResetHeading == null || EnrollHeading == null || ResetAcknowledged == null) return;
        RenderRegistrationLogStatus();
        ReadReviewWarning();
        RenderProgress(EnrollProgress, _addPasskey ? ["Key", "Tenant", "User", "Review"] : ["Key", "Profile", "Tenant", "User", "Review"], Math.Min(_enrollStep, ReviewStep));
        RenderProgress(ResetProgress, ["Key", "Confirm", "Result"], _resetStep);
        EnrollmentTaskLabel.Text = _addPasskey ? "ADD A PASSKEY" : "SET UP A YUBIKEY";
        EnrollHeading.Text = _enrollStep == 0 ? (_addPasskey ? "Choose your existing key" : "Choose the key to set up") :
            _enrollStep == TenantStepIndex ? "Connect to a tenant" : _enrollStep == UserStepIndex ? "Choose the user" :
            _enrollStep == ReviewStep ? (_addPasskey ? "Review and add passkey" : "Review and enroll") :
            _enrollStep == EnrollmentResultStep ? "Registration result" : "Choose a configuration profile";
        EnrollDescription.Text = _enrollStep == 0 ? (_addPasskey ? "Use a key with an existing FIDO PIN. Its passkeys and PIN will be kept." : "Select the physical key you want to give to the user.") :
            _enrollStep == TenantStepIndex ? "Sign in with your administrator account." : _enrollStep == UserStepIndex ? "Select the account to register on this key." :
            _enrollStep == ReviewStep ? "Check these details before starting." : _enrollStep == EnrollmentResultStep ? "Check the outcome before continuing." :
            "Only profiles supported by the selected key can be chosen.";
        EnrollmentChooser.Visibility = Visible(_enrollStep == 0);
        TenantStep.Visibility = Visible(_enrollStep == TenantStepIndex); UserCard.Visibility = Visible(_enrollStep == UserStepIndex);
        ProfileStep.Visibility = Visible(!_addPasskey && _enrollStep == 1);
        NamePanel.Visibility = Visible(_enrollStep == UserStepIndex);
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
        EnrollBack.Content = _enrollStep == 0 || _enrollStep == EnrollmentResultStep ? "Back to Home" : "←  Back";
        EnrollmentInventoryProgress.Visibility = ResetInventoryProgress.Visibility = Visible(_inventoryDirty);
        FooterInstruction.Text = _enrollStep >= EnrollmentResultStep ? "" : _inventoryDirty ? (_enrollStep == 0 ? "" : "Checking connected keys…") :
            _enrollStep == 0 && _addPasskey && CandidateReady && !AdditionCandidateReady ? string.Join(" ", PasskeyAddition.Evaluate((EnrollmentKeys.SelectedItem as KeyStatus)?.Capabilities, (EnrollmentKeys.SelectedItem as KeyStatus)?.Pin ?? FidoPinState.Unknown).Issues) :
            _enrollStep > 0 && !TargetReady ? "The target is unavailable. Select or reconnect it." :
            _enrollStep == ReviewStep && !_reviewValid ? ProfileCompatibilityText.Text : "";
        FooterInstruction.Visibility = Visible(!string.IsNullOrWhiteSpace(FooterInstruction.Text));
        ResetHeading.Text = new[] { "Choose the key to reset", "Review the FIDO reset", "Reset result" }[_resetStep];
        ResetDescription.Text = new[] { "This removes the key’s FIDO passkeys and PIN.", "Confirm the physical key before erasing its FIDO credentials.", "Check the reported outcome and the target’s PIN status." }[_resetStep];
        OverviewChooser.Visibility = Visible(_resetStep == 0); ResetReview.Visibility = Visible(_resetStep == 1); ResetResult.Visibility = Visible(_resetStep == 2);
        ResetNext.Visibility = Visible(_resetStep != 1); ResetTargetButton.Visibility = Visible(_resetStep == 1);
        ResetNext.Content = _resetStep == 2 ? "Reset another key" : "Review reset";
        ResetNext.ContentTemplate = _resetStep == 2 ? null : (DataTemplate)FindResource("ForwardContent");
        ResetNext.IsEnabled = !_busy && (_resetStep == 2 || CandidateReady);
        ResetBack.Content = _resetStep == 1 ? "←  Back" : "Back to Home";
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
        try
        {
            var current = ConfigReader.Read(_configPath);
            foreach (var choice in Profiles.Items.OfType<ProfileChoice>())
            {
                ProfileCompatibilityResult result;
                try
                {
                    if (!current.Profiles.Any(p => p.Name == choice.Profile.Name && p.Fingerprint == choice.Profile.Fingerprint))
                        throw new InvalidOperationException();
                    result = ProfileCompatibility.Evaluate(ProfileConfigWriter.Read(File.ReadAllText(_configPath), choice.Profile.Name), TargetReady ? _selectedKey?.Capabilities : null, _selectedKey?.Pin ?? FidoPinState.Unknown);
                }
                catch { result = new(["Profile changed or could not be read. Reopen Settings."]); }
                choice.Set(result);
            }
            if (_addPasskey)
            {
                var result = PasskeyAddition.Evaluate(TargetReady ? _selectedKey?.Capabilities : null, _selectedKey?.Pin ?? FidoPinState.Unknown);
                _reviewValid = result.Compatible;
                ProfileCompatibilityText.Text = string.Join(" ", result.Issues);
                ReviewWarningBox.Visibility = Visibility.Collapsed;
                return;
            }
            var profile = SelectedProfile;
            if (!current.Profiles.Any(p => p.Name == profile.Name && p.Fingerprint == profile.Fingerprint))
                throw new InvalidOperationException("Profile changed. Reopen Settings.");
            var setup = ProfileConfigWriter.Read(File.ReadAllText(_configPath), profile.Name);
            var compatibility = ProfileCompatibility.Evaluate(setup, TargetReady ? _selectedKey?.Capabilities : null, _selectedKey?.Pin ?? FidoPinState.Unknown);
            _reviewValid = compatibility.Compatible;
            ProfileCompatibilityText.Text = compatibility.Compatible ? "" : compatibility.Message;
            ReviewWarningBox.Visibility = Visible(setup.Reset != false);
            ReviewWarning.Text = setup.Reset == true ? "All existing FIDO credentials and the FIDO PIN on this key will be erased before enrollment." : "Reset behavior is unspecified. Choose an explicit profile before continuing.";
        }
        catch { ProfileCompatibilityText.Text = Profiles.Items.Count == 0 ? "Create a configuration profile in Settings → Profiles." : "Select a compatible profile. If the configuration changed, reopen Settings."; }
    }
    private void RenderReviewSummary()
    {
        ReviewSummary.Children.Clear();
        void Row(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Muted") });
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(text, 1); row.Children.Add(text); ReviewSummary.Children.Add(row);
        }
        var user = Users.SelectedItem as UserCandidate;
        Row("Key", _selectedKey?.Key.Model ?? "Unavailable"); Row("Serial number", _selectedKey?.Key.Serial ?? "—");
        Row("Connection", _selectedKey?.Key.ConnectionLabel ?? "—");
        Row("Tenant", (Tenants.SelectedItem as Provider)?.Name ?? "—");
        Row("User", !string.IsNullOrWhiteSpace(user?.DisplayName) ? user.DisplayName : user?.Label ?? "—");
        if (!string.IsNullOrWhiteSpace(user?.UserPrincipalName)) Row("Sign-in name", user.UserPrincipalName);
        if (!string.IsNullOrWhiteSpace(user?.Email) && !string.Equals(user.Email, user.UserPrincipalName, StringComparison.OrdinalIgnoreCase)) Row("Email", user.Email);
        Row(_addPasskey ? "Action" : "Profile", _addPasskey ? "Add passkey · keep existing passkeys, PIN and key settings" : (Profiles.SelectedItem as ProfileChoice)?.Profile.Name ?? "—");
        Row("Registration name", string.IsNullOrWhiteSpace(KeyName.Text) ? "Default (serial number)" : KeyName.Text.Trim());
        ReviewRecipient.Text = user?.DisplayName ?? user?.Label ?? "";
    }
    private void EnrollNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_enrollStep == 0 && (!_addPasskey || AdditionCandidateReady) && CommitTarget()) _enrollStep = 1;
        else if (!_addPasskey && _enrollStep == 1 && TargetReady && _reviewValid) _enrollStep = TenantStepIndex;
        else if (_enrollStep == TenantStepIndex && TargetReady && IsConnected) _enrollStep = UserStepIndex;
        else if (_enrollStep == UserStepIndex && TargetReady && IsConnected && Users.SelectedItem != null && _reviewValid) _enrollStep = ReviewStep;
        else if (_enrollStep == EnrollmentResultStep) BeginNextKey();
        EnrollmentScroll.ScrollToTop(); UpdateSummary();
    }
    private void EnrollBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_enrollStep == 0 || _enrollStep == EnrollmentResultStep) ShowPage(OverviewPage);
        else { _enrollStep--; EnrollmentScroll.ScrollToTop(); UpdateSummary(); }
    }
    private void ResetNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_resetStep == 2) BeginNextKey();
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
        _enrollStep = _resetStep = 0; _resetReviewTarget = null; ResetAcknowledged.IsChecked = false;
        ResetSelection(); Query.Clear(); KeyName.Clear(); _suggestedName = "";
        _selectedKey = null; _lastKnownKey = null; _selectionSerial = null; _choosingTarget = true;
        _readingKeys = true;
        try { OverviewKeys.SelectedItem = EnrollmentKeys.SelectedItem = null; }
        finally { _readingKeys = false; }
        UpdateSummary();
    }
    private void ShowEnrollmentResult(string message)
    {
        _enrollStep = EnrollmentResultStep; EnrollmentResultText.Text = message; StatusText.Text = message; RenderWizard();
    }
}
