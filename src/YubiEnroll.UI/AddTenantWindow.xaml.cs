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

    public AddTenantWindow(ConfigSnapshot snapshot, string? editName = null)
    {
        _snapshot = snapshot;
        _editName = editName;
        InitializeComponent();
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);

        if (editName != null)
        {
            var tenant = TenantConfigWriter.Read(snapshot.Text ?? "", editName);
            Heading.Text = "Edit Microsoft Entra tenant";
            Title = Heading.Text;
            TenantName.Text = tenant.Name;
            TenantName.IsReadOnly = false;
            TenantId.Text = tenant.TenantId;
            ClientId.Text = tenant.ClientId;
            RedirectUri.Text = tenant.RedirectUri;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TrySave();

    internal bool TrySave()
    {
        try
        {
            var setup = new TenantSetup(TenantName.Text, TenantId.Text, ClientId.Text, RedirectUri.Text, "", false).Validate();
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
