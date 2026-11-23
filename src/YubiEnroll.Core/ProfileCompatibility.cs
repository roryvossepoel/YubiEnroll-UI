using System.Text.Json;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record FidoCapabilities(string Serial, string Firmware, bool ClientPin, bool ResidentKey,
    bool PinPolicy, bool AlwaysUvSupported, bool AlwaysUvEnabled, bool EnterpriseAttestation,
    int MinimumPinLength, int MaximumPinLength, bool ResetBlocked, bool ResetOverUsb, bool AlwaysUvLocked, bool ForcePinChange = false, bool IsNfc = false, bool ResetOverNfc = false, bool? CanListPasskeys = null, bool? PinConfigured = null)
{
    public bool ResetAllowed => !ResetBlocked && (IsNfc ? ResetOverNfc : ResetOverUsb);

    public static FidoCapabilities Parse(string json, ResetKey target)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Duplicate properties must not turn an ambiguous response into a successful check.
            static void Unique(JsonElement element)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                    if (!names.Add(p.Name)) throw new InvalidDataException();
            }
            Unique(root);
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("serial").GetString() != target.Serial)
                throw new InvalidDataException();
            if (target.IsNfc && (!root.TryGetProperty("reader", out var reader) || reader.GetString() != target.Reader))
                throw new InvalidDataException();
            var firmware = root.GetProperty("firmware").GetString() ?? "";
            var expected = Regex.Match(target.Model, @"\((\d+\.\d+\.\d+)\)$");
            if (!Version.TryParse(firmware, out _) || !expected.Success || expected.Groups[1].Value != firmware)
                throw new InvalidDataException();
            var options = root.GetProperty("options");
            Unique(options);
            foreach (var p in options.EnumerateObject()) _ = p.Value.GetBoolean();
            bool Present(string name) => options.TryGetProperty(name, out _);
            bool Enabled(string name) => options.TryGetProperty(name, out var value) && value.GetBoolean();
            var minimum = root.GetProperty("minimumPinLength").GetInt32();
            var maximum = root.GetProperty("maximumPinLength").GetInt32();
            if (minimum is < 4 or > 63 || maximum < minimum || maximum > 63) throw new InvalidDataException();
            return new(target.Serial, firmware, Present("clientPin"), Enabled("rk"),
                Enabled("authnrCfg") && Enabled("setMinPINLength"), Present("alwaysUv"), Enabled("alwaysUv"),
                Present("ep"), minimum, maximum, root.GetProperty("resetBlocked").GetBoolean(),
                root.GetProperty("resetOverUsb").GetBoolean(), root.GetProperty("alwaysUvLocked").GetBoolean(), root.GetProperty("forcePinChange").GetBoolean(), target.IsNfc,
                root.TryGetProperty("resetOverNfc", out var nfcReset) && nfcReset.GetBoolean(),
                root.TryGetProperty("canListPasskeys", out var listing) ? listing.GetBoolean() : null, Present("clientPin") ? Enabled("clientPin") : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The selected key's FIDO capabilities could not be verified. Refresh and select the key again.");
        }
    }
}

public sealed record ProfileCompatibilityResult(IReadOnlyList<string> Issues)
{
    public bool Compatible => Issues.Count == 0;
    public string Message => Compatible ? "Profile requirements verified for this key. YubiEnroll will apply the settings during enrollment." :
        "This profile cannot be applied to the selected key:\n" + string.Join("\n", Issues.Select(x => "• " + x));
}

public static class ProfileCompatibility
{
    public static ProfileSetup Basic(string name) => new(name, MinPinLength: null);
    public static ProfileSetup Managed(string name) => new(name, ForcePinChange: true);

    public static string Requirements(ProfileSetup p)
    {
        var requirements = new List<string>();
        if (p.MinPinLength.HasValue) requirements.Add("configurable minimum PIN length");
        if (p.ForcePinChange == true) requirements.Add("forced PIN change on first use");
        if (p.RequireAlwaysUv == true) requirements.Add("Always UV");
        if (p.RequireEa == true) requirements.Add("provisioned enterprise attestation");
        var result = requirements.Count == 0 ? "No advanced PIN policy required." :
            "Requires: " + string.Join(", ", requirements) + ". These PIN policy features are generally available on standard YubiKey 5 firmware 5.7+. Model and actual key capabilities are checked before enrollment.";
        if (!p.Reset.HasValue || !p.RandomPin.HasValue || !p.RequireAlwaysUv.HasValue || !p.RequireEa.HasValue || !p.ForcePinChange.HasValue ||
            p.RandomPin == true && !p.RandomPinLength.HasValue)
            result += " Unspecified options must be made explicit before compatibility can be verified.";
        return result;
    }

    public static ProfileCompatibilityResult Evaluate(ProfileSetup p, FidoCapabilities? c, FidoPinState pin = FidoPinState.Unknown)
    {
        var issues = new List<string>();
        try { p.Validate(); } catch (ArgumentException ex) { issues.Add(ex.Message); }
        if (!p.Reset.HasValue || !p.RandomPin.HasValue || !p.RequireAlwaysUv.HasValue || !p.RequireEa.HasValue || !p.ForcePinChange.HasValue)
            issues.Add("Set the profile checkboxes explicitly in Settings → Profiles; CLI defaults cannot be verified in advance.");
        if (p.RandomPin == true && !p.RandomPinLength.HasValue)
            issues.Add("Specify the generated PIN length in the profile.");
        if (c == null)
        {
            issues.Add("Key capabilities are unavailable. Refresh the connected keys; enrollment remains blocked until the key can be read.");
            return new(issues);
        }
        if (!c.ClientPin || !c.ResidentKey) issues.Add("FIDO2 PIN and discoverable-credential support are required.");
        if (p.MinPinLength.HasValue && !c.PinPolicy) issues.Add("Configurable minimum PIN length is not supported. Choose a profile without a minimum PIN override.");
        if (p.ForcePinChange == true && !c.PinPolicy) issues.Add("Forced PIN change on first use is not supported. Use another profile or a supporting key.");
        if (p.RequireAlwaysUv == true && !c.AlwaysUvSupported) issues.Add("Always UV is not supported.");
        if (p.RequireAlwaysUv == false && c.AlwaysUvLocked) issues.Add("Always UV cannot be disabled on this key. Enable it in a suitable profile.");
        if (p.RequireEa == true && !c.EnterpriseAttestation) issues.Add("This key is not provisioned for enterprise attestation.");
        if (p.Reset == true && !c.ResetAllowed) issues.Add("FIDO reset is unavailable over this connection or blocked by this key's configuration. No broader factory reset will be attempted.");
        if (p.Reset == false && pin == FidoPinState.Blocked) issues.Add("The FIDO PIN is blocked. A FIDO reset is required before enrollment.");
        // The current floor is deliberately conservative even if reset is requested: no reset is performed just to discover a factory limit.
        if (p.MinPinLength is int min && (min < c.MinimumPinLength || min > c.MaximumPinLength))
            issues.Add($"The minimum PIN length must be between this key's reported limits ({c.MinimumPinLength}–{c.MaximumPinLength}).");
        if (p.RandomPin == true && p.RandomPinLength is int length &&
            (length < Math.Max(c.MinimumPinLength, p.MinPinLength ?? 0) || length > c.MaximumPinLength))
            issues.Add($"The generated PIN length must be between {Math.Max(c.MinimumPinLength, p.MinPinLength ?? 0)} and {c.MaximumPinLength} for this key and profile.");
        return new(issues);
    }
}

public static class FidoCapabilityProbe
{
    public static string Script
    {
        get
        {
            using var stream = typeof(FidoCapabilityProbe).Assembly.GetManifestResourceStream("YubiEnroll.Core.Resources.InspectFido.py")
                ?? throw new InvalidOperationException("The bundled capability probe is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static async Task<FidoCapabilities> ReadAsync(CliRunner runner, ResetKey target, CancellationToken cancellation = default)
    {
        // Validate the serial with the existing strict validator; never use a shell or substitute the first key.
        _ = KeyResetService.Selector(target);
        // --force suppresses only ykman's script prompt for this fixed read-only resource.
        // It is never passed to enrollment, reset or any configuration operation.
        var result = await runner.CaptureWithInputAsync(TimeSpan.FromSeconds(15), Script, cancellation,
            "script", "--force", "-", target.Serial, target.Reader ?? "");
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not read FIDO capabilities for the selected key. Reconnect it, close other key tools and Refresh.");
        return FidoCapabilities.Parse(result.Output, target);
    }
}
