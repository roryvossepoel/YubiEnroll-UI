using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record UserCandidate(string Id, string Details)
{
    public override string ToString() => Details;
}

public static class UserSearch
{
    // Entra only: accept an object ID at the beginning of a row or in an ID-labelled field.
    // Never guess a UPN from a display name or an email column.
    private static readonly Regex IdRow = new(
        @"^\s*[|│]?\s*(?:ID\s*:\s*)?(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?=\s|[|│]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]");

    public static IReadOnlyList<UserCandidate> Parse(string output)
    {
        var result = new List<UserCandidate>();
        foreach (var raw in Ansi.Replace(output, "").Split('\n'))
        {
            var line = raw.Trim();
            var match = IdRow.Match(line);
            if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var id) && id != Guid.Empty)
                result.Add(new(id.ToString(), line));
            else if (result.Count > 0 && Regex.IsMatch(line, @"^(Display Name|Username|Email)\s*:", RegexOptions.IgnoreCase))
                result[^1] = result[^1] with { Details = result[^1].Details + " | " + line };
        }
        return result.DistinctBy(x => x.Id).ToArray();
    }
}
