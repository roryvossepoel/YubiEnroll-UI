using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

// Transfers named configuration only; never copies the CLI's authentication cache.
public static class ConfigTransfer
{
    private static readonly string[] ProviderKeys = ["provider", "client_id", "redirect_uri", "tenant_id", "entra_base_url", "graph_base_url", "profile"];
    private static readonly string[] ProfileKeys = ["reset", "min_pin_length", "require_always_uv", "require_ea", "force_pin_change", "random_pin", "random_pin_length"];

    public static string Export(string text, DeskPolicy? policy = null)
    {
        var tenants = policy?.ManagesTenants != true;
        var profiles = policy?.ManagesProfiles != true;
        if (!tenants && !profiles) throw new InvalidOperationException("Tenants and profiles are managed by your organization.");
        var config = ConfigReader.Parse(text);
        var source = Toml.ToModel(text);
        var result = new TomlTable();
        if (tenants && config.Providers.Any(x => x.Name == config.ActiveProvider)) result["active_provider"] = config.ActiveProvider;
        foreach (var (key, allowed) in new[] { ("providers", ProviderKeys), ("profiles", ProfileKeys) })
        {
            if ((key == "providers" && !tenants) || (key == "profiles" && !profiles)) continue;
            var target = new TomlTable();
            if (source.TryGetValue(key, out var value) && value is TomlTable tables)
                foreach (var entry in tables)
                {
                    if (key == "providers" && !config.Providers.Any(x => x.Name == entry.Key)) continue;
                    if (entry.Value is not TomlTable table) throw new InvalidDataException("Configuration entry must be a table.");
                    var clean = new TomlTable();
                    foreach (var field in allowed) if (table.TryGetValue(field, out var v)) clean[field] = v;
                    target[entry.Key] = clean;
                }
            result[key] = target;
        }
        // Tenant-only files may refer to a managed profile by name, without exporting its settings.
        return ValidateImport(Toml.FromModel(result), profiles ? null : config.Profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
    }

    // A transfer can contain both lists. Under partial policy, build the new file from
    // the local snapshot; never copy the protected list or unrelated CLI settings from the import.
    public static string PrepareImport(string? existing, string incoming, DeskPolicy? policy)
    {
        bool tenantsManaged = policy?.ManagesTenants == true, profilesManaged = policy?.ManagesProfiles == true;
        if (!tenantsManaged && !profilesManaged) return ValidateImport(incoming);
        if (tenantsManaged && profilesManaged) throw new InvalidOperationException("Tenants and profiles are managed by your organization.");
        var local = ConfigReader.Parse(existing ?? "");
        var working = Toml.ToModel(existing ?? "");
        TomlTable transfer;
        try { transfer = Toml.ToModel(incoming); }
        catch { throw new InvalidDataException("Cannot parse the imported TOML configuration."); }
        var section = tenantsManaged ? "profiles" : "providers";
        if (!transfer.TryGetValue(section, out var raw) || raw is not TomlTable list || list.Count == 0)
            throw new InvalidDataException(tenantsManaged ? "No local profiles to import from this file." : "No local tenants to import from this file.");
        var subset = new TomlTable { [section] = list };
        if (working.TryGetValue("providers", out var items) && items is not TomlTable)
            throw new InvalidDataException("The existing providers section must be a table.");
        var providers = items as TomlTable ?? new();
        if (tenantsManaged)
        {
            ValidateImport(Toml.FromModel(subset));
            foreach (var t in providers.Values.OfType<TomlTable>())
                if (t.TryGetValue("profile", out var reference) && reference is string name && name.Length > 0 && !list.ContainsKey(name))
                    throw new InvalidDataException("A current provider uses a profile missing from the import.");
            working["profiles"] = list;
        }
        else
        {
            // Validate legacy profile links against this device's actual managed profile names.
            var known = new TomlTable();
            foreach (var profile in local.Profiles) known.Add(profile.Name, new TomlTable());
            subset["profiles"] = known;
            if (transfer.TryGetValue("active_provider", out var requested)) subset["active_provider"] = requested;
            ValidateImport(Toml.FromModel(subset));
            foreach (var p in local.Providers) providers.Remove(p.Name);
            var reserved = providers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list)
            {
                if (reserved.Contains(entry.Key)) throw new InvalidDataException("An imported tenant conflicts with a non-Entra provider.");
                providers.Add(entry.Key, entry.Value);
            }
            working["providers"] = providers;
            if (requested is string active) working["active_provider"] = active;
            else if (!providers.ContainsKey(local.ActiveProvider)) working["active_provider"] = list.Keys.First();
        }
        return Toml.FromModel(working);
    }

    public static string ValidateImport(string text) => ValidateImport(text, null);

    private static string ValidateImport(string text, HashSet<string>? managedProfiles)
    {
        var config = ConfigReader.Parse(text);
        var model = Toml.ToModel(text);
        if (model.Keys.Any(x => x is not "active_provider" and not "providers" and not "profiles"))
            throw new InvalidDataException("Import supports tenants, profiles and active_provider only. Remove other top-level settings first.");
        foreach (var (key, allowed) in new[] { ("providers", ProviderKeys), ("profiles", ProfileKeys) })
        {
            if (!model.TryGetValue(key, out var v)) continue;
            if (v is not TomlTable tables) throw new InvalidDataException("Providers and profiles must be tables.");
            if (tables.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tables.Count)
                throw new InvalidDataException("Duplicate names differing only by letter case are not supported.");
            foreach (var entry in tables)
            {
                if (entry.Value is not TomlTable table || table.Keys.Any(x => !allowed.Contains(x)))
                    throw new InvalidDataException("Import contains unsupported settings. Only public Entra tenant and enrollment profile fields are accepted.");
                if (key == "profiles") ProfileConfigWriter.Read(text, entry.Key).Validate();
                else
                {
                    string Get(string k) => table.TryGetValue(k, out var value) && value is string s ? s : "";
                    if (Get("provider") != "ENTRA") throw new InvalidDataException("Only ENTRA tenants can be imported.");
                    new TenantSetup(entry.Key, Get("tenant_id"), Get("client_id"), Get("redirect_uri"), Get("profile"), false).Validate();
                    if (Get("profile").Length > 0 && !config.Profiles.Any(p => p.Name == Get("profile")) && managedProfiles?.Contains(Get("profile")) != true) throw new InvalidDataException("A tenant references a missing profile.");
                    foreach (var endpoint in new[] { "entra_base_url", "graph_base_url" })
                        if (table.ContainsKey(endpoint) && (!Uri.TryCreate(Get(endpoint), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0))
                            throw new InvalidDataException("Cloud endpoints must be absolute HTTPS URLs without credentials.");
                }
            }
        }
        if (model.ContainsKey("active_provider") && !config.Providers.Any(x => x.Name == config.ActiveProvider))
            throw new InvalidDataException("Active provider must refer to an imported tenant.");
        if (config.Providers.Count == 0 && config.Profiles.Count == 0) throw new InvalidDataException("No tenants or profiles found.");
        return text;
    }
}
