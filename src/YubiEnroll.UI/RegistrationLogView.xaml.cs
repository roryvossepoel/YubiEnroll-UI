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
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (!_loading && Records != null) ApplyFilter(); }
    internal void RefreshRecords()
    {
        _loading = true;
        var selected = TenantFilter.SelectedItem as TenantChoice;
        Feedback.Text = "";
        PreferenceStatus.Text = _journal.Preferences.Enabled ? "Logging on" : "Logging is off";
        var pins = _journal.Preferences.Enabled && _journal.Preferences.SaveGeneratedPin;
        PinStatus.Text = pins ? "Initial PIN logging on" : "Initial PIN logging off";
        PinPill.Background = pins ? System.Windows.Media.Brushes.BlanchedAlmond : System.Windows.Media.Brushes.WhiteSmoke;
        RetryButton.Visibility = _journal.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            _records = _journal.Read();
            var tenants = new[] { new TenantChoice(null, "All tenants") }.Concat(_records.GroupBy(r => r.TenantId)
                .Select(g => new TenantChoice(g.Key, g.First().TenantName)).OrderBy(t => t.Label)).ToList();
            // Refresh/export must not silently widen a tenant filter when its files disappear.
            if (selected?.Id != null && tenants.All(t => t.Id != selected.Id)) tenants.Add(selected);
            TenantFilter.ItemsSource = tenants;
            TenantFilter.SelectedItem = tenants.FirstOrDefault(t => t.Id == selected?.Id) ?? tenants[0];
            if (_journal.PendingCount > 0) Feedback.Text = $"{_journal.PendingCount} result(s) are not saved. Keep the app open and retry saving. CSV export includes these in-memory results.";
        }
        catch (Exception ex) when (RegistrationJournal.IsStorageError(ex))
        {
            _records = [];
            Feedback.Text = _journal.SettingsError ?? "Could not read the complete registration log. Check folder access and record files, then reopen this tab. Export is unavailable until the log can be read.";
        }
        finally { _loading = false; ApplyFilter(); }
    }
    private void Filter_Changed(object? sender, SelectionChangedEventArgs e) { if (!_loading && Records != null) ApplyFilter(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { _loading = true; Search.Clear(); TenantFilter.SelectedIndex = 0; FromDate.SelectedDate = ThroughDate.SelectedDate = null; ResultFilter.SelectedIndex = 0; _loading = false; ApplyFilter(); }
    private void ApplyFilter()
    {
        var invalidDates = FromDate.SelectedDate > ThroughDate.SelectedDate;
        IReadOnlyList<RegistrationRecord> visible = invalidDates ? [] : RegistrationJournal.Filter(_records, (TenantFilter.SelectedItem as TenantChoice)?.Id, FromDate.SelectedDate, ThroughDate.SelectedDate, ResultFilter.SelectedIndex == 1);
        if (ResultFilter.SelectedIndex >= 2) visible = visible.Where(r => r.Outcome == (ResultFilter.SelectedIndex == 2 ? RegistrationOutcome.FailedOrInterrupted : RegistrationOutcome.Unknown)).ToList();
        var query = Search.Text.Trim();
        if (query.Length > 0) visible = visible.Where(r => string.Join(" ",r.UserLabel,r.UserPrincipalName,r.Email,r.TenantName,r.TargetSerial).Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
        var filterCount = (FromDate.SelectedDate.HasValue ? 1 : 0) + (ThroughDate.SelectedDate.HasValue ? 1 : 0) + (ResultFilter.SelectedIndex > 0 ? 1 : 0);
        Filters.Header = filterCount == 0 ? "Filters" : $"Filters ({filterCount})";
        Records.ItemsSource = visible;
        EmptyMessage.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyMessage.Text = invalidDates ? "Choose an end date on or after the start date." : visible.Count == 0 ? (_records.Count == 0 ? "No registrations to display." : "No registrations match your filters.") : "";
        CountText.Text = $"{visible.Count} enrollment record(s) · export uses these filters";
        ExportButton.IsEnabled = visible.Count > 0;
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        // Refresh before export so the file matches the currently displayed, complete filtered data.
        RefreshRecords();
        if (!ExportButton.IsEnabled) return;
        if (Records.Items.Cast<RegistrationRecord>().Any(r => !string.IsNullOrEmpty(r.GeneratedPin)) && MessageBox.Show(Window.GetWindow(this), "These results contain generated initial PINs. Include them in the CSV?", "Export registration log", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
    private void PinContext_Changed(object sender, DependencyPropertyChangedEventArgs e) =>
        ((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked = false;
    private void Pin_Unloaded(object sender, RoutedEventArgs e) =>
        ((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked = false;
    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not RegistrationRecord record) return;
        if (!string.IsNullOrEmpty(record.GeneratedPin))
        {
            var choice = MessageBox.Show(Window.GetWindow(this), "Include the initial PIN on the clipboard?", "Copy CSV row", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice != MessageBoxResult.Yes) record = record with { GeneratedPin = null };
        }
        var separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? ";" : ",";
        try { Clipboard.SetText(RegistrationJournal.Csv([record], separator, includeHeader: false)); }
        catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = "Clipboard unavailable. Try copying again."; }
    }
    private void Retry_Click(object sender, RoutedEventArgs e) { _journal.RetryPending(); RefreshRecords(); }
}
