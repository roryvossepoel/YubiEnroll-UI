using System.Net;
using System.Text;
using YubiEnroll.Core;

internal static class RegistrationDateTests
{
    private sealed class GraphHandler(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(req), Encoding.UTF8, "application/json") });
    }

    internal static async Task Run(Action<bool, string> check)
    {
        var user = Guid.NewGuid();
        var endpoint = $"https://graph.microsoft.com/v1.0/users/{user:D}/authentication/fido2Methods";
        int calls = 0;
        using var client = new HttpClient(new GraphHandler(req =>
        {
            check(req.Method == HttpMethod.Get && req.Headers.Authorization?.Parameter == "mock" &&
                req.RequestUri!.GetLeftPart(UriPartial.Path) == endpoint, "Dates only read the selected user's Graph v1.0 resource");
            return ++calls == 1
                ? $$"""{"value":[{"id":"Key","displayName":"Office","lastUsedDateTime":null,"createdDateTime":"2026-03-15T12:00:00Z"}],"@odata.nextLink":"{{endpoint}}?$skiptoken=next"}"""
                : """{"value":[{"id":"key","displayName":"Office","createdDateTime":"invalid","lastUsedDateTime":"2026-08-20T13:42:00+02:00"}]}""";
        }));
        var data = await RegistrationDateReader.ReadAsync(client, user, "mock", CancellationToken.None);
        check(calls == 2 && data.Count == 2 && data["Key"].Registered?.Month == 3 && data["Key"].LastUsed == null &&
            data["key"].Registered == null && data["key"].LastUsed?.UtcDateTime.Hour == 11,
            "Same-named registrations retain separate dates across pages by exact case-sensitive ID");
        check(new RegistrationCard(new("Office","YubiKey 5","Key"), data["Key"]).DateLine.EndsWith("Last used: Unknown") &&
            new RegistrationCard(new("Office","YubiKey 5","missing"), null).DateLine.Contains("Registered: Unknown"),
            "Missing metadata is unknown, never inferred from another key");
        foreach (var invalid in new[]{ endpoint.Replace("graph.microsoft.com", "example.test"), endpoint.Replace(user.ToString(), Guid.NewGuid().ToString()), endpoint })
        {
            calls = 0;
            using var broken = new HttpClient(new GraphHandler(_ => ++calls == 1
                ? $$"""{"value":[],"@odata.nextLink":"{{invalid}}"}""" : throw new Exception("Unexpected authenticated request.")));
            try { await RegistrationDateReader.ReadAsync(broken, user, "mock", CancellationToken.None); throw new Exception("Untrusted continuation accepted."); }
            catch (InvalidDataException) { check(calls == 1, "Graph does not follow a foreign, different-user or repeated continuation"); }
        }
    }
}
