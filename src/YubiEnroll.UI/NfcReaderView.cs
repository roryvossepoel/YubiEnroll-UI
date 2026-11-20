using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private readonly NfcReaderCatalog _readerCatalog = new();
    private void RenderReaderInventory(IReadOnlyList<NfcReaderStatus> readers)
    {
        var visible = _readerCatalog.Visible(readers);
        foreach (var host in new[] { EnrollmentReaderStatus, ResetReaderStatus })
        {
            host.Children.Clear();
            host.Visibility = Visible(visible.Count > 0);
            foreach (var reader in visible)
            {
                var body = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                var line = new DockPanel();
                line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = (Brush)FindResource("Accent"),
                    Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top });
                line.Children.Add(new TextBlock { Text = "NFC reader connected · " + reader.Name,
                    TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted") });
                body.Children.Add(line);
                if (reader.HasWarning)
                    body.Children.Add(new TextBlock { Text = reader.Message, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(15, 4, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(133, 64, 24)) });
                host.Children.Add(body);
            }
        }
    }
}
