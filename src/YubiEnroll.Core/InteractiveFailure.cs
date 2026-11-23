using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public static class InteractiveFailure
{
    public const string PinAuthInvalid = "CTAP 0x33 · PIN_AUTH_INVALID";
    public const string OperatorInterrupted = "Interrupted by operator. Outcome unverified.";

    public static string Describe(string output)
    {
        var parts = new List<string>();
        if (output.Contains("CTAP error: 0x33 - PIN_AUTH_INVALID", StringComparison.Ordinal))
            parts.Add(PinAuthInvalid); // Only retain the known code, never the raw terminal line.
        var http = Regex.Match(output, @"(?m)^\s*ERROR:\s*(?<code>[45][0-9]{2})\b");
        if (http.Success) parts.Add("HTTP status: " + http.Groups["code"].Value);
        foreach (var name in new[] { "request-id", "client-request-id" })
        {
            var id = Regex.Match(output, @"(?m)^\s*" + name + @":\s*([0-9a-fA-F-]{36})\s*$");
            if (id.Success && Guid.TryParse(id.Groups[1].Value, out var value)) parts.Add(name + ": " + value);
        }
        return string.Join("\n", parts);
    }
}
