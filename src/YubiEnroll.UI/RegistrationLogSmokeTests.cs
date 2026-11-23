using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private static void AssertLogScrollsToCopy(DataGrid table)
    {
        table.UpdateLayout();
        var viewer = VisualDescendants<ScrollViewer>(table).First();
        var bar = VisualDescendants<ScrollBar>(viewer).First(b => b.Orientation == Orientation.Horizontal);
        Assert(bar.IsVisible && viewer.ScrollableWidth > 10, "Wide log entries show a horizontal scrollbar");
        viewer.ScrollToRightEnd();
        table.UpdateLayout();
        var copy = VisualDescendants<Button>(table).First(b => b.IsVisible &&
            System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Copy"));
        var x = copy.TranslatePoint(new Point(), table).X;
        Assert(viewer.HorizontalOffset > 0 && x >= 0 && x + copy.ActualWidth <= table.ActualWidth + 1,
            "Scrolling right brings the entire last-column copy action into view");
        viewer.ScrollToLeftEnd();
        table.UpdateLayout();
    }

    private void TestRegistrationLogView(SettingsWindow settings)
    {
        var tenant = new Provider("Demo tenant", "ENTRA", "11111111-2222-3333-4444-555555555555", "Managed PIN", "fixture");
        var user = new UserCandidate("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Synthetic user")
        { DisplayName = "Alex Example", UserPrincipalName = "alex@example.test", Email = "alex.mail@example.test" };
        var target = new ResetKey("00123456", "YubiKey 5 NFC (5.7.1)");
        var profile = ProfileCompatibility.Managed("Managed PIN") with { Reset = true };
        Assert(!_registrationJournal.Preferences.Enabled, "Registration logging starts disabled");
        settings.AppSettingsTab.IsSelected = true;
        settings.UpdateLayout();
        Assert(settings.EmbeddedChoice.IsVisible && settings.EnabledChoice.IsVisible && settings.EnabledChoice.IsChecked == false &&
            !settings.SavePinChoice.IsEnabled && settings.FolderPath.Text == _registrationJournal.Preferences.Directory,
            "App settings shows terminal and logging choices with the journal default folder");
        Assert(!settings.RegistrationDays.IsEnabled && settings.RegistrationDays.Text == "30", "Registration retention defaults to 30 days");
        settings.EnabledChoice.IsChecked = true;
        settings.RegistrationDays.Text = "0";
        settings.SaveLogSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(!_registrationJournal.Preferences.Enabled && settings.SaveLogSettingsButton.IsEnabled && settings.LogSettingsFeedback.Text.Contains("3650"),
            "Invalid retention keeps the edit unsaved");
        settings.RegistrationDays.Text = "14";
        settings.SaveLogSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(_registrationJournal.Preferences.Enabled && _registrationJournal.Preferences.RetentionDays == 14 && settings.SavePinChoice.IsEnabled && !_registrationJournal.Preferences.SaveGeneratedPin,
            "Enabling registration logging in App settings does not opt into PIN storage");
        CaptureWindow(settings, "settings-app-settings.png");
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
        var view = (RegistrationLogView)settings.RegistrationHost.Content;
        settings.RegistrationTab.IsSelected = true;
        settings.UpdateLayout();
        Assert(view.PreferenceStatus.Text.StartsWith("Logging on") && view.Records.Items.Count == 3 && view.ExportButton.IsEnabled, "Registration log shows successful, interrupted and unknown attempts");
        view.Filters.IsExpanded = true;
        settings.UpdateLayout();
        var resultOrigin = view.ResultFilter.TranslatePoint(new Point(), view);
        var clearOrigin = view.ClearFiltersButton.TranslatePoint(new Point(), view);
        Assert(Math.Abs(resultOrigin.Y - clearOrigin.Y) < 1 &&
            Math.Abs(view.ResultFilter.ActualHeight - view.ClearFiltersButton.ActualHeight) < 1 &&
            clearOrigin.X >= resultOrigin.X + view.ResultFilter.ActualWidth + 8,
            "Clear filters aligns with the result selector and leaves space between controls");
        CaptureWindow(settings, "registration-log-filters.png");
        view.Filters.IsExpanded = false;
        view.ResultFilter.SelectedIndex = 1;
        Assert(view.Records.Items.Count == 1, "Registration export success filter matches the visible records");
        view.FromDate.SelectedDate = DateTime.Today.AddDays(1);
        view.ThroughDate.SelectedDate = DateTime.Today;
        Assert(view.Records.Items.Count == 0 && !view.ExportButton.IsEnabled, "Invalid date range cannot export unrelated records");
        view.FromDate.SelectedDate = null; view.ThroughDate.SelectedDate = null; view.ResultFilter.SelectedIndex = 0;
        view.TenantFilter.SelectedIndex = 1;
        var originalFolder = _registrationJournal.Preferences.Directory;
        _registrationJournal.Configure(true, originalFolder + "-empty");
        view.RefreshRecords();
        Assert(view.TenantFilter.SelectedIndex == 1 && view.Records.Items.Count == 0 && !view.ExportButton.IsEnabled, "Refresh cannot silently widen a tenant filter when its records disappear");
        _registrationJournal.Configure(true, originalFolder);
        view.RefreshRecords();
        view.TenantFilter.SelectedIndex = 0;
        view.Records.SelectedIndex = 2;
        var sample = view.Records.Items.Cast<RegistrationRecord>().ToArray();
        // Exercise real grid cells without modifying the journal or persisting any PIN.
        var first = sample[0] with { GeneratedPin = "001234", GeneratePinConfigured = true };
        var other = sample[1] with { GeneratedPin = new string('7', 63), GeneratePinConfigured = true };
        view.Records.ItemsSource = new[] { first, other, sample[2] };
        view.Records.ScrollIntoView(first, view.Records.Columns.Single(c => Equals(c.Header, "Initial PIN")));
        settings.UpdateLayout();
        var cells = VisualDescendants<ToggleButton>(view.Records).Where(b => b.Name == "Reveal" && b.IsVisible).ToArray();
        var showFirst = cells.Single(b => ReferenceEquals(b.DataContext, first));
        var showOther = cells.Single(b => ReferenceEquals(b.DataContext, other));
        TextBlock Value(RegistrationRecord r) => VisualDescendants<TextBlock>(view.Records).Single(t => t.Name == "PinValue" && ReferenceEquals(t.DataContext, r));
        var row = (DataGridRow)view.Records.ItemContainerGenerator.ContainerFromItem(other);
        var originalHeight = row.ActualHeight;
        var origin = showFirst.TranslatePoint(new Point(), view.Records);
        Assert(cells.Length == 2 && Value(first).Text == "••••••", "Masked PIN by default; records without a saved PIN offer no reveal action");
        Assert(Equals(showFirst.ToolTip, "Show PIN") && System.Windows.Automation.AutomationProperties.GetName(showFirst) == "Show PIN" &&
            !VisualDescendants<TextBlock>(showFirst).Any(), "Icon-only reveal has an accessible label and tooltip");
        showFirst.IsChecked = true; settings.UpdateLayout();
        Assert(Equals(showFirst.ToolTip, "Hide PIN") && System.Windows.Automation.AutomationProperties.GetName(showFirst) == "Hide PIN" &&
            ((UIElement)showFirst.Template.FindName("Slash", showFirst)).IsVisible, "Revealed PIN offers the hide icon and label");
        Assert(Value(first).Text == "001234" && showFirst.TranslatePoint(new Point(), view.Records) == origin,
            "Reveal preserves leading zeroes and keeps the button in place");
        CaptureWindow(settings, "registration-log-initial-pin.png");
        showOther.IsChecked = true; settings.UpdateLayout();
        Assert(showFirst.IsChecked == false && Value(first).Text == "••••••" && Value(other).Text == other.GeneratedPin && row.ActualHeight == originalHeight,
            "Only one PIN is visible; a long PIN wraps without resizing its row");
        view.Search.Text = "no matches";
        Assert(showOther.IsChecked == false, "Filtering the log hides a revealed PIN");
        view.Search.Clear(); view.RefreshRecords();
        CaptureWindow(settings, "settings-registration-log.png");
        var width = settings.Width; var height = settings.Height;
        settings.Width = 740; settings.Height = 600;
        CaptureWindow(settings, "registration-log-compact.png");
        view.Records.ItemsSource = new[] { sample[0] with { DisplayName = new string('W', 220) } };
        AssertLogScrollsToCopy(view.Records);
        settings.Width = width; settings.Height = height;
        settings.AppSettingsTab.IsSelected = true;
        settings.FolderPath.Text = originalFolder + "-empty";
        settings.SaveLogSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        settings.DefaultFolderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(settings.FolderPath.Text == originalFolder && _registrationJournal.Preferences.Directory == originalFolder + "-empty",
            "Use default suggests Documents without changing the saved folder until Save");
        settings.EnabledChoice.IsChecked = false;
        settings.SaveLogSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        settings.RegistrationTab.IsSelected = true;
        Assert(view.PreferenceStatus.Text.StartsWith("Logging is off") && view.Records.Items.Count == 3,
            "Returning to the registration log reflects saved preferences and retains history");
        Assert(_registrationJournal.Read().Count == 3, "Turning registration logging off preserves previous records");
    }
}
