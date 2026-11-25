using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

public enum PolicySyncResult { Unmanaged, AppSettingsOnly, TomlCreated, TomlUpdated, TomlUnchanged, NoTomlNeeded }

// A machine policy snapshot, read once at startup. No registry or file access on the CLI side.
public sealed class DeskPolicy
{
    public const string RegistryPath = @"SOFTWARE\Policies\PasskeyDesk";
    public const int TenantSlots = 10;
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

    [SupportedOSPlatform("windows")]
    public static DeskPolicy? ReadRegistry(RegistryKey? root, string defaultActivity, string defaultRegistration)
    {
        IReadOnlyDictionary<string, object?>? ReadSection(string path)
        {
            using var section = root?.OpenSubKey(path, writable: false);
            if (section == null) return null;
            return section.GetValueNames().ToDictionary(name => name,
                name => section.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                StringComparer.OrdinalIgnoreCase);
        }
        return Read(ReadSection, defaultActivity, defaultRegistration);
    }

    private sealed class Section(string path, IReadOnlyDictionary<string, object?>? values)
    {
        private object? Get(string name) => values?.GetValueOrDefault(name);
        public bool HasAny(params string[] fields) => fields.Any(name => Get(name) != null);
        public int Number(string name, int min, int max) => Get(name) is int n && n >= min && n <= max ? n :
            throw new InvalidDataException($@"Policy field {path}\{name} must be a DWORD from {min} to {max}.");
        public bool Flag(string name) => Number(name, 0, 1) == 1;
        public string Required(string name) => Get(name) is string s && !string.IsNullOrWhiteSpace(s) ? s :
            throw new InvalidDataException($@"Policy field {path}\{name} must contain text.");
        public string Folder(string fallback)
        {
            var raw = Get("Folder");
            if (raw != null && raw is not string) throw new InvalidDataException($@"Policy field {path}\Folder must contain a folder path.");
            var folder = raw is string value && !string.IsNullOrWhiteSpace(value)
                ? Environment.ExpandEnvironmentVariables(value.Trim()) : fallback;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.Contains('%'))
                throw new InvalidDataException($@"Policy field {path}\Folder must resolve to an absolute folder for this user.");
            return Path.GetFullPath(folder);
        }
    }

    public static DeskPolicy? Read(Func<string, IReadOnlyDictionary<string, object?>?> readSection, string defaultActivity, string defaultRegistration)
    {
        Section Open(string path) => new(path, readSection(path));
        var tenantSection = Open(@"YubiEnroll\Tenants");
        var tenants = tenantSection.HasAny("TenantsJson")
            ? ParseTenants(tenantSection.Required("TenantsJson"))
            : ReadTenantSlots(Open);
        var profileSection = Open(@"YubiEnroll\Profiles");
        var profiles = profileSection.HasAny("ProfilesJson")
            ? ParseProfiles(profileSection.Required("ProfilesJson"))
            : ReadProfileSlots(Open);
        var activitySection = Open(@"AppSettings\Logging\Activity");
        ActivityPreferences? activity = activitySection.HasAny("Enabled", "Folder", "RetentionDays")
            ? new(activitySection.Flag("Enabled"), activitySection.Folder(defaultActivity), activitySection.Number("RetentionDays", 1, 3650)) : null;
        var registrationSection = Open(@"AppSettings\Logging\Registration");
        RegistrationLogPreferences? registration = registrationSection.HasAny("Enabled", "Folder", "SaveGeneratedPin", "RetentionDays")
            ? new(registrationSection.Flag("Enabled"), registrationSection.Folder(defaultRegistration), registrationSection.Flag("SaveGeneratedPin"),
                registrationSection.Number("RetentionDays", 1, RegistrationJournal.MaxRetentionDays)) : null;
        var terminalSection = Open(@"AppSettings\Terminal");
        bool? terminal = terminalSection.HasAny("EmbeddedTerminal") ? terminalSection.Flag("EmbeddedTerminal") : null;
        if (tenants == null && profiles == null && activity == null && registration == null && terminal == null) return null;
        return new(tenants, profiles, activity, registration, terminal);
    }

    private static TenantSetup[]? ReadTenantSlots(Func<string, Section> open)
    {
        var tenants = new List<TenantSetup>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= TenantSlots; i++)
        {
            var slot = open($@"YubiEnroll\Tenants\Tenant{i}");
            if (!slot.HasAny("Name", "TenantId", "ClientId", "RedirectUri")) continue;
            try
            {
                var tenant = new TenantSetup(slot.Required("Name"), slot.Required("TenantId"), slot.Required("ClientId"),
                    slot.Required("RedirectUri"), "", false).Validate();
                if (!names.Add(tenant.Name)) throw new ArgumentException("Duplicate tenant name.");
                tenants.Add(tenant);
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"Tenant slot {i}: {ex.Message}", ex); }
        }
        return tenants.Count == 0 ? null : tenants.ToArray();
    }

    private static ProfileSetup ReadProfileFields(Section slot, string context)
    {
        int? Length(string field)
        {
            var value = slot.Number(field, 0, 63);
            if (value is > 0 and < 4) throw new InvalidDataException($"{context}: {field} must be 0 or 4–63.");
            return value == 0 ? null : value;
        }
        try
        {
            return new ProfileSetup(slot.Required("Name"), slot.Flag("Reset"), Length("MinPinLength"),
                slot.Flag("AlwaysUv"), slot.Flag("RequireEa"), slot.Flag("ForcePinChange"),
                slot.Flag("RandomPin"), Length("RandomPinLength")).Validate();
        }
        catch (ArgumentException ex) { throw new InvalidDataException($"{context}: {ex.Message}", ex); }
    }

    private static ProfileSetup[]? ReadProfileSlots(Func<string, Section> open)
    {
        var profiles = new List<ProfileSetup>();
        for (var i = 1; i <= ProfileSlots; i++)
        {
            var slot = open($@"YubiEnroll\Profiles\Profile{i}");
            if (slot.HasAny(ProfileFields)) AddProfile(profiles, slot, $"Configuration profile {i}");
        }
        return profiles.Count == 0 ? null : profiles.ToArray();
    }

    private static void AddProfile(List<ProfileSetup> profiles, Section slot, string context)
    {
        var profile = ReadProfileFields(slot, context);
        if (profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{context}: duplicate profile name.");
        profiles.Add(profile);
    }

    private static ProfileSetup[] ParseProfiles(string json)
    {
        var profiles = new List<ProfileSetup>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ProfilesJson must be an array.");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var context = $"ProfilesJson item {profiles.Count + 1}";
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{context} must be an object.");
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in item.EnumerateObject())
                {
                    object? value = field.Name switch
                    {
                        "name" when field.Value.ValueKind == JsonValueKind.String => field.Value.GetString(),
                        "minPinLength" or "randomPinLength" when field.Value.ValueKind == JsonValueKind.Number &&
                            field.Value.TryGetInt32(out var n) => n,
                        "reset" or "alwaysUv" or "requireEa" or "forcePinChange" or "randomPin"
                            when field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False => field.Value.GetBoolean() ? 1 : 0,
                        _ => throw new InvalidDataException($"{context}: unexpected field or value for {field.Name}.")
                    };
                    if (!values.TryAdd(field.Name, value)) throw new InvalidDataException($"{context}: duplicate {field.Name}.");
                }
                AddProfile(profiles, new Section(context, values), context);
            }
        }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid ProfilesJson near line {(ex.LineNumber ?? 0) + 1}.", ex); }
        return profiles.ToArray();
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
        => ReadProfile(Toml.ToModel(text), name, policy);

    public static ProfileSetup ReadProfile(TomlTable model, string name, DeskPolicy? policy)
    {
        var stored = ProfileConfigWriter.Read(model, name);
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
