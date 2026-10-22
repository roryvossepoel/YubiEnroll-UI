using System.Security.Cryptography;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

public sealed record Provider(string Name, string Type, string TenantId, string Profile, string Fingerprint);
public sealed record EnrollmentProfile(string Name, string Summary, string Fingerprint);
public sealed record Configuration(string Path, string ActiveProvider, IReadOnlyList<Provider> Providers,
    IReadOnlyList<EnrollmentProfile> Profiles);

public static class ConfigReader
{
    public static string FindPath(string? appDataOverride = null)
    {
        var appData = appDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] paths = [System.IO.Path.Combine(appData, "Yubico", "yubienroll", "yubienroll.toml"),
            System.IO.Path.Combine(appData, "yubienroll", "yubienroll.toml")];
        return paths.FirstOrDefault(File.Exists) ?? paths[1];
    }

    public static Configuration Read(string path) => Parse(File.ReadAllText(path), path);

    public static Configuration Parse(string text, string path = "")
    {
        // Never include parser diagnostics: a config can contain sensitive values.
        TomlTable model;
        try { model = Toml.ToModel(text); }
        catch { throw new InvalidDataException("Cannot parse YubiEnroll TOML configuration."); }
        var providers = new List<Provider>();
        var profiles = new List<EnrollmentProfile>();
        if (model.TryGetValue("providers", out var p) && p is TomlTable pt)
            foreach (var entry in pt)
                if (entry.Value is TomlTable table)
                {
                    var type = Value(table, "provider");
                    if (type.Equals("ENTRA", StringComparison.OrdinalIgnoreCase))
                    {
                        var tenant = Value(table, "tenant_id");
                        if (!Guid.TryParse(tenant, out _))
                            throw new InvalidDataException("An Entra provider has an invalid tenant_id.");
                        providers.Add(new(entry.Key, type, tenant, Value(table, "profile"), Hash(table)));
                    }
                }
        if (model.TryGetValue("profiles", out var q) && q is TomlTable qt)
            foreach (var entry in qt)
                if (entry.Value is TomlTable table)
                {
                    string[] keys = ["min_pin_length", "require_always_uv", "require_ea",
                        "force_pin_change", "random_pin", "random_pin_length", "reset"];
                    var summary = string.Join(Environment.NewLine,
                        keys.Select(key => $"{key}: {Value(table, key, "CLI default")}"));
                    profiles.Add(new(entry.Key, summary, Hash(table)));
                }
        return new(path, Value(model, "active_provider"), providers, profiles);
    }

    private static string Value(TomlTable table, string key, string fallback = "") =>
        table.TryGetValue(key, out var value) ? Convert.ToString(value,
            System.Globalization.CultureInfo.InvariantCulture) ?? fallback : fallback;

    private static string Hash(TomlTable table) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Toml.FromModel(table))));

    public static void EnsureUnchanged(Configuration current, Provider provider, EnrollmentProfile? profile)
    {
        if (!current.Providers.Any(x => x.Name == provider.Name && x.Fingerprint == provider.Fingerprint) ||
            (profile != null && !current.Profiles.Any(x => x.Name == profile.Name && x.Fingerprint == profile.Fingerprint)))
            throw new InvalidOperationException("Configuration changed. Reload and select the tenant, profile and user again.");
    }
}
