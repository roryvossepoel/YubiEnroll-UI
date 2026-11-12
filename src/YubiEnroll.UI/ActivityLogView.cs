using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace YubiEnroll.UI;

// Session-only event summaries. Never accepts enrollment transcripts or PIN output.
internal sealed class ActivityLogView : DockPanel
{
    private sealed record Entry(DateTime Time, string Level, string Message, string Detail)
    {
        public string Heading => $"{Time:yyyy-MM-dd HH:mm:ss} · {Level} · {Message}";
        public string Text => Heading + (Detail.Length == 0 ? "" : "\n" + Detail);
    }
    private readonly ObservableCollection<Entry> _entries = new();
    private readonly ListCollectionView _view;
    internal ActivityLogView()
    {
        Margin = new Thickness(28);
        var title = new TextBlock { Text = "Activity log", FontSize = 28, FontWeight = FontWeights.SemiBold };
        SetDock(title, Dock.Top); Children.Add(title);
        var help = new TextBlock { Text = "Actions and errors from this session. Expand an event for technical details. PINs and terminal transcripts are not recorded.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) };
        SetDock(help, Dock.Top); Children.Add(help);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        SetDock(actions, Dock.Top); Children.Add(actions);
        _view = new ListCollectionView(_entries);
        var filter = new ComboBox { ItemsSource = new[] { "All", "Errors", "Warnings" }, SelectedIndex = 0, MinWidth = 120, Margin = new Thickness(0, 0, 12, 0) };
        filter.SelectionChanged += (_, _) => { _view.Filter = x => filter.SelectedIndex == 0 || ((Entry)x).Level == (filter.SelectedIndex == 1 ? "Error" : "Warning"); };
        actions.Children.Add(filter);
        var copy = new Button { Content = "Copy log" };
        copy.Click += (_, _) => { try { Clipboard.SetText(Export()); } catch { MessageBox.Show("The clipboard is unavailable. Try Export log.", "Activity log"); } };
        actions.Children.Add(copy);
        var export = new Button { Content = "Export log…" };
        export.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Text log|*.txt", FileName = $"yubienroll-activity-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true)
                try { File.WriteAllText(picker.FileName, Export()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show("Could not save the log. Choose a writable location.", "Activity log"); }
        };
        actions.Children.Add(export);
        var list = new ItemsControl { ItemsSource = _view };
        var expander = new FrameworkElementFactory(typeof(Expander));
        expander.SetBinding(Expander.HeaderProperty, new Binding("Heading"));
        expander.SetValue(MarginProperty, new Thickness(0, 0, 0, 12));
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding("Detail"));
        detail.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        detail.SetValue(MarginProperty, new Thickness(20, 8, 0, 8));
        expander.AppendChild(detail); list.ItemTemplate = new DataTemplate { VisualTree = expander };
        Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    private string Export() => string.Join("\n\n", _view.Cast<Entry>().Select(e => e.Text));
    internal void Add(string message, string level, string detail)
    {
        if (_entries.Count >= 1000) _entries.RemoveAt(_entries.Count - 1);
        _entries.Insert(0, new(DateTime.Now, level, message, detail));
    }
}
