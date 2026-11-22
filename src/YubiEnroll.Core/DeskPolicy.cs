using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

public enum PolicySyncResult { Unmanaged, AppSettingsOnly, TomlCreated, TomlUpdated, TomlUnchanged, NoTomlNeeded }

// A machine policy snapshot, read once at startup. No registry or file access on the CLI side.
public sealed class DeskPolicy
{
    public const string RegistryPath = @"SOFTWARE\Policies\PasskeyDesk";
    public const int ProfileSlots = 5;
    public ActivityPreferences? Activity { get; }
    public RegistrationLogPreferences? Registration { get; }
    public bool? EmbeddedTerminal { get; }
    public bool ManagesTenants => _tenants != null;   // An explicit [] is a managed empty list.
    public bool ManagesProfiles => _profiles != null;
    private readonly TenantSetup[]? _tenants;
    private readonly ProfileSetup[]? _profiles;
    private static readonly string[] ProfileFields = ["Name", "Reset", "MinPinLength", "AlwaysUv", "RequireEa", "ForcePinChange", "RandomPin", "RandomPinLength"];

    private DeskPolicy(TenantSetup[]? tenants, ProfileSetup[]? profiles, ActivityPreferences? activity,
        RegistrationLogPreferences? registration, bool? embedded) =>
        (_tenants, _profiles, Activity, Registration, EmbeddedTerminal) = (tenants, profiles, activity, registration, embedded);

    public static DeskPolicy? Read(Func<string, object?> read, string defaultActivity, string defaultRegistration)
    {
        var values = new Dictionary<string, object?>();
        object? get(string key) => values.TryGetValue(key, out var v) ? v : values[key] = read(key);
        int Number(string name, int min, int max) => get(name) is int n && n >= min && n <= max ? n :
            throw new InvalidDataException($"Policy field {name} must be a DWORD from {min} to {max}.");
        bool Flag(string name) => Number(name, 0, 1) == 1;
        string Required(string name) => get(name) is string s && !string.IsNullOrWhiteSpace(s) ? s :
            throw new InvalidDataException($"Policy field {name} must contain text.");
        string Folder(string name, string fallback)
        {
            var raw = get(name);
            if (raw != null && raw is not string) throw new InvalidDataException($"Policy field {name} must contain a folder path.");
            var folder = raw is string value && !string.IsNullOrWhiteSpace(value)
                ? Environment.ExpandEnvironmentVariables(value.Trim()) : fallback;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.Contains('%'))
                throw new InvalidDataException($"Policy field {name} must resolve to an absolute folder for this user.");
            return Path.GetFullPath(folder);
        }
        var tenants = get("TenantsJson") == null ? null : ParseTenants(Required("TenantsJson"));
        var profiles = new List<ProfileSetup>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= ProfileSlots; i++)
        {
            var prefix = $"Profile{i}";
            if (!ProfileFields.Any(field => get(prefix + field) != null)) continue;
            int? PinLength(string field)
            {
                var value = Number(prefix + field, 0, 63);
                if (value is > 0 and < 4) throw new InvalidDataException($"{prefix}{field} must be 0 (CLI default) or 4–63.");
                return value == 0 ? null : value;
            }
            ProfileSetup p;
            try
            {
                p = new ProfileSetup(Required(prefix + "Name"), Flag(prefix + "Reset"), PinLength("MinPinLength"),
                    Flag(prefix + "AlwaysUv"), Flag(prefix + "RequireEa"), Flag(prefix + "ForcePinChange"),
                    Flag(prefix + "RandomPin"), PinLength("RandomPinLength")).Validate();
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"Configuration profile {i}: {ex.Message}", ex); }
            if (!names.Add(p.Name)) throw new InvalidDataException($"Configuration profile {i} has a duplicate name.");
            profiles.Add(p);
        }
        ActivityPreferences? activity = get("ActivityLogging") != null || get("ActivityFolder") != null || get("RetentionDays") != null
            ? new(Flag("ActivityLogging"), Folder("ActivityFolder", defaultActivity), Number("RetentionDays", 1, 3650)) : null;
        RegistrationLogPreferences? registration = get("RegistrationLogging") != null || get("RegistrationFolder") != null || get("SaveGeneratedPin") != null
            ? new(Flag("RegistrationLogging"), Folder("RegistrationFolder", defaultRegistration), Flag("SaveGeneratedPin")) : null;
        bool? terminal = get("EmbeddedTerminal") != null ? Flag("EmbeddedTerminal") : null;
        if (tenants == null && profiles.Count == 0 && activity == null && registration == null && terminal == null) return null;
        return new(tenants, profiles.Count == 0 ? null : profiles.ToArray(), activity, registration, terminal);
    }

    private static TenantSetup[] ParseTenants(string json)
    {
        var tenants = new List<TenantSetup>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("TenantsJson must be an array.");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                int row = tenants.Count + 1;
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"TenantsJson item {row} must be an object.");
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in item.EnumerateObject())
                    if (prop.Name is not ("name" or "tenantId" or "clientId" or "redirectUri") ||
                        prop.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(prop.Name, prop.Value.GetString()!))
                        throw new InvalidDataException($"TenantsJson item {row}: use exactly name, tenantId, clientId and redirectUri as strings.");
                if (fields.Count != 4) throw new InvalidDataException($"TenantsJson item {row} is missing one of its four fields.");
                TenantSetup tenant;
                try { tenant = new TenantSetup(fields["name"], fields["tenantId"], fields["clientId"], fields["redirectUri"], "", false).Validate(); }
                catch (ArgumentException ex) { throw new InvalidDataException($"TenantsJson item {row}: {ex.Message}", ex); }
                if (!names.Add(tenant.Name)) throw new InvalidDataException($"TenantsJson item {row} has a duplicate tenant name.");
                tenants.Add(tenant);
            }
        }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid TenantsJson near line {(ex.LineNumber ?? 0) + 1}.", ex); }
        return tenants.ToArray();
    }

    public void CheckProfileRenameOrRemoval(ConfigSnapshot snapshot, string? oldName, string? newName)
    {
        if (ManagesProfiles) throw new InvalidOperationException("Configuration profiles are managed by your organization.");
        // The native profile writer also rewrites legacy provider references on rename/remove.
        if (ManagesTenants && oldName != null && oldName != newName &&
            ConfigReader.Parse(snapshot.Text ?? "").Providers.Any(p => p.Profile == oldName))
            throw new InvalidOperationException("A managed tenant references this profile. Its name must be kept.");
    }

    // YubiEnroll 1.2.0 indexes both lengths even when listing profiles. Give the
    // standalone CLI concrete defaults; Passkey Desk can use the actual key's floor.
    private static ProfileSetup CliProfile(ProfileSetup p) => p with
    {
        MinPinLength = p.MinPinLength ?? 4,
        RandomPinLength = p.RandomPinLength ?? p.MinPinLength ?? 4
    };

    public static ProfileSetup ReadProfile(string text, string name, DeskPolicy? policy)
    {
        var stored = ProfileConfigWriter.Read(text, name);
        var managed = policy?._profiles?.FirstOrDefault(p => p.Name == name);
        if (managed == null) return stored;
        if (stored != CliProfile(managed))
            throw new InvalidDataException("This managed profile changed since startup. Restart Passkey Desk to reapply policy.");
        return stored with { MinPinLength = managed.MinPinLength };
    }

    public static PolicySyncResult Synchronize(DeskPolicy? policy, Func<string> resolvePath)
    {
        if (policy == null) return PolicySyncResult.Unmanaged;
        if (!policy.ManagesTenants && !policy.ManagesProfiles) return PolicySyncResult.AppSettingsOnly;
        var snapshot = ConfigSnapshot.Read(resolvePath());
        var result = policy.Render(snapshot.Text ?? "");
        if (result == null) return snapshot.Text == null ? PolicySyncResult.NoTomlNeeded : PolicySyncResult.TomlUnchanged;
        ConfigFileWriter.Save(snapshot, result);
        return snapshot.Text == null ? PolicySyncResult.TomlCreated : PolicySyncResult.TomlUpdated;
    }

    private string? Render(string source)
    {
        var current = ConfigReader.Parse(source);
        var root = Toml.ToModel(source);
        TomlTable Table(string key) => !root.TryGetValue(key, out var value) ? new() :
            value as TomlTable ?? throw new InvalidDataException($"Existing TOML section {key} is not a table.");
        var providers = Table("providers");
        var profiles = Table("profiles");
        var desiredProviders = new TomlTable();
        var desiredProfiles = _profiles == null ? profiles : new TomlTable();
        var managedNames = _tenants?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entraNames = current.Providers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in providers)
        {
            bool entra = entraNames.Contains(entry.Key);
            if (entra && _tenants != null) continue;
            if (managedNames?.Contains(entry.Key) == true) throw new InvalidDataException("A managed tenant name conflicts with an existing non-Entra provider.");
            if (_profiles != null && entry.Value is TomlTable t && t.TryGetValue("profile", out var reference) && reference is string p &&
                p.Length > 0 && !_profiles.Any(x => x.Name == p))
            {
                if (!entra) throw new InvalidDataException("A non-Entra provider references a profile outside policy. Resolve the reference before applying policy.");
                // Remove only the obsolete default profile link from an otherwise unmanaged Entra tenant.
                var clone = new TomlTable();
                foreach (var field in t.Where(k => k.Key != "profile")) clone.Add(field.Key, field.Value);
                desiredProviders.Add(entry.Key, clone);
            }
            else desiredProviders.Add(entry.Key, entry.Value);
        }
        foreach (var t in _tenants ?? [])
            desiredProviders.Add(t.Name, new TomlTable { ["provider"] = "ENTRA", ["tenant_id"] = t.TenantId,
                ["client_id"] = t.ClientId, ["redirect_uri"] = t.RedirectUri });
        foreach (var p in _profiles ?? [])
        {
            var single = Toml.ToModel(ProfileConfigWriter.Build(null, CliProfile(p)));
            desiredProfiles.Add(p.Name, ((TomlTable)single["profiles"])[p.Name]);
        }
        static bool Same(object a, object b) =>
            ReferenceEquals(a,b) || (a is TomlTable x && b is TomlTable y
                ? x.Count == y.Count && x.All(k => y.TryGetValue(k.Key, out var v) && Same(k.Value,v))
                : Equals(a,b));
        bool adjustActive = _tenants != null && !desiredProviders.ContainsKey(current.ActiveProvider)
            && (_tenants.Length > 0 || root.ContainsKey("active_provider"));
        bool providerChange = !Same(providers, desiredProviders), profileChange = !Same(profiles, desiredProfiles);
        if (!adjustActive && !providerChange && !profileChange) return null;
        if (providerChange) root["providers"] = desiredProviders;
        if (profileChange) root["profiles"] = desiredProfiles;
        if (adjustActive)
        {
            if (_tenants!.Length > 0) root["active_provider"] = _tenants[0].Name;
            else root.Remove("active_provider");
        }
        return Toml.FromModel(root);
    }
}
