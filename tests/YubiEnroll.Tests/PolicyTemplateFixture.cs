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
        var values = new Dictionary<string, object>();
        var policies = defs.Descendants(ns + "policy").ToArray();
        foreach (var p in policies)
        {
            if (A(p,"class") != "Machine" || !A(p,"key").Equals(DeskPolicy.RegistryPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected registry location");
            var form = forms[A(p,"presentation")[15..^1]].Elements().ToDictionary(x=>A(x,"refId"));
            var elements = p.Element(ns + "elements")!.Elements().ToArray();
            if (form.Count != elements.Length) throw new InvalidDataException("Unmatched ADML controls");
            var scoped = new Dictionary<string,object>();
            foreach (var f in elements)
            {
                var field = A(f,"valueName"); var input = form[A(f,"id")];
                switch (f.Name.LocalName)
                {
                    case "enum":
                        var selected = f.Elements(ns+"item").ElementAt((int)input.Attribute("defaultItem")!);
                        scoped.Add(field, (int)selected.Descendants(ns+"decimal").Single().Attribute("value")!); break;
                    case "decimal": scoped.Add(field, (int)input.Attribute("defaultValue")!); break;
                    case "text" when field == "TenantsJson": scoped.Add(field, File.ReadAllText(Path.Combine(folder,"tenants.example.json"))); break;
                    case "text" when field.EndsWith("Name"): scoped.Add(field,A(p,"name")); break;
                    case "text" when A(f,"required") == "false": break;
                    default: throw new InvalidDataException("Unexpected template field " + field);
                }
            }
            var dir = Path.GetDirectoryName(path)!;
            var single = DeskPolicy.Read(k => scoped.GetValueOrDefault(k),dir,dir);
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
            foreach (var kv in scoped) values.Add(kv.Key,kv.Value);
        }
        check(policies.Length == 9, "Only concrete settings in template; no master switch");
        string target = Path.GetDirectoryName(path)!;
        DeskPolicy.Synchronize(DeskPolicy.Read(k => values.GetValueOrDefault(k),target,target),()=>path);
        var result = ConfigReader.Read(path);
        check(result.Providers.Single().Name == "Pilot tenant" && result.Profiles.Count == 5,
            "All template defaults and the example JSON round-trip to the standard CLI format");
    }
}
