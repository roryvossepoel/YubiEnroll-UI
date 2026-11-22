using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

public partial class ProfileWindow : Window
{
    private readonly ConfigSnapshot _snapshot;
    private readonly string? _editName;
    private readonly DeskPolicy? _policy;
    public string? SavedProfileName { get; private set; }
    public string? BackupPath { get; private set; }

    private readonly Dictionary<CheckBox, WrapPanel> _missing = new();
    private bool _applying;
    public ProfileWindow(ConfigSnapshot snapshot, string? editName = null, DeskPolicy? policy = null)
    {
        _policy = policy;
        _snapshot = snapshot;
        _editName = editName;
        InitializeComponent();
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 32);
        var profile = editName == null ? ProfileCompatibility.Basic("") : ProfileConfigWriter.Read(snapshot.Text ?? "", editName);
        Heading.Text = editName == null ? "New configuration profile" : "Edit configuration profile";
        ProfileName.Text = profile.Name;
        ProfileName.IsReadOnly = false;

        UsageText.Text = "Choose this profile during key setup. It can be used with any tenant and a compatible key.";
        foreach (var box in new[] { ResetProfile, AlwaysUv, EnterpriseAttestation, ForcePinChange, RandomPin })
        {
            // Imported flags may be absent. Display a deliberate choice instead of a mixed checkbox.
            var parent = (Panel)box.Parent;
            var options = new WrapPanel { Margin = box.Margin };
            options.Children.Add(new TextBlock { Text = box.Content + " · Not specified", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) });
            foreach (var value in new[] {true,false})
            {
                var choice = new Button { Content = value ? "On" : "Off", Padding = new Thickness(9,3,9,3) };
                choice.Click += (_, _) => box.IsChecked = value;
                options.Children.Add(choice);
            }
            parent.Children.Insert(parent.Children.IndexOf(box) + 1, options);
            _missing.Add(box, options);
            box.Checked += (_, _) => UpdateRequirements(); box.Unchecked += (_, _) => UpdateRequirements();
            box.Indeterminate += (_, _) => UpdateRequirements();
        }
        MinimumPinLength.TextChanged += (_, _) => UpdateRequirements();
        GeneratedPinLength.TextChanged += (_, _) => UpdateRequirements();
        ApplySettings(profile);
        Advanced.IsExpanded = profile.RequireEa != false || profile.RequireAlwaysUv != false;
    }

    private void ApplySettings(ProfileSetup profile)
    {
        _applying = true;
        ResetProfile.IsChecked = profile.Reset;
        MinimumPinLength.Text = profile.MinPinLength?.ToString(CultureInfo.InvariantCulture) ?? "";
        AlwaysUv.IsChecked = profile.RequireAlwaysUv;
        EnterpriseAttestation.IsChecked = profile.RequireEa;
        ForcePinChange.IsChecked = profile.ForcePinChange;
        RandomPin.IsChecked = profile.RandomPin;
        GeneratedPinLength.Text = profile.RandomPinLength?.ToString(CultureInfo.InvariantCulture) ?? "";
        _applying = false; UpdateRequirements();
    }
    private void Basic_Click(object sender, RoutedEventArgs e) => ApplySettings(ProfileCompatibility.Basic(ProfileName.Text) with { Reset = ResetProfile.IsChecked });
    private void Managed_Click(object sender, RoutedEventArgs e) => ApplySettings(ProfileCompatibility.Managed(ProfileName.Text) with { Reset = ResetProfile.IsChecked });
    private void UpdateRequirements()
    {
        if (_applying) return;
        foreach (var (box, options) in _missing)
        {
            box.Visibility = box.IsChecked.HasValue ? Visibility.Visible : Visibility.Collapsed;
            options.Visibility = box.IsChecked.HasValue ? Visibility.Collapsed : Visibility.Visible;
        }
        InitialLengthRow.Visibility = RandomPin.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ResetWarning.Visibility = ResetProfile.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        static int? Length(string text) => int.TryParse(text, out var value) ? value : null;
        var p = new ProfileSetup("", ResetProfile.IsChecked, Length(MinimumPinLength.Text), AlwaysUv.IsChecked,
            EnterpriseAttestation.IsChecked, ForcePinChange.IsChecked, RandomPin.IsChecked, Length(GeneratedPinLength.Text));
        var validNumbers = (MinimumPinLength.Text.Length == 0 || p.MinPinLength != null) &&
            (GeneratedPinLength.Text.Length == 0 || p.RandomPinLength != null);
        BasicPreset.IsChecked = validNumbers && p == ProfileCompatibility.Basic("");
        ManagedPreset.IsChecked = validNumbers && p == ProfileCompatibility.Managed("");
        CustomPreset.Visibility = BasicPreset.IsChecked == true || ManagedPreset.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        RequirementsText.Text = ProfileCompatibility.Requirements(p);
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
            _policy?.CheckProfileRenameOrRemoval(_snapshot, _editName, setup.Name);
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
