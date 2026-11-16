using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class RegistrationLogView : UserControl
{
    private sealed record TenantChoice(string? Id, string Label);
    private readonly RegistrationJournal _journal;
    private IReadOnlyList<RegistrationRecord> _records = [];
    private bool _loading;
    public RegistrationLogView(RegistrationJournal journal)
    {
        _journal = journal;
        InitializeComponent();
        EnabledChoice.IsChecked = journal.Preferences.Enabled;
        FolderPath.Text = journal.Preferences.Directory;
        FolderPath.ToolTip = FolderPath.Text;
        LoggingPreferences.IsExpanded = !journal.Preferences.Enabled || journal.SettingsError != null;
        RefreshRecords();
    }
    private void SavePreference_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _journal.Configure(EnabledChoice.IsChecked == true, FolderPath.Text);
            LoggingPreferences.IsExpanded = false;
            RefreshRecords();
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex))
        { Feedback.Text = _journal.PendingCount > 0 ? "Save the pending results before changing folders. Restore access to the original folder and retry saving." : "Could not save your preference. Choose a writable folder and try again. Your previous setting is unchanged."; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the registration log folder", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) { FolderPath.Text = picker.FolderName; FolderPath.ToolTip = picker.FolderName; }
    }
    internal void RefreshRecords()
    {
        _loading = true;
        var selected = (TenantFilter.SelectedItem as TenantChoice)?.Id;
        Feedback.Text = "";
        PreferenceStatus.Text = _journal.Preferences.Enabled
            ? "Enabled for future enrollments. Files are stored under this Windows account's chosen folder."
            : "Logging is off. Existing records remain available; new enrollments are not recorded.";
        RetryButton.Visibility = _journal.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            _records = _journal.Read();
            TenantFilter.ItemsSource = new[] { new TenantChoice(null, "All tenants") }.Concat(_records.GroupBy(r => r.TenantId)
                .Select(g => new TenantChoice(g.Key, g.First().TenantName)).OrderBy(t => t.Label)).ToArray();
            TenantFilter.SelectedItem = TenantFilter.Items.Cast<TenantChoice>().FirstOrDefault(t => t.Id == selected) ?? TenantFilter.Items[0];
            if (_journal.PendingCount > 0) Feedback.Text = $"{_journal.PendingCount} result(s) are not saved. Keep the app open and retry saving. CSV export includes these in-memory results.";
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex))
        {
            _records = [];
            Feedback.Text = _journal.SettingsError ?? "Could not read the complete registration log. Check folder access and record files, then Refresh. Export is unavailable until the log can be read.";
        }
        finally { _loading = false; ApplyFilter(); }
    }
    private void Filter_Changed(object? sender, SelectionChangedEventArgs e) { if (!_loading && Records != null) ApplyFilter(); }
    private void SuccessFilter_Changed(object sender, RoutedEventArgs e) { if (!_loading && Records != null) ApplyFilter(); }
    private void ApplyFilter()
    {
        var invalidDates = FromDate.SelectedDate > ThroughDate.SelectedDate;
        IReadOnlyList<RegistrationRecord> visible = invalidDates ? [] : RegistrationJournal.Filter(_records, (TenantFilter.SelectedItem as TenantChoice)?.Id, FromDate.SelectedDate, ThroughDate.SelectedDate, SuccessOnly.IsChecked == true);
        Records.ItemsSource = visible;
        Records.Height = Math.Clamp(visible.Count * 36 + 30, 140, 260);
        CountText.Text = invalidDates ? "Choose an end date on or after the start date." : visible.Count == 0 ? "No enrollment records match this view." : $"{visible.Count} enrollment record(s) · export uses these filters";
        ExportButton.IsEnabled = visible.Count > 0;
        SelectionDetails.Text = "Select a record to see its user, key and profile details.";
    }
    private void Record_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Records.SelectedItem is not RegistrationRecord r) return;
        string Known(string value) => string.IsNullOrEmpty(value) ? "Not provided by YubiEnroll" : value;
        SelectionDetails.Text = $"UPN: {Known(r.UserPrincipalName)} · Email: {Known(r.Email)}\nKey: {r.KeyModel} · target serial {r.TargetSerial} · Profile: {r.ProfileName}\nReset before enrollment: {(r.ResetBeforeEnrollment ? "Yes" : "No")} · PIN change on first use: {(r.ForcePinChangeOnFirstUse ? "Yes" : "No")}\nAttempt: {r.AttemptId}";
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        // Refresh before export so the file matches the currently displayed, complete filtered data.
        RefreshRecords();
        if (!ExportButton.IsEnabled) return;
        var picker = new SaveFileDialog { Filter = "CSV file|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = $"yubienroll-registrations-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (!Path.GetExtension(picker.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a CSV file.");
            var separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? ";" : ",";
            File.WriteAllText(picker.FileName, RegistrationJournal.Csv(Records.Items.Cast<RegistrationRecord>(), separator), new UTF8Encoding(true));
            Feedback.Text = $"Exported {Records.Items.Count} record(s). The file contains personal data; share it deliberately.";
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex)) { Feedback.Text = "Could not export the CSV. Choose a writable location and try again."; }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_journal.Preferences.Directory)) { Feedback.Text = "The folder will be created when logging is enabled."; return; }
            Process.Start(new ProcessStartInfo(_journal.Preferences.Directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex) || ex is System.ComponentModel.Win32Exception) { Feedback.Text = "Could not open the log folder."; }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRecords();
    private void Retry_Click(object sender, RoutedEventArgs e) { _journal.RetryPending(); RefreshRecords(); }
}
