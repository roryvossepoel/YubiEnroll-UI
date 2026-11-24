using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace YubiEnroll.Core;

public static class BatchRoster
{
    public const int Limit = 500;
    public const int MaxCharacters = 256_000;

    public static string[] Import(string text, List<int>? sourceLines = null)
    {
        if (text.Length > MaxCharacters) throw new ArgumentException("The list is too large. Import at most 500 users at a time.");
        text = text.TrimStart('\uFEFF');
        using var parser = new TextFieldParser(new StringReader(text)) { HasFieldsEnclosedInQuotes = true };
        var first = text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        var quoted = false; var delimiter = ",";
        foreach (var ch in first)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch is ',' or ';' or '\t') { delimiter = ch.ToString(); break; }
        }
        parser.SetDelimiters(delimiter);
        var physicalLines = text.ReplaceLineEndings("\n").Split('\n');
        var upns = new List<string>();
        int? column = null; var columns = 1;
        try
        {
            while (!parser.EndOfData)
            {
                var line = checked((int)parser.LineNumber);
                while (line > 0 && line <= physicalLines.Length && string.IsNullOrWhiteSpace(physicalLines[line - 1])) line++;
                var cells = parser.ReadFields() ?? [];
                if (column == null)
                {
                    var indices = Enumerable.Range(0, cells.Length).Where(i => cells[i].Trim().Equals("UserPrincipalName", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (indices.Length == 1) { column = indices[0]; columns = cells.Length; continue; }
                    if (indices.Length > 1 || cells.Length != 1) throw new ArgumentException("Use one UserPrincipalName column, or paste one sign-in name per line.");
                    column = 0;
                }
                if (cells.Length != columns) throw new ArgumentException($"Unexpected column count in entry {upns.Count + 1}.");
                var value = cells[column.Value].Trim();
                if (value.Length == 0 || value.Any(char.IsControl)) throw new ArgumentException($"Missing or invalid sign-in name in entry {upns.Count + 1}.");
                upns.Add(value);
                sourceLines?.Add(line);
                if (upns.Count > Limit) throw new ArgumentException("Split the list into batches of at most 500 users.");
            }
        }
        catch (MalformedLineException) { throw new ArgumentException("The CSV could not be read. Check the quotes and column separators."); }
        if (upns.Count == 0) throw new ArgumentException("Enter at least one user principal name.");
        return upns.ToArray();
    }

    public static bool ValidUpn(string value) => value.Length is >= 3 and <= 256 && value.IndexOf('@') > 0 &&
        value.LastIndexOf('@') == value.IndexOf('@') && !value.EndsWith('@') && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    public static UserCandidate? Exact(string upn, IReadOnlyList<UserCandidate> users)
    {
        var exact = users.Where(u => upn.Equals(u.UserPrincipalName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return exact.Length == 1 && Guid.TryParse(exact[0].Id, out var id) && id != Guid.Empty ? exact[0] : null;
    }
}

public enum IssueStatus { Pending, InProgress, Registered, Skipped, CheckRequired }
public sealed record IssuanceRow(UserCandidate User, IssueStatus Status = IssueStatus.Pending, string Serial = "")
{
    public string StatusLabel => Status switch { IssueStatus.Registered => "Registered", IssueStatus.Skipped => "Skipped",
        IssueStatus.CheckRequired => "Check required", IssueStatus.InProgress => "In progress", _ => "Not started" };
}

public sealed class EnrollmentBatch
{
    private readonly IssuanceRow[] _rows;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    public Provider Tenant { get; }
    public EnrollmentProfile Profile { get; }
    public IReadOnlyList<IssuanceRow> Rows { get; }
    public int Position { get; private set; }
    public IssuanceRow? Current => Position < _rows.Length ? _rows[Position] : null;
    public EnrollmentBatch(Provider tenant, EnrollmentProfile profile, IReadOnlyList<UserCandidate> users)
    {
        if (users.Count is 0 or > BatchRoster.Limit || users.Any(u => !BatchRoster.ValidUpn(u.UserPrincipalName) || !Guid.TryParse(u.Id, out var id) || id == Guid.Empty)
            || users.Select(u => Guid.Parse(u.Id)).Distinct().Count() != users.Count ||
            users.Select(u => u.UserPrincipalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != users.Count)
            throw new ArgumentException("Every row must have a unique, verified account.");
        Tenant = tenant; Profile = profile;
        _rows = users.Select(u => new IssuanceRow(u)).ToArray(); Rows = Array.AsReadOnly(_rows);
    }

    public static bool AllowsPin(bool? reset, FidoPinState pin) => reset.HasValue &&
        (pin == FidoPinState.NotSet || reset == true && pin is (FidoPinState.Set or FidoPinState.Blocked));
    public bool Used(string serial) => _attempted.Contains(serial);
    public string? StillConnected(IEnumerable<ResetKey> connected) => connected.Select(k => k.Serial).FirstOrDefault(Used);
    public void Start(UserCandidate user, ResetKey key, FidoPinState pin, ProfileSetup setup)
    {
        if (Current is not { Status: IssueStatus.Pending } row || row.User != user || !key.Serial.All(char.IsAsciiDigit) || key.Serial.Length == 0 ||
            setup.Name != Profile.Name || !AllowsPin(setup.Reset, pin) || !_attempted.Add(key.Serial))
            throw new InvalidOperationException("The recipient, key or profile is no longer eligible. Review this row again.");
        _rows[Position] = row with { Serial = key.Serial, Status = IssueStatus.InProgress };
    }
    public void Complete(int? exit)
    {
        if (Current is not { Status: IssueStatus.InProgress } row) throw new InvalidOperationException("No enrollment is running.");
        _rows[Position] = row with { Status = exit == 0 ? IssueStatus.Registered : IssueStatus.CheckRequired };
    }
    public void Skip()
    {
        if (Current is not { Status: IssueStatus.Pending } row) throw new InvalidOperationException("This row cannot be skipped.");
        _rows[Position] = row with { Status = IssueStatus.Skipped };
        Advance();
    }
    public void Advance()
    {
        if (Current is not { Status: IssueStatus.Registered or IssueStatus.Skipped or IssueStatus.CheckRequired })
            throw new InvalidOperationException("Finish or skip the current row first.");
        Position++;
    }

    public string Csv(string separator)
    {
        static string Cell(string value, bool serial = false)
        {
            if (value.Length > 0 && (serial || "=+-@\t\r\n".Contains(value.TrimStart().FirstOrDefault()) || char.IsControl(value[0]))) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var result = new StringBuilder();
        void Line(params string[] cells) => result.AppendLine(string.Join(separator, cells));
        Line("Tenant","TenantId","Profile","UserPrincipalName","UserId","DisplayName","Serial","Result");
        foreach (var r in Rows) Line(Cell(Tenant.Name), Cell(Tenant.TenantId), Cell(Profile.Name), Cell(r.User.UserPrincipalName),
            Cell(r.User.Id), Cell(r.User.DisplayName), Cell(r.Serial, true), Cell(r.StatusLabel));
        return result.ToString();
    }
}
