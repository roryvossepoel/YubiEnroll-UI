using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Xml.Linq;
using YubiEnroll.Core;
internal static class PolicyTemplateFixture
{
    // Round-trip the files shipped to IT, not a separately maintained set of registry field names.
    public static void Write(string path, Action<bool,string> check)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "policy");
        var defs = XDocument.Load(Path.Combine(folder,"PasskeyDesk.admx"));
        var labels = XDocument.Load(Path.Combine(folder,"en-US","PasskeyDesk.adml"));
        XNamespace ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";
        static string A(XElement e,string key) => (string?)e.Attribute(key) ?? throw new InvalidDataException(key);
        var strings = labels.Descendants(ns + "string").ToDictionary(e=>A(e,"id"));
        var forms = labels.Descendants(ns + "presentation").ToDictionary(e=>A(e,"id"));
        foreach (var attr in defs.Descendants().Attributes().Select(a=>a.Value).Where(v=>v.StartsWith("$(string.")))
            if (!strings.ContainsKey(attr[9..^1])) throw new InvalidDataException("Unresolved ADML string: " + attr);
        var categories = defs.Descendants(ns + "category").ToDictionary(e => A(e, "name"));
        bool InCategory(XElement policy, string category)
        {
            var parent = policy.Element(ns + "parentCategory");
            var visited = new HashSet<string>();
            while (parent != null)
            {
                var name = A(parent, "ref");
                if (!visited.Add(name)) throw new InvalidDataException("Cyclic category: " + name);
                if (name == category) return true;
                parent = categories[name].Element(ns + "parentCategory");
            }
            return false;
        }
        var values = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var policies = defs.Descendants(ns + "policy").ToArray();
        foreach (var p in policies)
        {
            var prefix = DeskPolicy.RegistryPath + @"\";
            var key = A(p,"key");
            if (A(p,"class") != "Machine" || !key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || key.Length == prefix.Length)
                throw new InvalidDataException("Unexpected registry location");
            var relativeKey = key[prefix.Length..];
            var form = forms[A(p,"presentation")[15..^1]].Elements().ToDictionary(x=>A(x,"refId"));
            var elements = p.Element(ns + "elements")!.Elements().ToArray();
            if (form.Count != elements.Length) throw new InvalidDataException("Unmatched ADML controls");
            var scoped = new Dictionary<string,object?>();
            foreach (var f in elements)
            {
                if (f.Attribute("key") != null) throw new InvalidDataException("Element must use its policy subkey");
                var field = A(f,"valueName"); var input = form[A(f,"id")];
                switch (f.Name.LocalName)
                {
                    case "enum":
                        var selected = f.Elements(ns+"item").ElementAt((int)input.Attribute("defaultItem")!);
                        scoped.Add(field, (int)selected.Descendants(ns+"decimal").Single().Attribute("value")!); break;
                    case "decimal": scoped.Add(field, (int)input.Attribute("defaultValue")!); break;
                    case "text" when field == "TenantsJson": scoped.Add(field, File.ReadAllText(Path.Combine(folder,"tenants.example.json"))); break;
                    case "text" when field == "ProfilesJson": scoped.Add(field, File.ReadAllText(Path.Combine(folder,"profiles.example.json"))); break;
                    case "text" when field is "TenantId" or "ClientId": scoped.Add(field, Guid.NewGuid().ToString()); break;
                    case "text" when field == "RedirectUri": scoped.Add(field, "http://localhost/cb"); break;
                    case "text" when field.EndsWith("Name"): scoped.Add(field,A(p,"name")); break;
                    case "text" when A(f,"required") == "false": break;
                    default: throw new InvalidDataException("Unexpected template field " + field);
                }
            }
            var dir = Path.GetDirectoryName(path)!;
            var single = DeskPolicy.Read(k => k == relativeKey ? scoped : null,dir,dir);
            check(single != null, "Template works independently: " + A(p,"name"));
            if (InCategory(p, "AppSettings"))
            {
                // App-only policy must not even resolve the user's CLI configuration path.
                DeskPolicy.Synchronize(single, () => throw new InvalidDataException("App settings attempted to access TOML"));
                check(single!.Activity != null || single.Registration != null || single.EmbeddedTerminal.HasValue,
                    "App-settings policy affects only the app: " + A(p,"name"));
            }
            else
                check(InCategory(p, "CliConfig") && single is { Activity: null, Registration: null, EmbeddedTerminal: null },
                    "CLI configuration policy does not supply app preferences: " + A(p,"name"));
            values.Add(relativeKey,scoped);
        }
        check(policies.Length == 20, "Only concrete settings in template; no master switch");
        string target = Path.GetDirectoryName(path)!;
        const string tenantJson = @"YubiEnroll\Tenants", profilesJson = @"YubiEnroll\Profiles";
        var fallbackFile = path + ".slots";
        DeskPolicy? ReadWith(params string[] omitted) => DeskPolicy.Read(k => omitted.Contains(k) ? null : values.GetValueOrDefault(k), target, target);
        DeskPolicy.Synchronize(ReadWith(tenantJson, profilesJson), () => fallbackFile);
        var slots = ConfigReader.Read(fallbackFile);
        check(slots.Providers.Count == 10 && slots.Profiles.Count == 5, "All tenant and profile slots work after both JSON policies are removed");
        DeskPolicy.Synchronize(ReadWith(profilesJson), () => fallbackFile);
        check(ConfigReader.Read(fallbackFile).Providers.Single().Name == "Pilot tenant" && ConfigReader.Read(fallbackFile).Profiles.Count == 5,
            "Tenant JSON coexists with profile slots");
        DeskPolicy.Synchronize(ReadWith(tenantJson), () => fallbackFile);
        check(ConfigReader.Read(fallbackFile).Providers.Count == 10 && ConfigReader.Read(fallbackFile).Profiles.Single().Name == "Managed PIN",
            "Profile JSON coexists with tenant slots");
        var savedJson = values[profilesJson]["ProfilesJson"];
        values[profilesJson]["ProfilesJson"] = "[]";
        DeskPolicy.Synchronize(ReadWith(), () => fallbackFile);
        check(ConfigReader.Read(fallbackFile).Profiles.Count == 0, "Empty profile JSON overrides populated slots");
        var before = File.ReadAllText(fallbackFile);
        foreach (var invalid in new[] { "{", "[{}]", "[true]" })
        {
            values[profilesJson]["ProfilesJson"] = invalid;
            try { DeskPolicy.Synchronize(ReadWith(), () => fallbackFile); throw new Exception("Invalid profile JSON was accepted"); }
            catch (InvalidDataException ex) { check(ex.Message.Contains("ProfilesJson") && File.ReadAllText(fallbackFile) == before,
                "Invalid active ProfilesJson identifies the section and preserves the existing TOML"); }
        }
        values[profilesJson]["ProfilesJson"] = savedJson;
        var snapshot = OperatingSystem.IsWindows() ? CheckRegistry(values, target, check) :
            DeskPolicy.Read(k => values.GetValueOrDefault(k),target,target);
        DeskPolicy.Synchronize(snapshot,()=>path);
        var result = ConfigReader.Read(path);
        check(result.Providers.Single().Name == "Pilot tenant" && result.Profiles.Single().Name == "Managed PIN",
            "Both JSON overrides take precedence over populated slots and round-trip to the standard CLI format");
    }

    [SupportedOSPlatform("windows")]
    private static DeskPolicy? CheckRegistry(Dictionary<string, Dictionary<string, object?>> sections, string folder, Action<bool,string> check)
    {
        var testPath = $@"Software\PasskeyDesk-PolicyTest-{Guid.NewGuid():N}";
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var root = hive.CreateSubKey(testPath);
            DeskPolicy? Read() => DeskPolicy.ReadRegistry(root, folder, folder);
            root.SetValue("TenantsJson", "stale flat value");
            using (root.CreateSubKey(@"YubiEnroll\Profiles\Profile1")) { }
            check(Read() == null, "Flat values and empty subkeys leave the app unmanaged");
            foreach (var (path, fields) in sections)
            {
                using var section = root.CreateSubKey(path);
                foreach (var (name, value) in fields)
                    section.SetValue(name, value!, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
            using (var key = root.OpenSubKey(@"AppSettings\Logging\Activity", true)!) key.SetValue("RetentionDays", 12);
            using (var key = root.OpenSubKey(@"AppSettings\Logging\Registration", true)!) key.SetValue("RetentionDays", 78);
            var all = Read();
            check(all is { ManagesTenants: true, ManagesProfiles: true, Activity.RetentionDays: 12, Registration.RetentionDays: 78, EmbeddedTerminal: true },
                "Shipped ADMX values round-trip through real Windows subkeys, including identically named values in separate log sections");
            root.DeleteSubKeyTree(@"AppSettings\Terminal");
            check(Read() is { EmbeddedTerminal: null, Activity: not null, Registration: not null },
                "Removing only the terminal subkey leaves both log policies active");
            return all;
        }
        finally { hive.DeleteSubKeyTree(testPath, throwOnMissingSubKey: false); }
    }
}
