using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Identity.Client;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

// MSAL owns the OAuth flow, PKCE and its in-memory account cache. Never read YubiEnroll's token store.
internal sealed class RegistrationDateSession(TenantSetup tenant)
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    private static readonly HttpClient Graph = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private IPublicClientApplication? _client;
    private IAccount? _account;

    internal async Task<IReadOnlyDictionary<string, RegistrationDates>> ReadAsync(Guid user, bool interactive,
        Action browserOpened, CancellationToken cancellation)
    {
        if (_client != null && _account != null)
        {
            try
            {
                var silent = await _client.AcquireTokenSilent(Scopes, _account).ExecuteAsync(cancellation);
                return await ReadDates(silent);
            }
            catch (MsalUiRequiredException) when (interactive) { }
        }
        if (!interactive) throw new InvalidOperationException("Sign in to load dates.");
        var redirect = LoopbackAddress(tenant.RedirectUri);
        var client = PublicClientApplicationBuilder.Create(tenant.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{Guid.Parse(tenant.TenantId):D}")
            .WithRedirectUri(redirect.AbsoluteUri).Build();
        browserOpened();
        var result = await client.AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        CheckTenant(result);
        _client = client; _account = result.Account;
        return await ReadDates(result);

        void CheckTenant(AuthenticationResult token)
        {
            if (!Guid.TryParse(token.TenantId, out var actual) || actual != Guid.Parse(tenant.TenantId))
                throw new InvalidOperationException("The selected tenant changed.");
        }
        Task<IReadOnlyDictionary<string, RegistrationDates>> ReadDates(AuthenticationResult token)
        {
            CheckTenant(token);
            return RegistrationDateReader.ReadAsync(Graph, user, token.AccessToken, cancellation);
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
