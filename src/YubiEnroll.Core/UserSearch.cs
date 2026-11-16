using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record UserCandidate(string Id, string Details)
{
    public string DisplayName { get; init; } = "";
    public string UserPrincipalName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Label => string.Join("  ·  ", Regex.Split(Details.Replace(Id, "", StringComparison.OrdinalIgnoreCase).Trim(' ', '|', '│'), @"\s{2,}|[|│]").Select(x => x.Trim()).Where(x => x.Length > 0 && x != "None"));
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
        string[] columns = [];
        int[] columnStarts = [];
        foreach (var raw in Ansi.Replace(output, "").Split('\n'))
        {
            var line = raw.Trim();
            var cells = Regex.Split(line.Trim('|', '│').Trim(), @"\s*[|│]\s*|\s{2,}");
            if (cells.Length >= 2 && cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase) &&
                cells.Skip(1).All(c => c.ToLowerInvariant() is "display name" or "username" or "email"))
            {
                columns = cells;
                columnStarts = !line.Contains('|') && !line.Contains('│')
                    ? Regex.Matches(line, @"\b(?:ID|Display Name|Username|Email)\b", RegexOptions.IgnoreCase).Select(m => m.Index).ToArray() : [];
                if (columnStarts.Length != columns.Length || columnStarts[1] < 36) columnStarts = [];
                continue;
            }
            var match = IdRow.Match(line);
            if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var id) && id != Guid.Empty)
            {
                var candidate = new UserCandidate(id.ToString(), line);
                if (columnStarts.Length > 0)
                    cells = columnStarts.Select((start, i) => start >= line.Length ? "" : line[start..(i + 1 < columnStarts.Length ? Math.Min(columnStarts[i + 1], line.Length) : line.Length)].Trim()).ToArray();
                // Only use explicitly labelled columns. Never infer an email/UPN from its position in free text.
                if (columns.Length == cells.Length && cells[0].Equals(id.ToString(), StringComparison.OrdinalIgnoreCase))
                    for (var i = 1; i < columns.Length; i++) candidate = SetField(candidate, columns[i], cells[i]);
                result.Add(candidate);
            }
            else if (result.Count > 0 && Regex.IsMatch(line, @"^(Display Name|Username|Email)\s*:", RegexOptions.IgnoreCase))
            {
                var separator = line.IndexOf(':');
                result[^1] = SetField(result[^1] with { Details = result[^1].Details + " | " + line }, line[..separator], line[(separator + 1)..]);
            }
        }
        return result.DistinctBy(x => x.Id).ToArray();
    }

    private static UserCandidate SetField(UserCandidate user, string field, string value)
    {
        value = value.Trim();
        if (value is "None" or "null" or "-") value = "";
        return field.Trim().ToLowerInvariant() switch
        {
            "display name" => user with { DisplayName = value },
            "username" => user with { UserPrincipalName = value },
            "email" => user with { Email = value },
            _ => user
        };
    }
}
