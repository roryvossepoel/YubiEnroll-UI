using YubiEnroll.Core;

static class Workflow055Tests
{
    public static void Run(Action<bool, string> check)
    {
        var tenant = new TenantSetup("Independent", "11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "http://localhost/yubienroll-redirect", "", false);
        var text = TenantConfigWriter.Build(null, tenant);
        check(ConfigReader.Parse(text).Profiles.Count == 0 && ConfigReader.Parse(text).Providers.Single().Profile == "", "A tenant can exist without any profile");
        check(ConfigTransfer.ValidateImport(ConfigTransfer.Export(text)).Length > 0, "Independent tenants round-trip through native TOML transfer");
        text = ProfileConfigWriter.Build(text, ProfileCompatibility.Managed("Managed") with { Reset = true });
        var legacy = TenantConfigWriter.Edit(text, tenant with { ProfileName = "Managed" }, tenant.Name);
        check(TenantConfigWriter.Read(TenantConfigWriter.Edit(legacy, tenant, tenant.Name), tenant.Name).ProfileName == "", "Editing a tenant can remove its legacy default without deleting the profile");
        var key = new FidoCapabilities("12345678", "5.4.3", true, true, false, true, true, false, 4, 63, false, true, false);
        check(PasskeyAddition.Evaluate(key, FidoPinState.Set).Compatible, "Add passkey accepts older firmware without advanced PIN policy");
        check(!PasskeyAddition.Evaluate(key, FidoPinState.NotSet).Compatible && !PasskeyAddition.Evaluate(key, FidoPinState.Blocked).Compatible && !PasskeyAddition.Evaluate(null, FidoPinState.Set).Compatible, "Add passkey blocks new, blocked or unreadable keys");
        check(!PasskeyAddition.Evaluate(key with { ForcePinChange = true }, FidoPinState.Set).Compatible, "Add passkey never clears a pending forced PIN change");
        var settings = PasskeyAddition.Settings(key);
        check(settings.Reset == false && settings.RandomPin == false && settings.ForcePinChange == false && settings.MinPinLength == 4 && settings.RandomPinLength == 4 && settings.RequireAlwaysUv == true, "Preservation mode keeps Always UV and requests no reset, replacement PIN or minimum policy change");
        var stricter = key with { MinimumPinLength = 8 };
        check(PasskeyAddition.Settings(stricter).MinPinLength == 8, "Addition carries the detected PIN floor instead of a fixed default");
        var basic = ProfileCompatibility.Basic("Basic");
        check(EnrollmentProfileLease.Resolve(basic, stricter).MinPinLength == 8 && basic.MinPinLength == null,
            "Native basic profile resolves an omitted floor without rewriting the reusable profile");
        var dir = Path.Combine(Path.GetTempPath(), "YubiEnroll-055-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "yubienroll.toml"); File.WriteAllText(path, legacy);
        try
        {
            string operationName;
            using (var lease = EnrollmentProfileLease.Create(path, settings))
            {
                operationName = lease.Profile.Name;
                var operation = ProfileConfigWriter.Read(File.ReadAllText(path), operationName);
                check(operation == settings with { Name = operationName }, "Native operation uses a separate complete profile despite destructive tenant default");
                var args = EnrollmentCommand.Build(tenant.TenantId, operationName, "Existing key", false);
                check(args.Contains(operationName) && args.Contains("--no-reset") && !args.Contains("--force"), "Addition explicitly targets the isolated profile with native confirmation intact");
                ProfileConfigWriter.Save(ConfigSnapshot.Read(path), ProfileCompatibility.Basic("Concurrent edit"));
            }
            var after = ConfigReader.Read(path);
            check(after.Profiles.Any(p => p.Name == "Concurrent edit") && after.Profiles.Any(p => p.Name == "Managed") && !after.Profiles.Any(p => p.Name == operationName) && after.Providers.Single().Profile == "Managed", "Operation cleanup preserves concurrent changes and legacy native defaults");
            var record = RegistrationRecord.Start(after.Providers.Single(), new(tenant.TenantId, "Example"), new("12345678", "YubiKey"), settings, "Extra passkey", addingPasskey: true);
            check(!record.ResetBeforeEnrollment && !record.ForcePinChangeOnFirstUse && record.ProfileName == "Add a passkey", "Registration log identifies the non-reset addition flow");
            check(record.PinUnavailableLabel == "Existing PIN" && record.Complete(7).PinUnavailableLabel == "Existing PIN", "Additional passkeys use an existing PIN even when the attempt fails");
            var custom = RegistrationRecord.Start(after.Providers.Single(), new(tenant.TenantId, "Example"), new("12345678", "YubiKey"), settings, "Setup");
            check(custom.PinUnavailableLabel == "Not generated", "A custom setup profile named like the operation does not imply use of an existing PIN");
            check((record with { GeneratePinConfigured = true }).PinUnavailableLabel == "PIN not logged" &&
                (record with { GeneratePinConfigured = null }).PinUnavailableLabel == "Not recorded", "Absent values distinguish configured generation from missing historical data");
        }
        finally { Directory.Delete(dir, true); }
        check(new UserCandidate(tenant.TenantId, "Example  user@example.test  user@example.test").Label == "Example  ·  user@example.test", "Display deduplicates identical UPN and email");
    }
}
