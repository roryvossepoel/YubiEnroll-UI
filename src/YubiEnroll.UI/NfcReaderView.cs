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
        foreach (var host in new[] { EnrollmentReaderStatus, ResetReaderStatus })
        {
            host.Children.Clear();
            host.Visibility = Visible(visible.Count > 0);
            foreach (var reader in visible)
            {
                var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = "NFC reader connected", FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Heading") });
                body.Children.Add(new TextBlock { Text = reader.Name, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0), Foreground = (Brush)FindResource("Muted") });
                body.Children.Add(new TextBlock { Text = reader.Message, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0), Foreground = reader.HasWarning
                        ? new SolidColorBrush(Color.FromRgb(133, 64, 24)) : (Brush)FindResource("Muted") });
                host.Children.Add(new Border { Child = body, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 12), Background = new SolidColorBrush(Color.FromRgb(245, 248, 240)) });
            }
        }
    }
}
