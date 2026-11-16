using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

// Only the documented ENTRA login confirmation is automated. Never used for key operations.
public sealed class LoginProtocol(TenantSetup tenant)
{
    public bool Confirmed { get; private set; }
    public bool TryConfirm(string screen)
    {
        if (Confirmed || !screen.Contains("Authenticating to ENTRA using the following parameters:", StringComparison.Ordinal) ||
            !Regex.IsMatch(screen, @"Proceed\? \[y/N\]:\s*$")) return false;
        bool Exact(string key, string value) => Regex.IsMatch(screen, @"(?m)^[ \t]*" + Regex.Escape(key) + @":[ \t]*" + Regex.Escape(value) + @"[ \t]*\r?$", RegexOptions.IgnoreCase);
        if (!Exact("tenant_id", tenant.TenantId) || !Exact("client_id", tenant.ClientId) || !Exact("redirect_uri", tenant.RedirectUri) ||
            !Exact("entra_base_url", "https://login.microsoftonline.com") || !Exact("graph_base_url", "https://graph.microsoft.com"))
            throw new InvalidOperationException("The sign-in parameters do not match the selected tenant. Check its configuration.");
        Confirmed = true;
        return true;
    }
    public Uri? AuthorizationUrl(string completedLines)
    {
        if (!completedLines.Contains("Go to the following link in your broswer, and complete the sign-in prompts:", StringComparison.Ordinal)) return null;
        var matches = Regex.Matches(completedLines, @"(?m)^https://[^\s]+$");
        if (matches.Count == 0) return null;
        if (matches.Count != 1 || !Uri.TryCreate(matches[0].Value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "login.microsoftonline.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.Equals($"/{tenant.TenantId}/oauth2/v2.0/authorize", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sign-in address could not be verified. Check the tenant configuration.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !query.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' '))))
                throw new InvalidOperationException("The sign-in address is invalid.");
        }
        if (query.GetValueOrDefault("client_id") != tenant.ClientId || query.GetValueOrDefault("redirect_uri") != tenant.RedirectUri ||
            query.GetValueOrDefault("response_type") != "code" || string.IsNullOrWhiteSpace(query.GetValueOrDefault("state")))
            throw new InvalidOperationException("The sign-in address does not match the selected tenant.");
        return uri;
    }
}
