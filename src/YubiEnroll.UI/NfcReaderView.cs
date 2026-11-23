using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private readonly NfcReaderCatalog _readerCatalog = new();
    private void RenderReaderInventory(IReadOnlyList<NfcReaderStatus> readers)
    {
        var visible = _readerCatalog.Visible(readers);
        foreach (var host in new[] { EnrollmentReaderStatus, ResetReaderStatus, LocalReaderStatus, BatchReaderStatus })
        {
            host.Children.Clear();
            host.Visibility = Visible(visible.Count > 0);
            foreach (var reader in visible)
            {
                var body = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                var line = new DockPanel();
                line.Children.Add(new Image { Width = 15, Height = 15, Source = (ImageSource)FindResource("NfcGlyph"),
                    Margin = new Thickness(0, 2, 7, 0), VerticalAlignment = VerticalAlignment.Top });
                var displayName = NfcReaderStatus.DisplayName(reader.Name);
                if (visible.Count(r => NfcReaderStatus.DisplayName(r.Name) == displayName) > 1) displayName = reader.Name;
                line.Children.Add(new TextBlock { Text = "NFC reader connected · " + displayName,
                    TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted") });
                body.Children.Add(line);
                if (reader.HasWarning)
                    body.Children.Add(new TextBlock { Text = reader.Message, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(22, 4, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(133, 64, 24)) });
                host.Children.Add(body);
            }
        }
    }
}
