using System.Net;
using System.Text;
using System.Text.Json;
using YubiEnroll.Core;

internal static class ManagementGraphTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => Task.FromResult(answer(req));
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal static async Task Run(Action<bool, string> check)
    {
        var user = Guid.NewGuid();
        var endpoint = $"https://graph.microsoft.com/v1.0/users/{user:D}/authentication/fido2Methods";
        var token = (CancellationToken _) => Task.FromResult("mock");
        var calls = 0;
        using var search = new HttpClient(new Handler(req =>
        {
            var url = req.RequestUri!;
            var filter = Uri.UnescapeDataString(url.Query.Split("&$filter=")[1]);
            check(req.Method == HttpMethod.Get && req.Headers.Authorization?.Parameter == "mock" &&
                url.AbsolutePath == "/v1.0/users" && filter.Contains("startswith(mail,'O''Brien & Co')") &&
                filter.Contains("startswith(userPrincipalName,'O''Brien & Co')"), "Directory search escapes literals and query delimiters");
            return Json($$"""{"value":[{"id":"{{user}}","displayName":"Alex","mail":null,"userPrincipalName":"alex@example.test"}]}""");
        }));
        var found = (await new ManagementGraph(search,token).SearchAsync("O'Brien & Co", default)).Single();
        check(found.Id == user.ToString() && found.Label == "Alex  ·  alex@example.test", "User search retains the immutable object ID and displays distinct profile fields");
        using var client = new HttpClient(new Handler(req =>
        {
            check(req.Method == HttpMethod.Get && req.Headers.Authorization?.Parameter == "mock" &&
                req.RequestUri!.GetLeftPart(UriPartial.Path) == endpoint, "Listing registrations and dates only reads the selected user");
            return Json(++calls == 1
                ? $$"""{"value":[{"id":"Key","model":"YubiKey 5","displayName":"Office","lastUsedDateTime":null,"createdDateTime":"2026-03-15T12:00:00Z"}],"@odata.nextLink":"{{endpoint}}?$skiptoken=next"}"""
                : """{"value":[{"id":"key","model":"YubiKey 5","displayName":"Office","createdDateTime":"invalid","lastUsedDateTime":"2026-08-20T13:42:00+02:00"}]}""");
        }));
        var rows = await new ManagementGraph(client,token).ListAsync(user, default);
        check(calls == 2 && rows.Count == 2 && rows[0].Dates?.Registered?.Month == 3 && rows[0].DateLine.EndsWith("Last used: Unknown") &&
            rows[1].Dates?.Registered == null && rows[1].Dates?.LastUsed?.UtcDateTime.Hour == 11,
            "Same-named registrations retain separate dates across pages by exact case-sensitive ID");
        foreach (var invalid in new[]{ endpoint.Replace("graph.microsoft.com", "example.test"), endpoint.Replace(user.ToString(), Guid.NewGuid().ToString()), endpoint })
        {
            calls = 0;
            using var broken = new HttpClient(new Handler(_ => ++calls == 1
                ? Json($$"""{"value":[],"@odata.nextLink":"{{invalid}}"}""") : throw new Exception("Unexpected authenticated request.")));
            try { await new ManagementGraph(broken,token).ListAsync(user,default); throw new Exception("Untrusted continuation accepted."); }
            catch (InvalidDataException) { check(calls == 1, "Graph does not follow a foreign, different-user or repeated continuation"); }
        }

        foreach (var scenario in new[] {"removed","propagated","changed","unknown-model","phone-target","invalid-user","unsafe-id","duplicate-id","still-present",
                    "missing-phone","denied","timeout","post-list-failed","invalid-success"})
        {
            const string model = "YubiKey 5 Series";
            var target = new RegisteredCredential("Office", model, "Key-1");
            var log = new List<string>();
            using var mock = new HttpClient(new Handler(req =>
            {
                log.Add(req.Method.Method);
                if (req.Method == HttpMethod.Delete)
                {
                    check(req.RequestUri!.AbsoluteUri == endpoint+"/Key-1" && req.Headers.Authorization?.Parameter == "mock",
                        "Only the confirmed object's exact ID on the selected user is addressed");
                    if (scenario == "timeout") throw new TaskCanceledException();
                    return new HttpResponseMessage(scenario switch { "denied" => HttpStatusCode.Forbidden,
                        "invalid-success" => HttpStatusCode.Accepted, _ => HttpStatusCode.NoContent });
                }
                check(req.Method == HttpMethod.Get && req.RequestUri!.AbsoluteUri == endpoint,"Verify the complete, unfiltered method collection");
                var after = log.Contains("DELETE");
                if (after && scenario == "post-list-failed") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var methods = new List<object>();
                if (!after || scenario == "still-present" || scenario == "propagated" && log.Count == 3)
                    methods.Add(new { id = "Key-1", displayName = scenario == "changed" ? "Renamed" : "Office", model });
                if (scenario == "duplicate-id") methods.Add(new {id="Key-1", displayName="Other", model});
                methods.Add(new {id="Key-10", displayName="Office", model});
                methods.Add(new {id="key-1", displayName="Office", model});
                if (!after || scenario != "missing-phone") methods.Add(new {id="phone", displayName="Office", model="Microsoft Authenticator - Android"});
                return Json(JsonSerializer.Serialize(new {value = methods}));
            }));
            var selected = scenario switch
            {
                "unknown-model" => target with {Model="Security Key"}, "phone-target" => target with {Id="phone",Model="Microsoft Authenticator - Android"},
                "unsafe-id" => target with {Id="../x"}, _ => target
            };
            bool verified = false;
            try
            {
                var remaining = await new ManagementGraph(mock,token).RemoveAsync(scenario == "invalid-user" ? Guid.Empty : user, selected, default);
                verified = true;
                check(remaining.Select(r=>r.Id).SequenceEqual(new[]{"Key-10","key-1","phone"}),"Similar IDs, duplicate names and the hidden phone remain");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or HttpRequestException) { }
            var expected = scenario is "changed" or "duplicate-id" ? new[] {"GET"} :
                scenario is "unsafe-id" or "phone-target" or "unknown-model" or "invalid-user" ? Array.Empty<string>() :
                scenario == "propagated" ? new[] {"GET","DELETE","GET","GET"} :
                scenario == "still-present" ? new[] {"GET","DELETE","GET","GET","GET","GET"} :
                scenario is "denied" or "timeout" or "invalid-success" ? new[] {"GET","DELETE"} : new[] {"GET","DELETE","GET"};
            check(verified == (scenario is "removed" or "propagated") && log.SequenceEqual(expected), "Exact deletion is checked and never replayed: " + scenario);
        }
    }
}
