using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

public sealed record TenantSetup(string Name, string TenantId, string ClientId, string RedirectUri,
    string ProfileName, bool CreateProfile, int PinLength = 6, bool ForcePinChange = false)
{
    public TenantSetup Validate()
    {
        static string NameValue(string value, string label)
        {
            value = value.Trim();
            if (value.Length is < 1 or > 80 || value.Any(char.IsControl))
                throw new ArgumentException($"{label} must contain 1–80 characters without control characters.");
            return value;
        }
        static string IdValue(string value, string label) =>
            Guid.TryParse(value.Trim(), out var id) && id != Guid.Empty ? id.ToString() :
                throw new ArgumentException($"Enter a valid {label} (GUID).");
        var redirect = RedirectUri.Trim();
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new ArgumentException("Redirect URI must use http://localhost and match the Entra app registration, for example http://localhost/yubienroll-redirect.");
        if (CreateProfile && PinLength is < 4 or > 63)
            throw new ArgumentException("PIN length must be between 4 and 63.");
        return this with { Name = NameValue(Name, "Tenant name"), TenantId = IdValue(TenantId, "Tenant ID"),
            ClientId = IdValue(ClientId, "Client ID"), RedirectUri = redirect, ProfileName = NameValue(ProfileName, "Profile name") };
    }
}

public sealed record ConfigSnapshot(string Path, string? Text)
{
    public static ConfigSnapshot Read(string path)
    {
        try { return new(path, File.ReadAllText(path)); }
        catch (FileNotFoundException) { return new(path, null); }
        catch (DirectoryNotFoundException) { return new(path, null); }
    }
}

public static class TenantConfigWriter
{
    // Append only: keep all original comments, other providers, profiles and unknown settings verbatim.
    public static string Build(string? existing, TenantSetup request)
    {
        var setup = request.Validate();
        var source = existing ?? "";
        var parsed = ConfigReader.Parse(source); // Wrap parser diagnostics so config values cannot leak.
        var model = Toml.ToModel(source);
        static TomlTable Table(TomlTable root, string key)
        {
            if (!root.TryGetValue(key, out var value)) return new();
            return value as TomlTable ?? throw new InvalidDataException($"The {key} configuration must be a TOML table.");
        }
        var providers = Table(model, "providers");
        var profiles = Table(model, "profiles");
        if (providers.Keys.Any(x => x.Equals(setup.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A provider with this name already exists. Choose a different tenant name.");
        if (setup.CreateProfile && profiles.Keys.Any(x => x.Equals(setup.ProfileName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A profile with this name already exists. Select that profile or choose another name.");
        if (!setup.CreateProfile && !parsed.Profiles.Any(x => x.Name == setup.ProfileName))
            throw new ArgumentException("The selected profile no longer exists. Reopen Add tenant and select a profile.");
        var addition = new TomlTable();
        var provider = new TomlTable
        {
            ["provider"] = "ENTRA", ["tenant_id"] = setup.TenantId, ["client_id"] = setup.ClientId,
            ["redirect_uri"] = setup.RedirectUri, ["profile"] = setup.ProfileName
        };
        addition["providers"] = new TomlTable { [setup.Name] = provider };
        if (setup.CreateProfile)
            addition["profiles"] = new TomlTable { [setup.ProfileName] = new TomlTable
            {
                ["reset"] = false, ["min_pin_length"] = (long)setup.PinLength,
                ["random_pin"] = true, ["random_pin_length"] = (long)setup.PinLength,
                ["force_pin_change"] = setup.ForcePinChange, ["require_always_uv"] = false,
                ["require_ea"] = false
            }};
        // A brand-new file gets an active provider. Existing activation is never overwritten.
        var prefix = existing == null || source.Length == 0 ? Toml.FromModel(new TomlTable { ["active_provider"] = setup.Name }) : "";
        // Tomlyn emits explicit parent table headers. Appending those again would redefine
        // [providers]/[profiles] when the existing file already declares them.
        var fragment = string.Join(Environment.NewLine, Toml.FromModel(addition).Split('\n')
            .Where(line => line.Trim() is not "[providers]" and not "[profiles]")
            .Select(line => line.TrimEnd('\r')));
        var result = prefix + source + Environment.NewLine + fragment;
        var verified = ConfigReader.Parse(result);
        if (!verified.Providers.Any(x => x.Name == setup.Name && x.TenantId == setup.TenantId))
            throw new InvalidDataException("The new tenant configuration could not be validated.");
        return result;
    }

    public static TenantSetup Read(string text, string name)
    {
        ConfigReader.Parse(text);
        var model = Toml.ToModel(text);
        if (!model.TryGetValue("providers", out var p) || p is not TomlTable providers ||
            !providers.TryGetValue(name, out var t) || t is not TomlTable table)
            throw new InvalidOperationException("Tenant no longer exists. Reopen Settings.");
        string Value(string key) => table.TryGetValue(key, out var value) && value is string v ? v : "";
        return new(name, Value("tenant_id"), Value("client_id"), Value("redirect_uri"), Value("profile"), false);
    }

    public static string Edit(string text, TenantSetup request, string name)
    {
        var setup = request.Validate();
        Read(text, name);
        if (setup.Name != name) throw new ArgumentException("Existing tenants cannot be renamed in this editor.");
        var model = Toml.ToModel(text);
        var profiles = model.TryGetValue("profiles", out var p) && p is TomlTable pt ? pt : new TomlTable();
        if (setup.CreateProfile)
        {
            if (profiles.Keys.Any(x => x.Equals(setup.ProfileName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A profile with this name already exists.");
            var addition = Toml.ToModel(Build(null, setup));
            profiles[setup.ProfileName] = ((TomlTable)addition["profiles"])[setup.ProfileName];
            model["profiles"] = profiles;
        }
        else if (!profiles.ContainsKey(setup.ProfileName)) throw new ArgumentException("Choose an existing profile.");
        var tenant = (TomlTable)((TomlTable)model["providers"])[name];
        tenant["tenant_id"] = setup.TenantId;
        tenant["client_id"] = setup.ClientId;
        tenant["redirect_uri"] = setup.RedirectUri;
        tenant["profile"] = setup.ProfileName;
        return Toml.FromModel(model);
    }

    public static string? Save(ConfigSnapshot expected, TenantSetup setup, string? editName = null) =>
        ConfigFileWriter.Save(expected, editName == null ? Build(expected.Text, setup) : Edit(expected.Text ?? "", setup, editName));
}

public static class ConfigFileWriter
{
    public static string? Save(ConfigSnapshot expected, string result)
    {
        ConfigReader.Parse(result);
        var path = System.IO.Path.GetFullPath(expected.Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Cooperating GUI writers cannot race; external CLI sessions must still be closed.
        using var writeLock = new FileStream(path + ".ui.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ConfigSnapshot.Read(path);
        if (!string.Equals(current.Text, expected.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Configuration changed while this window was open. Cancel and reopen it before saving.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string? backup = null;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(result);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (expected.Text == null)
                File.Move(temporary, path); // Refuses to replace a file created in the meantime.
            else
            {
                backup = path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N") + ".bak";
                File.Replace(temporary, path, backup);
            }
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
