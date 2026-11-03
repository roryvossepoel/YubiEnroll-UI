using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

// Transfers named configuration only; never copies the CLI's authentication cache.
public static class ConfigTransfer
{
    private static readonly string[] ProviderKeys = ["provider", "client_id", "redirect_uri", "tenant_id", "entra_base_url", "graph_base_url", "profile"];
    private static readonly string[] ProfileKeys = ["reset", "min_pin_length", "require_always_uv", "require_ea", "force_pin_change", "random_pin", "random_pin_length"];

    public static string Export(string text)
    {
        var config = ConfigReader.Parse(text);
        var source = Toml.ToModel(text);
        var result = new TomlTable();
        if (config.Providers.Any(x => x.Name == config.ActiveProvider)) result["active_provider"] = config.ActiveProvider;
        foreach (var (key, allowed) in new[] { ("providers", ProviderKeys), ("profiles", ProfileKeys) })
        {
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
        return ValidateImport(Toml.FromModel(result));
    }

    public static string ValidateImport(string text)
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
                    if (!config.Profiles.Any(p => p.Name == Get("profile"))) throw new InvalidDataException("A tenant references a missing profile.");
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
