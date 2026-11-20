using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Identity.Client;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

// The interactive flow exists only at Connect. Each operation acquires a token silently for that same account.
internal static class ManagementSignIn
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    private static readonly HttpClient Graph = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    internal static async Task<ManagementGraph> ConnectAsync(TenantSetup tenant, CancellationToken cancellation)
    {
        var tenantId = Guid.Parse(tenant.TenantId);
        var client = PublicClientApplicationBuilder.Create(tenant.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId:D}")
            .WithRedirectUri(LoopbackAddress(tenant.RedirectUri).AbsoluteUri).Build();
        var login = await client.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false).ExecuteAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        VerifyTenant(login);
        var account = login.Account ?? throw new InvalidOperationException("The sign-in did not return an administrator account.");
        return new ManagementGraph(Graph, async ct =>
        {
            var token = await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            VerifyTenant(token);
            if (token.Account?.HomeAccountId.Identifier != account.HomeAccountId.Identifier)
                throw new InvalidOperationException("The signed-in account changed. Connect again.");
            return token.AccessToken;
        });

        void VerifyTenant(AuthenticationResult token)
        {
            if (!Guid.TryParse(token.TenantId, out var actual) || actual != tenantId)
                throw new InvalidOperationException("The signed-in tenant does not match the selected tenant.");
        }
    }

    // MSAL selects a new port for bare localhost and would discard the configured path.
    internal static Uri LoopbackAddress(string configured)
    {
        var uri = new Uri(configured);
        if (uri.Scheme != "http" || uri.Host != "localhost" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new InvalidOperationException("Invalid sign-in callback.");
        if (!uri.IsDefaultPort) return uri;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return new UriBuilder(uri) { Port = ((IPEndPoint)probe.LocalEndpoint).Port }.Uri;
    }

}
