using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public static class InteractiveFailure
{
    public static string Describe(string output)
    {
        var parts = new List<string>();
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
