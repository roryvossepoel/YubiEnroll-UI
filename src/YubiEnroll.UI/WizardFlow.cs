using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private int _enrollStep;
    private int _resetStep;
    private ResetKey? _resetReviewTarget;
    private bool _reviewValid;
    private static Visibility Visible(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    private bool TargetReady => !_inventoryDirty && !_choosingTarget && _selectedKey != null && _knownKeys.Any(k => k.Key == _selectedKey.Key);
    private bool CandidateReady => !_inventoryDirty && EnrollmentKeys.SelectedItem is KeyStatus key && _knownKeys.Contains(key);
    private bool EnrollmentReady => TargetReady && IsConnected && _cliReady && Users.SelectedItem != null && Profiles.SelectedItem != null &&
        EnrollmentTargetGuard.CanStart(_selectedKey?.Key, _knownKeys.Select(k => k.Key).ToArray(), _unavailableKeys);

    private void RenderProgress(UniformGrid panel, string[] labels, int step)
    {
        panel.Children.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border { Background = i <= step ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(234, 237, 238)),
                CornerRadius = new CornerRadius(18), Width = 32, Height = 32,
                Child = new TextBlock { Text = (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold } });
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
        RenderProgress(EnrollProgress, ["Key", "Tenant", "User", "Review"], Math.Min(_enrollStep, 3));
        RenderProgress(ResetProgress, ["Key", "Confirm", "Result"], _resetStep);
        EnrollHeading.Text = new[] { "Choose the key to enroll", "Connect to a tenant", "Choose the recipient", "Review and enroll", "Enrollment result" }[_enrollStep];
        EnrollDescription.Text = new[] { "Select the physical key you want to give to the user.", "Sign in with your administrator account.", "Find the user who will receive this key.", "Check the target, recipient and profile before starting.", "Review the outcome before preparing another key." }[_enrollStep];
        EnrollmentChooser.Visibility = Visible(_enrollStep == 0);
        TenantStep.Visibility = Visible(_enrollStep == 1); UserCard.Visibility = Visible(_enrollStep == 2);
        PreparationCard.Visibility = Visible(_enrollStep == 3); EnrollmentResult.Visibility = Visible(_enrollStep == 4);
        FooterTargetText.Visibility = Visible(_enrollStep > 0);
        FooterTargetText.Text = _selectedKey != null ? "Target · " + _selectedKey.Key.Label : "Target unavailable · serial " + (_lastKnownKey?.Serial ?? "not selected");
        ReviewRecipient.Text = $"Tenant: {(Tenants.SelectedItem as Provider)?.Name}\nUser: {(Users.SelectedItem as UserCandidate)?.Label}";
        if (_enrollStep == 3) ReadReviewWarning();
        EnrollButton.Visibility = Visible(_enrollStep == 3);
        EnrollButton.IsEnabled = !_busy && _enrollStep == 3 && EnrollmentReady && _reviewValid;
        EnrollNext.Visibility = Visible(_enrollStep != 3);
        EnrollNext.Content = new[] { "Continue to tenant  →", "Continue to user  →", "Review enrollment  →", "", "Enroll another key" }[_enrollStep];
        EnrollNext.IsEnabled = !_busy && (_enrollStep switch { 0 => CandidateReady, 1 => TargetReady && IsConnected, 2 => TargetReady && IsConnected && Users.SelectedItem != null, 4 => true, _ => false });
        EnrollBack.Content = _enrollStep == 0 || _enrollStep == 4 ? "Back to Home" : "←  Back";
        FooterInstruction.Text = _inventoryDirty && _enrollStep < 4 ? "Checking connected keys…" :
            _enrollStep == 0 ? "" : _enrollStep == 4 ? "" : !TargetReady ? "The target is unavailable. Go back to select or reconnect it." :
            _enrollStep == 1 && !_cliReady ? "Install YubiEnroll under Settings → Prerequisites." :
            _enrollStep == 3 && !EnrollmentReady ? EnrollmentTargetNotice.Text : "";
        ResetHeading.Text = new[] { "Choose the key to reset", "Review the FIDO reset", "Reset result" }[_resetStep];
        ResetDescription.Text = new[] { "This removes the key’s FIDO passkeys and PIN.", "Confirm the physical key before erasing its FIDO credentials.", "Check the reported outcome and the target’s PIN status." }[_resetStep];
        OverviewChooser.Visibility = Visible(_resetStep == 0); ResetReview.Visibility = Visible(_resetStep == 1); ResetResult.Visibility = Visible(_resetStep == 2);
        ResetNext.Visibility = Visible(_resetStep != 1); ResetTargetButton.Visibility = Visible(_resetStep == 1);
        ResetNext.Content = _resetStep == 2 ? "Reset another key" : "Review reset  →";
        ResetNext.IsEnabled = !_busy && (_resetStep == 2 || CandidateReady);
        ResetBack.Content = _resetStep == 1 ? "←  Back" : "Back to Home";
        ResetTargetText.Text = _resetReviewTarget?.Label ?? "No target selected";
        KeyPinStatus.Text = _selectedKey is { } currentKey && currentKey.Key == _resetReviewTarget ? currentKey.PinLabel : "Target unavailable";
        ResetAvailability.Text = !IsAdministrator() ? "Restart as administrator from the startup requirements screen to reset this key." :
            !TargetReady || _selectedKey?.Key != _resetReviewTarget ? "The reviewed target is unavailable. Go back, reconnect it and Refresh." : "";
        ResetAcknowledged.IsEnabled = !_busy && TargetReady && _selectedKey?.Key == _resetReviewTarget;
        ResetTargetButton.IsEnabled = !_busy && _resetStep == 1 && TargetReady && _selectedKey?.Key == _resetReviewTarget && IsAdministrator() && ResetAcknowledged.IsChecked == true;
    }
    private void ReadReviewWarning()
    {
        _reviewValid = false;
        ProfileCompatibilityText.Text = "Choose a profile and a connected target to check compatibility.";
        try
        {
            var provider = SelectedProvider; var profile = SelectedProfile;
            ConfigReader.EnsureUnchanged(ConfigReader.Read(_configPath), provider, profile);
            var setup = ProfileConfigWriter.Read(File.ReadAllText(_configPath), profile.Name);
            var reset = setup.Reset;
            ReviewWarning.Text = reset switch
            {
                true => "This profile erases all existing FIDO credentials and resets the FIDO PIN before enrollment.",
                false => "This profile preserves existing FIDO credentials. YubiEnroll will apply the selected PIN and enrollment settings.",
                null => "Reset is not specified. YubiEnroll’s default may erase existing FIDO credentials."
            };
            var compatibility = ProfileCompatibility.Evaluate(setup, TargetReady ? _selectedKey?.Capabilities : null, _selectedKey?.Pin ?? FidoPinState.Unknown);
            ProfileCompatibilityText.Text = compatibility.Message;
            ProfileCompatibilityText.Foreground = compatibility.Compatible ? (Brush)FindResource("Heading") : new SolidColorBrush(Color.FromRgb(164, 38, 44));
            _reviewValid = compatibility.Compatible;
        }
        catch { ReviewWarning.Text = "The configuration could not be verified. Check Settings and select the tenant and profile again."; }
    }
    private void EnrollNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_enrollStep == 0 && CommitTarget()) _enrollStep = 1;
        else if (_enrollStep == 1 && TargetReady && IsConnected) _enrollStep = 2;
        else if (_enrollStep == 2 && TargetReady && IsConnected && Users.SelectedItem != null) _enrollStep = 3;
        else if (_enrollStep == 4) BeginNextKey();
        EnrollmentScroll.ScrollToTop(); UpdateSummary();
    }
    private void EnrollBack_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_enrollStep == 0 || _enrollStep == 4) ShowPage(OverviewPage);
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
        _enrollStep = 4; EnrollmentResultText.Text = message; StatusText.Text = message; RenderWizard();
    }
}
