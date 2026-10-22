using Tomlyn;
using Tomlyn.Model;

namespace YubiEnroll.Core;

public sealed record ProfileSetup(string Name, bool? Reset = false, int? MinPinLength = 6,
    bool? RequireAlwaysUv = false, bool? RequireEa = false, bool? ForcePinChange = false,
    bool? RandomPin = true, int? RandomPinLength = 6)
{
    public ProfileSetup Validate()
    {
        var name = Name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Profile name must contain 1–80 characters without control characters.");
        if (MinPinLength is < 4 or > 63 || RandomPinLength is < 4 or > 63)
            throw new ArgumentException("PIN lengths must be between 4 and 63, or empty for the CLI default.");
        if (RandomPin == true && MinPinLength.HasValue && RandomPinLength.HasValue && RandomPinLength < MinPinLength)
            throw new ArgumentException("Generated PIN length cannot be shorter than the minimum PIN length.");
        return this with { Name = name };
    }
}

public static class ProfileConfigWriter
{
    private static TomlTable Model(string? text)
    {
        ConfigReader.Parse(text ?? "");
        return Toml.ToModel(text ?? "");
    }

    private static TomlTable Profiles(TomlTable model)
    {
        if (!model.TryGetValue("profiles", out var value)) return new();
        return value as TomlTable ?? throw new InvalidDataException("Profiles must be a TOML table.");
    }

    public static ProfileSetup Read(string text, string name)
    {
        var profiles = Profiles(Model(text));
        if (!profiles.TryGetValue(name, out var value) || value is not TomlTable table)
            throw new InvalidOperationException("The selected profile no longer exists. Reload the configuration.");
        bool? Flag(string key) => !table.TryGetValue(key, out var v) ? null :
            v is bool b ? b : throw new InvalidDataException($"Profile setting {key} must be true or false.");
        int? Number(string key) => !table.TryGetValue(key, out var v) ? null :
            v is long n && n is >= 4 and <= 63 ? (int)n : throw new InvalidDataException($"Profile setting {key} must be an integer between 4 and 63.");
        return new(name, Flag("reset"), Number("min_pin_length"), Flag("require_always_uv"), Flag("require_ea"),
            Flag("force_pin_change"), Flag("random_pin"), Number("random_pin_length"));
    }

    public static string Build(string? existing, ProfileSetup request, string? editName = null)
    {
        var setup = request.Validate();
        var model = Model(existing);
        var profiles = Profiles(model);
        TomlTable profile;
        if (editName == null)
        {
            if (profiles.Keys.Any(x => x.Equals(setup.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A profile with this name already exists. Choose another name or edit that profile.");
            profile = new();
        }
        else
        {
            if (setup.Name != editName)
                throw new ArgumentException("An existing profile cannot be renamed because tenants may reference it.");
            if (!profiles.TryGetValue(editName, out var value) || value is not TomlTable table)
                throw new InvalidOperationException("The selected profile no longer exists. Reload the configuration.");
            profile = table; // Preserve unknown fields on the edited profile.
        }
        void Set(string key, object? value)
        {
            if (value == null) profile.Remove(key);
            else profile[key] = value;
        }
        Set("reset", setup.Reset);
        Set("min_pin_length", setup.MinPinLength.HasValue ? (long)setup.MinPinLength.Value : null);
        Set("require_always_uv", setup.RequireAlwaysUv);
        Set("require_ea", setup.RequireEa);
        Set("force_pin_change", setup.ForcePinChange);
        Set("random_pin", setup.RandomPin);
        Set("random_pin_length", setup.RandomPinLength.HasValue ? (long)setup.RandomPinLength.Value : null);
        profiles[setup.Name] = profile;
        model["profiles"] = profiles;
        // Serialize all values, including unknown provider endpoints and unrelated profile settings.
        // Formatting/comments can change; ConfigFileWriter keeps the original file as a backup.
        var result = Toml.FromModel(model);
        if (Read(result, setup.Name) != setup)
            throw new InvalidDataException("Saved profile settings did not match the requested values.");
        return result;
    }

    public static string? Save(ConfigSnapshot expected, ProfileSetup setup, string? editName = null) =>
        ConfigFileWriter.Save(expected, Build(expected.Text, setup, editName));
}
