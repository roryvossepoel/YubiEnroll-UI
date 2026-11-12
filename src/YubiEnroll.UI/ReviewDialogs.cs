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
        body.Children.Add(ReviewText($"Tenant: {provider.Name}\n\nUser: {user.Label}\n\nConfiguration profile: {profile.Name}\n\nRegistration name: {(string.IsNullOrWhiteSpace(KeyName.Text) ? "Default (serial number)" : KeyName.Text.Trim())}"));
        body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(255, 242, 240)), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16), Child = ReviewText(warning) });
        body.Children.Add(ReviewText((_selectedKey == null ? "No key inspected." : "Inspected key: " + _selectedKey.Label) +
            "\nYubiEnroll selects the enrollment device independently. Confirm the physical key in its prompts. Disconnect other keys to avoid selecting the wrong device."));
        var detail = new Expander { Header = "Technical identifiers", Content = ReviewText($"Tenant ID: {provider.TenantId}\nUser ID: {user.Id}") };
        body.Children.Add(detail);
        return dialog.ShowDialog() == true;
    }

    private void ShowRegisteredKeys(UserCandidate user, Provider provider, string output)
    {
        var dialog = ReviewWindow("Registered keys", 780);
        var layout = new DockPanel { Margin = new Thickness(24) }; dialog.Content = layout;
        var title = ReviewText("Registered keys", 24);
        DockPanel.SetDock(title, Dock.Top); layout.Children.Add(title);
        var note = ReviewText($"{user.Label}\nTenant: {provider.Name}");
        DockPanel.SetDock(note, Dock.Top); layout.Children.Add(note);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => dialog.Close();
        var refresh = new Button { Content = "Refresh" }; buttons.Children.Add(refresh); buttons.Children.Add(close);
        var display = new StackPanel();
        layout.Children.Add(new ScrollViewer { Content = display, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Render(string text)
        {
            display.Children.Clear();
            var rows = RegisteredCredentials.Parse(text);
            foreach (var row in rows)
            {
                var card = new StackPanel();
                card.Children.Add(ReviewText(row.Name, 16)); card.Children.Add(ReviewText(row.Model));
                card.Children.Add(new TextBlock { Text = "Registration ID", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
                card.Children.Add(new TextBox { Text = row.Id, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0) });
                display.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = card, Margin = new Thickness(0, 0, 0, 12) });
            }
            if (rows.Count == 0) display.Children.Add(ReviewText("No registered keys could be displayed. Try Refresh or verify the user’s registrations in Entra."));
        }
        Render(output);
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                await ActivateAsync(provider);
                var result = await Runner.CaptureAsync("credentials", "list", user.Id);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Credential lookup failed (exit {result.ExitCode}).");
                Render(result.Output); Record("Registered keys refreshed.");
            }
            catch (Exception ex) { display.Children.Clear(); display.Children.Add(ReviewText("Could not refresh registered keys. Try again.")); Record("Registered key refresh failed.", "Error", ex.GetType().Name); }
            finally { refresh.IsEnabled = true; }
        };
        dialog.ShowDialog();
    }
}
