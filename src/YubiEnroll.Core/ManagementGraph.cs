using System.Net;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

// The token source is bound to the tenant and account selected at Connect. It must never open a browser.
public sealed class ManagementGraph(HttpClient http, Func<CancellationToken, Task<string>> silentToken)
{
    private const string Users = "https://graph.microsoft.com/v1.0/users";
    public const int SearchLimit = 50;

    public async Task<IReadOnlyList<UserCandidate>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 200 || query.Any(char.IsControl))
            throw new ArgumentException("Enter between 2 and 200 characters to search.");
        var term = query.Replace("'", "''");
        var filter = string.Join(" or ", new[] { "displayName", "givenName", "surname", "userPrincipalName", "mail" }
            .Select(field => $"startswith({field},'{term}')"));
        var first = new Uri($"{Users}?$select=id,displayName,userPrincipalName,mail&$top={SearchLimit}&$filter={Uri.EscapeDataString(filter)}");
        var found = new Dictionary<Guid, UserCandidate>();
        var token = await silentToken(ct);
        await ReadPages(first, token, ct, row =>
        {
            if (!Guid.TryParse(Text(row, "id"), out var id) || id == Guid.Empty)
                throw new InvalidDataException("Graph returned a user without a valid object ID.");
            var name = Text(row, "displayName"); var upn = Text(row, "userPrincipalName"); var mail = Text(row, "mail");
            var details = string.Join("  ", new[]{name, upn, mail}.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            found.TryAdd(id, new(id.ToString(), details.Length == 0 ? "Unnamed user" : details)
                { DisplayName = name, UserPrincipalName = upn, Email = mail });
            return found.Count < SearchLimit;
        });
        return found.Values.ToArray();
    }

    public async Task<IReadOnlyList<RegistrationCard>> ListAsync(Guid user, CancellationToken ct)
    {
        var endpoint = Methods(user);
        return await ReadMethods(endpoint, await silentToken(ct), ct);
    }

    public async IAsyncEnumerable<TenantSearchBatch> ScanTenantAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var users = new Dictionary<Guid, UserCandidate>();
        var first = new Uri($"{Users}?$select=id,displayName,userPrincipalName&$top=999");
        await ReadPages(first, await silentToken(ct).ConfigureAwait(false), ct, row =>
        {
            if (!Guid.TryParse(Text(row, "id"), out var id) || id == Guid.Empty)
                throw new InvalidDataException("A directory user has no valid ID. Refresh the tenant search.");
            users.TryAdd(id, new(id.ToString(), Text(row, "displayName")) { UserPrincipalName = Text(row, "userPrincipalName") });
            return true;
        }).ConfigureAwait(false);
        var completed = 0;
        var unavailable = 0;
        yield return new(users.Count, completed, unavailable, []);
        foreach (var group in users.Chunk(4))
        {
            ct.ThrowIfCancellationRequested();
            var token = await silentToken(ct).ConfigureAwait(false);
            async Task<TenantKeyRow[]?> ReadUser(KeyValuePair<Guid, UserCandidate> user)
            {
                try
                {
                    var cards = await ReadMethods(Methods(user.Key), token, ct).ConfigureAwait(false);
                    return cards.Select(card => new TenantKeyRow(user.Value.Details, user.Value.UserPrincipalName, card.Credential)).ToArray();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    return null;
                }
            }
            var results = await Task.WhenAll(group.Select(ReadUser)).ConfigureAwait(false);
            completed += group.Length;
            unavailable += results.Count(rows => rows == null);
            yield return new(users.Count, completed, unavailable, results.OfType<TenantKeyRow[]>().SelectMany(rows => rows).ToArray());
        }
    }

    public async Task<IReadOnlyList<RegistrationCard>> RemoveAsync(Guid user, RegisteredCredential target, CancellationToken ct, Action? refreshing = null)
    {
        var endpoint = Methods(user);
        if (!target.IsYubicoHardware || !Regex.IsMatch(target.Id, @"\A[A-Za-z0-9_+-]+={0,2}\z"))
            throw new InvalidOperationException("Select a recognized YubiKey with a valid registration ID.");
        var token = await silentToken(ct);
        var before = await ReadMethods(endpoint, token, ct); // includes hidden methods and all pages
        if (!before.Any(r => r.Credential == target))
            throw new InvalidOperationException("The selected registration changed. Refresh and review it again.");

        using var request = Request(HttpMethod.Delete, new Uri(endpoint.AbsoluteUri + "/" + Uri.EscapeDataString(target.Id)), token);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new InvalidOperationException("Removal could not be verified. Refresh before trying again.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException { StatusCode: null })
        { throw new InvalidOperationException("Removal outcome unknown. Refresh before trying again.", ex); }

        IReadOnlyList<RegistrationCard> after;
        refreshing?.Invoke();
        try
        {
            after = await ReadMethods(endpoint, token, ct);
            foreach (var delay in new[] { 350, 750, 1500 })
            {
                if (!after.Any(c => c.Id == target.Id)) break;
                await Task.Delay(delay, ct);
                after = await ReadMethods(endpoint, token, ct);
            }
        }
        catch (Exception ex) { throw new InvalidOperationException("The removal was submitted, but the updated list could not be verified. Refresh before trying again.", ex); }
        var remaining = after.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (remaining.Contains(target.Id)) throw new InvalidOperationException("Removal has not been verified. Refresh to check the registration.");
        if (before.Any(c => c.Id != target.Id && !remaining.Contains(c.Id)))
            throw new InvalidOperationException("Another registration is also missing. Check this user's registrations in Entra before continuing.");
        return after;
    }

    private static Uri Methods(Guid user) => user == Guid.Empty ? throw new ArgumentException("Select a valid Entra user.") :
        new($"{Users}/{user:D}/authentication/fido2Methods");

    private async Task<IReadOnlyList<RegistrationCard>> ReadMethods(Uri endpoint, string token, CancellationToken ct)
    {
        var rows = new Dictionary<string, RegistrationCard>(StringComparer.Ordinal);
        await ReadPages(endpoint, token, ct, item =>
        {
            var id = Text(item, "id"); var name = Text(item, "displayName"); var model = Text(item, "model");
            if (string.IsNullOrWhiteSpace(id) || !rows.TryAdd(id, new(new(name.Length == 0 ? "(Unnamed passkey)" : name, model, id),
                    new(Date(item, "createdDateTime"), Date(item, "lastUsedDateTime")))))
                throw new InvalidDataException("The registration list contains a missing or duplicate ID. Refresh before continuing.");
            return true;
        });
        return rows.Values.ToArray();
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri url, string token) =>
        new(method, url) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    private async Task ReadPages(Uri first, string token, CancellationToken ct, Func<JsonElement, bool> visit)
    {
        var url = first;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!seen.Add(url.AbsoluteUri)) throw new InvalidDataException("Invalid Graph pagination.");
            using var response = await GetPage(url, token, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray()) if (!visit(item)) return;
            if (!json.RootElement.TryGetProperty("@odata.nextLink", out var next) || next.ValueKind == JsonValueKind.Null) return;
            if (!Uri.TryCreate(next.GetString(), UriKind.Absolute, out var nextUri) ||
                nextUri.Scheme != first.Scheme || nextUri.Host != first.Host || !nextUri.IsDefaultPort ||
                nextUri.UserInfo.Length != 0 || nextUri.Fragment.Length != 0 || nextUri.AbsolutePath != first.AbsolutePath)
                throw new InvalidDataException("Unexpected Graph continuation address.");
            url = nextUri;
        }
    }

    private async Task<HttpResponseMessage> GetPage(Uri url, string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = Request(HttpMethod.Get, url, token);
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 3) return response;
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1 << attempt);
            response.Dispose();
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct).ConfigureAwait(false);
        }
    }

    private static string Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var field) ? field.GetString() ?? "" : "";
    private static DateTimeOffset? Date(JsonElement row, string property) =>
        row.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String &&
        field.TryGetDateTimeOffset(out var value) ? value : null;
}
