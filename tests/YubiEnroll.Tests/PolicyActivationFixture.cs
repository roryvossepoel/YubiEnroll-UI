using YubiEnroll.Core;

internal static class PolicyActivationFixture
{
    // Independent of the native executable; CI invokes the real providers activate between calls.
    public static void Run(string action, string beforePath, string path)
    {
        string[] names = ["New key - temporary PIN", "New key - manual PIN", "Add passkey"];
        var sections = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        for (int i = 0; i < names.Length; i++)
            sections[$@"YubiEnroll\Profiles\Profile{i+1}"] = new Dictionary<string, object?> {
                ["Name"] = names[i], ["MinPinLength"] = 0, ["Reset"] = i < 2 ? 1 : 0,
                ["AlwaysUv"] = 0, ["RequireEa"] = 0, ["ForcePinChange"] = i == 0 ? 1 : 0,
                ["RandomPin"] = i == 0 ? 1 : 0, ["RandomPinLength"] = i == 0 ? 6 : 0 };
        var policy = DeskPolicy.Read(k => sections.GetValueOrDefault(k), "", "");
        if (action == "prepare")
        {
            DeskPolicy.Synchronize(policy, () => path);
            TenantConfigWriter.Save(ConfigSnapshot.Read(path), new TenantSetup("Second pilot tenant",
                "22222222-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "http://localhost/yubienroll-redirect", "", false));
            File.Copy(path, beforePath);
            return;
        }
        if (action != "verify") throw new ArgumentException("Unknown test command.");
        var before = ConfigReader.Read(beforePath);
        var after = ConfigReader.Read(path);
        if (after.ActiveProvider != "Second pilot tenant") throw new Exception("Native tenant switch did not take effect.");
        var provider = before.Providers.Single(p => p.Name == after.ActiveProvider);
        foreach (var name in names)
        {
            ConfigReader.EnsureUnchanged(after, provider, before.Profiles.Single(p => p.Name == name));
            if (DeskPolicy.ReadProfile(File.ReadAllText(beforePath), name, policy) != DeskPolicy.ReadProfile(File.ReadAllText(path), name, policy))
                throw new Exception("Profile behavior changed during native activation.");
        }
    }
}
