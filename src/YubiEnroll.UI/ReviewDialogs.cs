using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class MainWindow
{
    private Window ReviewWindow(string title, double width = 680)
    {
        return new Window { Owner = this, Title = title, Width = Math.Min(width, SystemParameters.WorkArea.Width),
            Height = Math.Min(600, SystemParameters.WorkArea.Height), MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily, FontSize = 14, Background = Background, Foreground = Foreground, Icon = Icon };
    }
    private static TextBlock ReviewText(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private bool ConfirmEnrollment(Provider provider, EnrollmentProfile profile, UserCandidate user, string warning)
    {
        var dialog = ReviewWindow("Confirm enrollment");
        var layout = new DockPanel { Margin = new Thickness(24) }; dialog.Content = layout;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var start = new Button { Content = "Start enrollment", Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        start.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(start);
        var body = new StackPanel();
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(ReviewText("Review before enrollment", 24));
        body.Children.Add(ReviewText("Target key: " + (_selectedKey?.Label ?? "Unavailable"), 16));
        body.Children.Add(ReviewText($"Tenant: {provider.Name}\n\nUser: {user.Label}\n\nConfiguration profile: {profile.Name}\n\nRegistration name: {(string.IsNullOrWhiteSpace(KeyName.Text) ? "Default (serial number)" : KeyName.Text.Trim())}"));
        body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(255, 242, 240)), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16), Child = ReviewText(warning) });
        body.Children.Add(ReviewText("Keep only this target connected until YubiEnroll finishes. Confirm the key in its touch and reinsertion prompts. If another sign-in is requested, cancel this attempt before reconnecting your administrator key."));
        var detail = new Expander { Header = "Technical identifiers", Content = ReviewText($"Tenant ID: {provider.TenantId}\nUser ID: {user.Id}") };
        body.Children.Add(detail);
        return dialog.ShowDialog() == true;
    }
}
