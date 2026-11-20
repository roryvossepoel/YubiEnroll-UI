using System.Windows;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class RemoveRegistrationWindow : Window
{
    public RemoveRegistrationWindow(Window owner, Provider tenant, UserCandidate user, RegisteredCredential registration)
    {
        InitializeComponent();
        Owner = owner;
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        KeyName.Text = registration.Name;
        KeyModel.Text = registration.Model;
        TenantLabel.Text = tenant.Name;
        UserLabel.Text = string.IsNullOrWhiteSpace(user.Label) ? user.Id : user.Label;
        UserId.Text = user.Id;
        RegistrationId.Text = registration.Id;
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
