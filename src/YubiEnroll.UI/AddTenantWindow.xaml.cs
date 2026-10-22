using System.IO;
using System.Windows;
using System.Windows.Controls;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class AddTenantWindow : Window
{
    private readonly ConfigSnapshot _snapshot;
    private readonly string? _editName;
    public string? SavedTenantName { get; private set; }
    public string? BackupPath { get; private set; }

    private sealed record ProfileOption(string Label, string? Name)
    {
        public override string ToString() => Label;
    }

    public AddTenantWindow(ConfigSnapshot snapshot, string? editName = null)
    {
        _snapshot = snapshot;
        _editName = editName;
        InitializeComponent();

        var config = ConfigReader.Parse(snapshot.Text ?? "");
        ProfileChoice.Items.Add(new ProfileOption("Create a new profile", null));
        foreach (var profile in config.Profiles) ProfileChoice.Items.Add(new ProfileOption(profile.Name, profile.Name));
        ProfileChoice.SelectedIndex = config.Profiles.Count > 0 ? 1 : 0;
        if (editName != null)
        {
            var tenant = TenantConfigWriter.Read(snapshot.Text ?? "", editName);
            Heading.Text = "Edit Microsoft Entra tenant";
            Title = Heading.Text;
            TenantName.Text = tenant.Name;
            TenantName.IsReadOnly = true;
            TenantId.Text = tenant.TenantId;
            ClientId.Text = tenant.ClientId;
            RedirectUri.Text = tenant.RedirectUri;
            ProfileChoice.SelectedItem = ProfileChoice.Items.Cast<ProfileOption>().FirstOrDefault(x => x.Name == tenant.ProfileName);
        }
    }

    private void ProfileChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (NewProfilePanel != null)
            NewProfilePanel.Visibility = (ProfileChoice.SelectedItem as ProfileOption)?.Name == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TrySave();

    internal bool TrySave()
    {
        try
        {
            var selected = ProfileChoice.SelectedItem as ProfileOption;
            var create = selected?.Name == null;
            if (create && !int.TryParse(PinLength.Text, out _)) throw new ArgumentException("Enter a numeric PIN length (4–63).");
            var setup = new TenantSetup(TenantName.Text, TenantId.Text, ClientId.Text, RedirectUri.Text,
                selected?.Name ?? ProfileName.Text, create, create ? int.Parse(PinLength.Text) : 6, ForcePinChange.IsChecked == true).Validate();
            BackupPath = TenantConfigWriter.Save(_snapshot, setup, _editName);
            SavedTenantName = setup.Name;
            DialogResult = true;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            ValidationText.Text = ex.Message;
        }
        catch (UnauthorizedAccessException) { ValidationText.Text = "No write access to the configuration. Check the Windows account and folder permissions."; }
        catch (IOException) { ValidationText.Text = "Could not save the configuration. Close other YubiEnroll sessions, check folder access and reopen this window."; }
        return false;
    }
}
