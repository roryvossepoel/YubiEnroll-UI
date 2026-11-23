using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record RegisteredCredential(string Name, string Model, string Id)
{
    // Graph reports the authenticator model separately from the user-editable display name.
    // Classify known Yubico hardware families only; "Security Key" alone has no vendor.
    public bool IsYubicoHardware => Regex.IsMatch(Model,
        @"^(?:(?:Yubico )?YubiKey (?:5(?:Ci?)?|Bio|4|NEO|FIPS)|Yubico Security Key|Security Key (?:C )?(?:NFC )?by Yubico)(?: |$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
