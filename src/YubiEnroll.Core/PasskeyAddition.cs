namespace YubiEnroll.Core;

// Explicit operation-scoped settings prevent inheritance of a provider's destructive default.
public static class PasskeyAddition
{
    public const string PinChangeIssue = "This key already requires a PIN change. Complete that change before using Add a passkey.";
    public static ProfileCompatibilityResult Evaluate(FidoCapabilities? key, FidoPinState pin)
    {
        var issues = new List<string>();
        if (key == null) issues.Add("Key capabilities are unavailable. Refresh the key before adding a passkey.");
        else
        {
            if (!key.ClientPin || !key.ResidentKey) issues.Add("This key must support FIDO2 PINs and discoverable credentials.");
            if (key.ForcePinChange) issues.Add(PinChangeIssue);
        }
        if (pin != FidoPinState.Set) issues.Add(pin == FidoPinState.Blocked ? "The existing FIDO PIN is blocked. Adding a passkey cannot bypass or reset it." :
            "A working existing FIDO PIN is required. Use Set up a YubiKey for initial configuration.");
        return new(issues);
    }
    public static ProfileSetup Settings(FidoCapabilities key) => new("Add a passkey", Reset: false,
        MinPinLength: key.MinimumPinLength, RequireAlwaysUv: key.AlwaysUvEnabled, RequireEa: false,
        ForcePinChange: false, RandomPin: false, RandomPinLength: key.MinimumPinLength);
}

public sealed class EnrollmentProfileLease : IDisposable
{
    private readonly string _path;
    public EnrollmentProfile Profile { get; }
    private EnrollmentProfileLease(string path, EnrollmentProfile profile) { _path = path; Profile = profile; }
    // YubiEnroll 1.2.0 indexes min_pin_length directly. Supply the freshly read
    // floor when a basic profile leaves it unset; never substitute a policy default.
    public static ProfileSetup Resolve(ProfileSetup setup, FidoCapabilities key) => setup with
    {
        MinPinLength = setup.MinPinLength ?? key.MinimumPinLength,
        RandomPinLength = setup.RandomPinLength ?? setup.MinPinLength ?? key.MinimumPinLength
    };
    public static EnrollmentProfileLease Create(string path, ProfileSetup setup)
    {
        var snapshot = ConfigSnapshot.Read(path);
        var name = "UI-enrollment-" + Guid.NewGuid().ToString("N");
        var content = ProfileConfigWriter.Build(snapshot.Text, setup with { Name = name });
        ConfigFileWriter.Save(snapshot, content);
        return new(path, ConfigReader.Parse(content).Profiles.Single(p => p.Name == name));
    }
    public void Dispose()
    {
        // Preserve concurrent changes; remove only our own, unchanged profile.
        var snapshot = ConfigSnapshot.Read(_path);
        var current = ConfigReader.Parse(snapshot.Text ?? "");
        if (!current.Profiles.Any(p => p.Name == Profile.Name && p.Fingerprint == Profile.Fingerprint)) return;
        ConfigFileWriter.Save(snapshot, ProfileConfigWriter.Remove(snapshot.Text!, Profile.Name));
    }
}
