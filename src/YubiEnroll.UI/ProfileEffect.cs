using YubiEnroll.Core;

namespace YubiEnroll.UI;

internal sealed record ProfileEffect(string Label, bool Warning = false)
{
    public static List<ProfileEffect> Describe(ProfileSetup p)
    {
        List<ProfileEffect> pills = [new(p.Reset switch { true => "⚠ Clears passkeys on key", false => "Keeps passkeys on key", _ => "Reset not specified" }, p.Reset == true),
            new(p.RandomPin switch { true => p.RandomPinLength is int length ? $"Generated PIN · {length} chars" : "Generated PIN · length not specified", false when p.Reset == true => "Set PIN during setup", false => "No PIN generated", _ => "Initial PIN not specified" })];
        if (p.ForcePinChange == true) pills.Add(new("Change PIN at first use"));
        if (p.MinPinLength is int min) pills.Add(new($"Minimum PIN · {min} chars"));
        if (p.RequireAlwaysUv == true) pills.Add(new("Always verify user"));
        if (p.RequireEa == true) pills.Add(new("Enterprise attestation"));
        return pills;
    }
}
