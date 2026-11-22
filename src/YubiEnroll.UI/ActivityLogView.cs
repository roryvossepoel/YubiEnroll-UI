using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

// Detailed diagnostics stay in memory; the journal keeps sanitized summaries.
internal sealed class ActivityLogView : DockPanel
{
    private sealed record Entry(DateTime Time, string Level, string Message, string Detail)
    {
        public string MessageLine => Message.ReplaceLineEndings(" ");
        public string DetailLine => Detail?.ReplaceLineEndings(" ") ?? "";
        public string Heading => $"{Time:yyyy-MM-dd HH:mm:ss} · {Level} · {Message}";
        public override string ToString() => Heading;
        public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
        public string Text => Heading + (HasDetail ? "\n" + Detail : "");
    }
    private readonly ObservableCollection<Entry> _entries = new();
    private readonly ListCollectionView _view;
    private readonly TextBlock _empty = new() { Text = "No activity yet.", Margin = new Thickness(0, 8, 0, 0) };
    private readonly ActivityJournal _journal;
    private readonly Dictionary<(DateTimeOffset, string, string), Entry> _sessionDetails = new();
    private readonly List<Entry> _unsaved = new();
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.SaddleBrown };
    internal ActivityLogView(ActivityJournal journal)
    {
        _journal = journal;
        Margin = new Thickness(18);
        System.Windows.Documents.TextElement.SetFontSize(this, 14);

        var title = new TextBlock { Text = "Activity log", Style = (Style)Application.Current.FindResource("SettingsTitle") };
        var head = new DockPanel(); SetDock(head, Dock.Top); Children.Add(head);
        var help = new TextBlock { Text = "Review recent actions, errors and technical details.", Style = (Style)Application.Current.FindResource("SettingsDescription") };
        SetDock(help, Dock.Top); Children.Add(help);
        SetDock(_notice, Dock.Top); Children.Add(_notice);
        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        SetDock(actions, Dock.Top); Children.Add(actions);
        _view = new ListCollectionView(_entries);
        ((System.Collections.Specialized.INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateEmpty();
        var filter = new ComboBox { ItemsSource = new[] { "All levels", "Error", "Warning", "Info" }, SelectedIndex = 0, MinWidth = 110, Margin = new Thickness(8,0,0,0) };
        SetDock(filter,Dock.Right); actions.Children.Add(filter);
        var search = new TextBox { ToolTip = "Search messages, levels, dates and details", MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(search, "Search activity");
        var field = new Grid(); field.Children.Add(search); var hint = new TextBlock { Text = "Search activity…", Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }; field.Children.Add(hint); actions.Children.Add(field);
        var export = new Button { Content = "Export log…" };
        var folder = new Button { Content = "Open log folder" };
        folder.Click += (_, _) =>
        {
            var path = _journal.Preferences.Directory;
            if (!Directory.Exists(path)) { _notice.Text = "The activity log folder does not exist or cannot be accessed."; return; }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { _notice.Text = "Could not open the activity log folder."; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        export.Margin = new Thickness(8,0,0,0);
        export.ToolTip = "Export filtered activity as a text file";
        buttons.Children.Add(folder); buttons.Children.Add(export);
        SetDock(buttons,Dock.Right); head.Children.Add(buttons); head.Children.Add(title);
        void Filter()
        {
            var query = search.Text.Trim(); hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Filter = x => x is Entry e && (filter.SelectedIndex == 0 || e.Level == filter.SelectedItem as string) && e.Text.Contains(query,StringComparison.OrdinalIgnoreCase);
        }
        search.TextChanged += (_,_) => Filter(); filter.SelectionChanged += (_,_) => Filter();
        export.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Text log|*.txt", FileName = $"yubikeydesk-activity-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true)
                try { File.WriteAllText(picker.FileName, Export()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show("Could not save the log. Choose a writable location.", "Activity log"); }
        };

        var table = (DataGrid)Application.Current.FindResource("ActivityTable");
        table.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { Tag: "CopyActivity", DataContext: Entry entry }) return;
            try { Clipboard.SetText(entry.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { _notice.Text = "Clipboard unavailable. Try copying again."; }
            e.Handled = true;
        }));
        table.ItemsSource = _view;
        var body = new Grid();
        body.Children.Add(table);
        _empty.Margin = new Thickness(12,58,12,0);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.IsHitTestVisible = false;
        body.Children.Add(_empty); Children.Add(body);
        Reload(); journal.Changed += Reload;
    }
    private void Reload()
    {
        _entries.Clear();
        var saved = _journal.Read().Select(e => _sessionDetails.TryGetValue((e.Time, e.Level, e.Message), out var live)
            ? live : new Entry(e.Time.LocalDateTime, e.Level, e.Message, e.Detail));
        foreach (var e in saved.Concat(_unsaved).OrderByDescending(e => e.Time)) _entries.Add(e);
        _notice.Text = _journal.Error ?? (_journal.Preferences.Enabled ? "" : "Activity saving is off. New events are visible for this session only.");
        UpdateEmpty();
    }
    private void UpdateEmpty()
    {
        _empty.Text = _entries.Count == 0 ? "No activity yet." : "No events match this filter.";
        _empty.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private string Export() => string.Join("\n\n", _view.Cast<Entry>().Select(e => e.Text));
    internal void Add(string message, string level, string detail)
    {
        var entry = new ActivityEvent(DateTimeOffset.Now, level, message, detail);
        var live = new Entry(entry.Time.LocalDateTime, level, message, detail);
        if (!_journal.Append(entry)) _unsaved.Add(live);
        else if (!string.IsNullOrWhiteSpace(detail)) _sessionDetails[(entry.Time, level, message)] = live;
        _entries.Insert(0, live);
        if (_journal.Error != null) _notice.Text = _journal.Error;
    }
}
