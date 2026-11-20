using System.Windows;
using YubiEnroll.Core;
namespace YubiEnroll.UI;

public partial class LocalPinWindow : Window
{
    internal LocalPinWindow(Window owner, ResetKey key, LocalPasskey? row)
    {
        InitializeComponent(); Owner = owner; MaxHeight = SystemParameters.WorkArea.Height;
        Target.Text = key.Label; Review.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = row == null ? "Unlock the passkey list" : "Remove this passkey from the YubiKey?";
        Proceed.Content = row == null ? "Unlock" : "Remove from YubiKey";
        Proceed.Style = (Style)FindResource(row == null ? "PrimaryButton" : "DangerButton");
        Proceed.IsDefault = row == null; CancelAction.IsDefault = row != null;
        if (row != null) { Origin.Text = row.Service; Account.Text = row.Account; CredentialId.Text = row.Id; }
        Loaded += (_, _) => Pin.Focus();
    }
    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        using var secret = Pin.SecurePassword;
        Proceed.IsEnabled = secret.Length > 0;
    }
    private void Proceed_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
