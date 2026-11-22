using System.Text.Json;
using YubiEnroll.Core;
internal static class DeskPolicyTests
{
    internal static void Run(Action<bool,string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "desk-policy-" + Guid.NewGuid().ToString("N"), "cli.toml");
        var root = Path.GetDirectoryName(path)!;
        var values = new Dictionary<string,object>();
        DeskPolicy? Read() => DeskPolicy.Read(k => values.GetValueOrDefault(k), root, root);
        PolicySyncResult Sync() => DeskPolicy.Synchronize(Read(), () => path);
        void Apply() => Sync();
        void Refuse(Action act, string what)
        {
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try { act(); throw new Exception("Accepted invalid policy: " + what); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
            { check(before == null ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(before), what); }
        }
        void Slot(int i, string name)
        {
            string key = $"Profile{i}";
            values[key+"Name"] = name;
            foreach (var f in new[]{"Reset","AlwaysUv","RequireEa","ForcePinChange","RandomPin","RandomPinLength","MinPinLength"})
                values[key+f] = f switch {"RandomPin" => 1, "RandomPinLength" => 6, _ => 0};
        }
        string json(string name) => JsonSerializer.Serialize(new[]{ new {name, tenantId="11111111-2222-3333-4444-555555555555", clientId="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",redirectUri="http://localhost/cb"} });
        try
        {
            PolicyTemplateFixture.Write(Path.Combine(root,"template","cli.toml"), check);
            Directory.Delete(root,true);
            values["Unrelated"] = 1;
            check(Read() == null, "Unknown registry values and empty policy keys do not enable policy mode");
            check(DeskPolicy.Synchronize(Read(), () => throw new Exception("Unmanaged startup touched the config path")) == PolicySyncResult.Unmanaged, "No policy means no policy step");
            check(!Directory.Exists(root), "Unmanaged first run creates no config, backup or lock");
            Directory.CreateDirectory(root);
            var original = "# CLI owner's comment\r\nactive_provider = \"Other\"\r\n[other_settings]\r\nflag = true\r\n[providers.Other]\r\nprovider = \"OTHER\"\r\n";
            original = ProfileConfigWriter.Build(original, new ProfileSetup("Local")) + "\n# keep this comment exactly\n";
            File.WriteAllText(path,original);
            Apply();
            check(File.ReadAllText(path) == original && Directory.GetFiles(root).Length == 1, "Existing unmanaged TOML stays byte-for-byte unchanged");
            // Zero is a valid configuration value. A preferences-only policy must not even open the TOML.
            values["EmbeddedTerminal"] = 0;
            check(Read() is { EmbeddedTerminal:false, Activity:null, Registration:null }, "A single external-terminal policy manages only terminal selection");
            check(DeskPolicy.Synchronize(Read(), () => throw new Exception("Preferences-only startup touched TOML")) == PolicySyncResult.AppSettingsOnly, "App-only result does not report a TOML sync");
            values["TenantsJson"] = json("Pilot");
            check(Sync() == PolicySyncResult.TomlUpdated, "Updated status follows a successful replacement");
            var config = ConfigReader.Read(path);
            check(config.Providers.Single().Name == "Pilot" && config.Profiles.Single().Name == "Local" && config.ActiveProvider == "Other"
                && File.ReadAllText(path).Contains("other_settings"), "Tenant-only deployment preserves unrelated settings, active non-Entra provider and local profiles");
            check(File.ReadAllText(Directory.GetFiles(root,"*.bak").Single()) == original, "First policy write backs up the exact original");
            check(Sync() == PolicySyncResult.TomlUnchanged, "Existing synchronized file is reported up to date");
            check(Directory.GetFiles(root,"*.bak").Length == 1, "Unchanged policy makes no second backup");
            values.Remove("TenantsJson"); values.Remove("EmbeddedTerminal");
            Slot(5,"Pilot profile");
            Apply();
            check(ConfigReader.Read(path).Providers.Single().Name == "Pilot" && ConfigReader.Read(path).Profiles.Single().Name == "Pilot profile",
                "Profile slot 5 alone manages profiles without removing local tenants");
            var nativeText = File.ReadAllText(path);
            check(ProfileConfigWriter.Read(nativeText, "Pilot profile") is { MinPinLength: 4, RandomPinLength: 6 }
                && DeskPolicy.ReadProfile(nativeText, "Pilot profile", Read()) is { MinPinLength: null, RandomPinLength: 6 },
                "Native CLI gets all profile fields while the app retains the key-specific PIN floor");
            Refuse(() => DeskPolicy.ReadProfile(nativeText.Replace("min_pin_length = 4", "min_pin_length = 8"), "Pilot profile", Read()),
                "Editing a managed profile behind the app cannot change its enrollment policy");
            values["Profile5RandomPinLength"] = 0;
            values["Profile5MinPinLength"] = 8;
            Apply();
            check(DeskPolicy.ReadProfile(File.ReadAllText(path), "Pilot profile", Read()) is { MinPinLength: 8, RandomPinLength: 8 },
                "Default generated PIN length respects a higher configured floor");
            values["Profile5MinPinLength"] = 0; values["Profile5RandomPinLength"] = 6;
            Apply();
            // CLI can edit a closed app's normal TOML. An unchanged managed profile is reconciled on the next launch.
            ProfileConfigWriter.Save(ConfigSnapshot.Read(path),new ProfileSetup("CLI addition"));
            check(ConfigReader.Read(path).Profiles.Count == 2, "Standalone TOML writer remains usable");
            Apply();
            check(ConfigReader.Read(path).Profiles.Count == 1, "Reopening behind policy reapplies managed collection");
            values["Profile5MinPinLength"] = 9;
            Refuse(Apply, "Inconsistent PIN length rejects the entire snapshot without a write");
            values["Profile5MinPinLength"] = 0;
            Slot(2,"PILOT PROFILE");
            Refuse(Apply,"Duplicate profile names across nonconsecutive slots are rejected");
            values.Clear();
            values["TenantsJson"] = json("New");
            values["Profile3Reset"] = 1;
            Refuse(Apply,"Incomplete additional profile does not partially apply valid tenant changes");
            values.Remove("Profile3Reset");
            values["TenantsJson"] = json("Other");
            Refuse(Apply,"Managed name cannot overwrite a non-Entra provider");
            foreach (var bad in new[]{"{", "[{}]",json("Pilot").Replace("\"name\":","\"extra\":0,\"name\":"),json("Pilot").Replace("\"name\":","\"name\":\"x\",\"name\":"),json("Pilot").Replace("http://localhost/cb","https://example.test/"),json("Pilot").Replace("11111111-2222-3333-4444-555555555555","bad")})
            { values["TenantsJson"] = bad; Refuse(Apply,"Invalid tenant JSON rejected with previous CLI configuration intact"); }
            values["TenantsJson"] = "[" + json("North")[1..^1] + "," + json("north")[1..^1] + "]";
            Refuse(Apply,"Duplicate tenant names rejected");
            values["TenantsJson"] = json("Replacement");
            Apply();
            check(ConfigReader.Read(path).Providers.Single().Name == "Replacement", "Removed managed tenants disappear from the CLI file");
            values["TenantsJson"] = "[]";
            Apply();
            check(ConfigReader.Read(path).Providers.Count == 0 && ConfigReader.Read(path).ActiveProvider == "Other", "Explicit empty array clears only Entra tenants");
            values.Clear(); values["ActivityLogging"] = 0; values["RetentionDays"] = 15; values["RegistrationLogging"] = 1; values["SaveGeneratedPin"] = 0; values["RegistrationRetentionDays"] = 90;
            var prefs = Read()!;
            var activityPath = Path.Combine(root,"activity.json");
            var logPath = Path.Combine(root,"registration.json");
            var aj = new ActivityJournal(activityPath, root, prefs.Activity);
            var rj = new RegistrationJournal(logPath, root, prefs.Registration);
            check(aj.IsManaged && !aj.Preferences.Enabled && aj.Preferences.RetentionDays == 15 && rj.IsManaged && rj.Preferences.RetentionDays == 90 && !rj.Preferences.SaveGeneratedPin, "Policy log preferences override local defaults for this session");
            Refuse(() => aj.Configure(true,root,30),"Managed activity settings cannot be saved");
            Refuse(() => rj.Configure(true,root,true),"Managed registration preferences cannot be saved");
            check(!File.Exists(activityPath) && !File.Exists(logPath), "Policy preferences did not overwrite local JSON");
            foreach (var invalid in new object[] { 0, 3651, "90" })
            {
                values["RegistrationRetentionDays"] = invalid;
                Refuse(Apply, "Out-of-range or non-DWORD registration retention rejected before TOML synchronization");
            }
            values["RegistrationRetentionDays"] = 90;
            values["ActivityFolder"] = "relative";
            Refuse(Apply,"Invalid log folder fails before TOML synchronization");
            values.Clear(); var local = new ActivityJournal(activityPath, root);
            local.Configure(false,root,60);
            check(!local.IsManaged && Read() == null && new ActivityJournal(activityPath,root).Preferences.RetentionDays == 60, "Last policy removed restores local editing; unrelated managed settings preserve local preferences");
            // Simulate each section separately, including zero-valued preferences and an empty managed tenant list.
            var transfer = TenantConfigWriter.Build(null, new TenantSetup("Imported tenant","11111111-2222-3333-4444-555555555555",
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","http://localhost/cb","Imported profile",true));
            var existing = ProfileConfigWriter.Build(TenantConfigWriter.Build(null,new TenantSetup("Local tenant","11111111-2222-3333-4444-555555555555",
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","http://localhost/cb","",false)),new ProfileSetup("Local profile"));
            existing += "\n[custom]\nkeep = true\n[providers.Other]\nprovider = 'OTHER'\n";
            values["ActivityLogging"] = 0; values["RetentionDays"] = 10;
            var logging = Read()!;
            check(!logging.ManagesTenants && !logging.ManagesProfiles && logging.Activity != null && logging.Registration == null && !logging.EmbeddedTerminal.HasValue &&
                ConfigTransfer.PrepareImport(existing, transfer, logging) == ConfigTransfer.ValidateImport(transfer), "Activity-only policy leaves both lists importable");
            var userLog = new RegistrationJournal(logPath, root, logging.Registration);
            userLog.Configure(false, root, false);
            check(!userLog.IsManaged && File.Exists(logPath), "Activity policy does not block saving registration preferences");
            values.Clear(); values["TenantsJson"] = "[]";
            var tenantsOnly = Read()!;
            var profileExport = ConfigTransfer.Export(transfer,tenantsOnly);
            check(!profileExport.Contains("providers") && !profileExport.Contains("active_provider") && ConfigReader.Parse(profileExport).Profiles.Single().Name == "Imported profile",
                "Profiles export omits the managed tenants and their active provider");
            var imported = ConfigTransfer.PrepareImport(existing, profileExport, tenantsOnly);
            var merged = ConfigReader.Parse(imported);
            check(tenantsOnly.ManagesTenants && !tenantsOnly.ManagesProfiles && merged.Providers.Single().Fingerprint == ConfigReader.Parse(existing).Providers.Single().Fingerprint &&
                merged.ActiveProvider == "Local tenant" && merged.Profiles.Single().Name == "Imported profile" && imported.Contains("[custom]"), "Profile-only import preserves tenant contents, activation and unrelated data");
            Refuse(() => ConfigTransfer.PrepareImport(existing, "[providers]", tenantsOnly),"Absent editable list cannot erase profiles");
            var linked = new ConfigSnapshot(path, transfer);
            Refuse(() => tenantsOnly.CheckProfileRenameOrRemoval(linked,"Imported profile","renamed"),"A profile rename cannot change a managed tenant's legacy default");
            values.Clear(); Slot(2,"Local profile");
            var profilesOnly = Read()!;
            var withLocalReference = transfer.Replace("Imported profile","Local profile");
            var tenantExport = ConfigTransfer.Export(withLocalReference, profilesOnly);
            check(!tenantExport.Contains("[profiles") && ConfigReader.Parse(tenantExport).Providers.Single().Profile == "Local profile",
                "Tenant-only export retains the profile reference without transferring managed profiles");
            imported = ConfigTransfer.PrepareImport(existing, tenantExport, profilesOnly);
            merged = ConfigReader.Parse(imported);
            check(profilesOnly.ManagesProfiles && !profilesOnly.ManagesTenants && merged.Profiles.Single().Fingerprint == ConfigReader.Parse(existing).Profiles.Single().Fingerprint &&
                merged.Providers.Single().Name == "Imported tenant" && merged.ActiveProvider == "Imported tenant" && imported.Contains("OTHER") && imported.Contains("[custom]"),
                "Tenant-only import preserves actual managed profile, non-Entra provider and other settings");
            Refuse(() => ConfigTransfer.PrepareImport(existing, transfer, profilesOnly), "Tenant-only import cannot introduce references to ignored profiles");
            values["TenantsJson"] = "[]";
            Refuse(() => ConfigTransfer.Export(existing, Read()), "When both lists are managed export is blocked too");
            Refuse(() => ConfigTransfer.PrepareImport(existing, transfer, Read()), "When both lists are managed, import is blocked");
            values.Clear();
            File.WriteAllText(path, "[invalid");
            values["TenantsJson"] = json("North");
            Refuse(Apply,"Malformed existing TOML is never replaced");
            File.Delete(path);
            values["TenantsJson"] = "[]";
            check(Sync() == PolicySyncResult.NoTomlNeeded && !File.Exists(path), "An empty managed collection needs no new file");
            values["TenantsJson"] = json("North");
            check(Sync() == PolicySyncResult.TomlCreated, "Fresh CLI configuration is reported as created");
            check(ConfigReader.Read(path).ActiveProvider == "North", "Policy provisions the ordinary CLI file for a fresh Windows profile");
            var many = Enumerable.Range(1,80).Select(i => json("Tenant " + i)[1..^1]);
            values["TenantsJson"] = "[" + string.Join(",",many) + "]"; Apply();
            check(ConfigReader.Read(path).Providers.Count == 80, "Tenants are not limited to the five profile slots");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
