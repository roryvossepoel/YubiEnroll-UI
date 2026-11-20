using System.Text.Json;

namespace YubiEnroll.Core;

public static class NfcDiscovery
{
    public static string Script
    {
        get
        {
            using var stream = typeof(NfcDiscovery).Assembly.GetManifestResourceStream("YubiEnroll.Core.Resources.DiscoverNfc.py")
                ?? throw new InvalidOperationException("The bundled NFC probe is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static KeyInventory Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static void Unique(JsonElement element)
        {
            if (element.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
                throw new InvalidDataException("Ambiguous NFC inventory.");
        }
        Unique(root);
        if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unknown NFC inventory format.");
        var keys = new List<ResetKey>();
        foreach (var item in root.GetProperty("keys").EnumerateArray())
        {
            Unique(item);
            var key = new ResetKey(item.GetProperty("serial").GetString()!, item.GetProperty("model").GetString()!, item.GetProperty("reader").GetString());
            if (!key.IsNfc || string.IsNullOrWhiteSpace(key.Model) || key.Model.Any(char.IsControl))
                throw new InvalidDataException("Incomplete NFC identity.");
            _ = KeyResetService.Selector(key);
            if (keys.Any(k => k.Reader!.Contains(key.Reader!, StringComparison.OrdinalIgnoreCase) || key.Reader!.Contains(k.Reader!, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Ambiguous NFC reader names.");
            keys.Add(key);
        }
        var unavailable = root.GetProperty("unavailable").GetInt32();
        var readers = root.GetProperty("readers").GetInt32();
        if (unavailable < 0 || readers < keys.Count) throw new InvalidDataException("Invalid NFC reader count.");
        return new(keys, 0, unavailable, readers);
    }

    public static async Task<KeyInventory> ReadAsync(CliRunner runner)
    {
        try
        {
            var result = await runner.CaptureWithInputAsync(TimeSpan.FromSeconds(15), Script, "script", "--force", "-");
            if (result.ExitCode != 0) return new([], 0, 1);
            return Parse(result.Output);
        }
        catch (Exception ex) when (ex is TimeoutException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // NFC failure must not remove working USB devices; no stale NFC identities survive.
            return new([], 0, 1);
        }
    }
}
