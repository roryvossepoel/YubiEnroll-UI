using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record RegisteredCredential(string Name, string Model, string Id);

public static class RegisteredCredentials
{
    // Read-only presentation: only a recognized table with three columns is interpreted.
    // Raw output remains available when a CLI version uses a different format.
    public static IReadOnlyList<RegisteredCredential> Parse(string text)
    {
        var result = new List<RegisteredCredential>();
        var started = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^Display name\s{2,}Model\s{2,}ID$", RegexOptions.IgnoreCase)) { started = true; continue; }
            if (!started || line.Length == 0) continue;
            var fields = Regex.Split(line, @"\s{2,}");
            if (fields.Length == 3 && fields.All(x => x.Length > 0) && !fields[0].StartsWith('-'))
                result.Add(new(fields[0], fields[1], fields[2]));
        }
        return result;
    }
}
