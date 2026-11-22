using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record LocalPasskey(
    [property:JsonPropertyName("id")] string Id, [property:JsonPropertyName("rp")] string Service,
    [property:JsonPropertyName("uid")] string UserId, [property:JsonPropertyName("name")] string Username,
    [property:JsonPropertyName("display")] string DisplayName)
{
    [JsonIgnore] public string Account => string.Join(" · ", new[] {Username, DisplayName}
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal)) is { Length: > 0 } s ? s : "Account name not provided";
}

public static class LocalPasskeys
{
    public static Task<IReadOnlyList<LocalPasskey>> ListAsync(CliRunner runner, ResetKey key, string pin) =>
        ExecuteAsync(runner, key, pin, null);
    public static Task<IReadOnlyList<LocalPasskey>> RemoveAsync(CliRunner runner, ResetKey key, string pin, LocalPasskey confirmed) =>
        ExecuteAsync(runner, key, pin, confirmed ?? throw new ArgumentNullException(nameof(confirmed)));

    private static async Task<IReadOnlyList<LocalPasskey>> ExecuteAsync(CliRunner runner, ResetKey key, string pin, LocalPasskey? confirmed)
    {
        _ = KeyResetService.Selector(key);
        if (string.IsNullOrEmpty(pin) || Encoding.UTF8.GetByteCount(pin) > 63 || pin.Contains('\0'))
            throw new ArgumentException("Enter the current FIDO PIN (up to 63 UTF-8 bytes).");
        var payload = JsonSerializer.Serialize(new { serial = key.Serial, reader = key.Reader,
            action = confirmed == null ? "list" : "remove", selected = confirmed, pin });
        // Only the embedded fixed code is ever written to disk. Keep it locked against replacement
        // while ykman opens it. The PIN and selected row travel only via redirected stdin.
        var path = Path.Combine(Path.GetTempPath(), $"YubiEnroll-{Guid.NewGuid():N}.py");
        using var source = typeof(LocalPasskeys).Assembly.GetManifestResourceStream("YubiEnroll.Core.Resources.LocalPasskeys.py")
            ?? throw new InvalidOperationException("The YubiKey passkey helper is unavailable.");
        using var script = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            await source.CopyToAsync(script); await script.FlushAsync();
            var response = await runner.CaptureWithInputAsync(TimeSpan.FromSeconds(45), payload, "script", "--force", path);
            if (response.ExitCode != 0) throw new InvalidOperationException(response.ExitCode switch
            {
                41 => "The selected YubiKey is missing or changed. Select it again.",
                42 => "This YubiKey does not support listing stored passkeys on this connection.",
                43 => "No FIDO PIN is set on this YubiKey.",
                44 => "Incorrect FIDO PIN. No retry was attempted.",
                45 => "PIN attempts are temporarily blocked. Reconnect the same YubiKey.",
                48 => "The FIDO PIN is blocked. No passkeys can be managed with this PIN.",
                49 => "This YubiKey requires a PIN change before passkeys can be managed.",
                46 => "The selected passkey changed. Unlock the list and review it again.",
                47 => "Removal could not be verified. Unlock the list to check; no deletion was retried.",
                _ => confirmed == null ? "Could not read the YubiKey. Reconnect it and close other key tools." : "Removal could not be verified. Unlock the list to check."
            });
            return Parse(response.Output, key);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException(confirmed == null ? "Could not verify the passkey list. Reconnect the key and try again." :
                "Removal outcome unknown. Unlock the list before trying again.");
        }
        finally
        {
            script.Dispose();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<LocalPasskey> Parse(string json, ResetKey key)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("serial").GetString() != key.Serial ||
            root.GetProperty("reader").GetString() != key.Reader) throw new InvalidDataException();
        var rows = new List<LocalPasskey>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("rows").EnumerateArray())
        {
            var row = item.Deserialize<LocalPasskey>() ?? throw new InvalidDataException();
            if (string.IsNullOrWhiteSpace(row.Service) || row.Username == null || row.DisplayName == null ||
                row.Id == null || !Regex.IsMatch(row.Id, @"\A(?:[0-9a-f]{2})+\z") || !ids.Add(row.Id) ||
                row.UserId == null || !Regex.IsMatch(row.UserId, @"\A(?:[0-9a-f]{2})*\z") ||
                new[] {row.Service, row.Username, row.DisplayName}.Any(s => s.Any(c => char.IsControl(c) ||
                    char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))) throw new InvalidDataException();
            rows.Add(row);
        }
        return rows;
    }
}
