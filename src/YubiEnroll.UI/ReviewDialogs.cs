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
    private bool? ChooseBatchPinExport()
    {
        var dialog = ReviewWindow("Export batch results", 460);
        dialog.MinHeight = 0;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.ResizeMode = ResizeMode.NoResize;
        var body = new StackPanel { Margin = new Thickness(24) };
        dialog.Content = body;
        var pins = new CheckBox { Content = "Include initial PINs", IsChecked = false, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(pins);
        body.Children.Add(ReviewText("If selected, the CSV will contain readable initial PINs for recipients in this batch."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = "Choose file…", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        body.Children.Add(buttons);
        return dialog.ShowDialog() == true ? pins.IsChecked == true : null;
    }

    private bool ConfirmBatchReset(ResetKey target, UserCandidate user)
    {
        var dialog = ReviewWindow("Reset and register YubiKey", 515);
        dialog.MinHeight = 0;
        dialog.SizeToContent = SizeToContent.Height;
        var body = new StackPanel { Margin = new Thickness(24) };
        dialog.Content = body;
        body.Children.Add(ReviewText($"Reset {target.Model} · serial {target.Serial}?", 18));
        body.Children.Add(ReviewText($"This erases all FIDO passkeys and the FIDO PIN on this key, then registers it for {user.UserPrincipalName}."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var confirm = new Button { Content = "Reset and register", Style = (Style)FindResource("PrimaryButton") };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); body.Children.Add(buttons);
        dialog.ContentRendered += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }
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
