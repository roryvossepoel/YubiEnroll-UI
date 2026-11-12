using System.Globalization;
using System.IO;
using System.Windows;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class ProfileWindow : Window
{
    private readonly ConfigSnapshot _snapshot;
    private readonly string? _editName;
    public string? SavedProfileName { get; private set; }
    public string? BackupPath { get; private set; }

    public ProfileWindow(ConfigSnapshot snapshot, string? editName = null)
    {
        _snapshot = snapshot;
        _editName = editName;
        InitializeComponent();
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
        var profile = editName == null ? new ProfileSetup("") : ProfileConfigWriter.Read(snapshot.Text ?? "", editName);
        Heading.Text = editName == null ? "New configuration profile" : "Edit configuration profile";
        ProfileName.Text = profile.Name;
        ProfileName.IsReadOnly = false;
        ResetProfile.IsChecked = profile.Reset;
        MinimumPinLength.Text = profile.MinPinLength?.ToString(CultureInfo.InvariantCulture) ?? "";
        AlwaysUv.IsChecked = profile.RequireAlwaysUv;
        EnterpriseAttestation.IsChecked = profile.RequireEa;
        ForcePinChange.IsChecked = profile.ForcePinChange;
        RandomPin.IsChecked = profile.RandomPin;
        GeneratedPinLength.Text = profile.RandomPinLength?.ToString(CultureInfo.InvariantCulture) ?? "";
        var linked = ConfigReader.Parse(snapshot.Text ?? "").Providers.Where(x => x.Profile == editName).Select(x => x.Name).ToArray();
        LinkedTenants.ItemsSource = linked;
        UsageText.Text = linked.Length > 0 ? $"Used by {linked.Length} tenant(s). Changes apply to all of them." :
            "Select this profile in the main window to use it for enrollment.";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TrySave();

    internal bool TrySave()
    {
        try
        {
            static int? Number(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    throw new ArgumentException("Enter numeric PIN lengths (4–63), or leave empty for CLI defaults.");
                return n;
            }
            var setup = new ProfileSetup(ProfileName.Text, ResetProfile.IsChecked, Number(MinimumPinLength.Text),
                AlwaysUv.IsChecked, EnterpriseAttestation.IsChecked, ForcePinChange.IsChecked,
                RandomPin.IsChecked, Number(GeneratedPinLength.Text)).Validate();
            BackupPath = ProfileConfigWriter.Save(_snapshot, setup, _editName);
            SavedProfileName = setup.Name;
            DialogResult = true;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        { ValidationText.Text = ex.Message; }
        catch (UnauthorizedAccessException) { ValidationText.Text = "No write access to the configuration. Check the Windows account and folder permissions."; }
        catch (IOException) { ValidationText.Text = "Could not save. Close other YubiEnroll sessions, check folder access and reopen this window."; }
        return false;
    }
}
