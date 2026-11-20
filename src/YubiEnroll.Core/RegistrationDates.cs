using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace YubiEnroll.Core;

public sealed record RegistrationDates(DateTimeOffset? Registered, DateTimeOffset? LastUsed);

// Keep optional display metadata out of the identity used to confirm and delete a registration.
public sealed record RegistrationCard(RegisteredCredential Credential, RegistrationDates? Dates)
{
    public string Name => Credential.Name;
    public string Model => Credential.Model;
    public string Id => Credential.Id;
    private static string LocalDate(DateTimeOffset? date) =>
        date?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "Unknown";
    public string DateLine => $"Registered: {LocalDate(Dates?.Registered)}   ·   Last used: {LocalDate(Dates?.LastUsed)}";
}

public static class RegistrationDateReader
{
    public static async Task<IReadOnlyDictionary<string, RegistrationDates>> ReadAsync(HttpClient client, Guid user,
        string bearer, CancellationToken cancellation)
    {
        if (user == Guid.Empty || string.IsNullOrWhiteSpace(bearer)) throw new ArgumentException("Missing Graph read context.");
        var endpoint = new Uri($"https://graph.microsoft.com/v1.0/users/{user:D}/authentication/fido2Methods");
        var url = endpoint;
        var dates = new Dictionary<string, RegistrationDates>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!seen.Add(url.AbsoluteUri) || seen.Count > 20) throw new InvalidDataException("Invalid registration pagination.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false));
            foreach (var method in json.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = method.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id) || !dates.TryAdd(id, new(ReadDate(method, "createdDateTime"), ReadDate(method, "lastUsedDateTime"))))
                    throw new InvalidDataException("Ambiguous Graph registration list.");
            }
            if (!json.RootElement.TryGetProperty("@odata.nextLink", out var next) || next.ValueKind == JsonValueKind.Null) return dates;
            if (!Uri.TryCreate(next.GetString(), UriKind.Absolute, out var nextUri) ||
                nextUri.Scheme != endpoint.Scheme || nextUri.Host != endpoint.Host || !nextUri.IsDefaultPort ||
                nextUri.UserInfo.Length != 0 || nextUri.Fragment.Length != 0 || nextUri.AbsolutePath != endpoint.AbsolutePath)
                throw new InvalidDataException("Unexpected Graph registrations URL.");
            url = nextUri;
        }
    }

    private static DateTimeOffset? ReadDate(JsonElement row, string property) =>
        row.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String &&
        field.TryGetDateTimeOffset(out var value) ? value : null;
}
