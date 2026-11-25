using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record TenantKeyRow(string User, string Upn, RegisteredCredential Credential)
{
    private static readonly Regex NamedSerial = new(@"\AYubiKey(?: [^()\r\n]+)?\s*\(([0-9]+)\)\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public string Serial { get; } = NamedSerial.Match(Credential.Name).Groups[1].Value;
    public string SerialLabel => Serial.Length == 0 ? "Unknown" : Serial;
    public string SerialSource => Serial.Length == 0 ? "" : "From registration name";
    public bool Matches(string query) => User.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Upn.Contains(query, StringComparison.OrdinalIgnoreCase) || Credential.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Credential.Model.Contains(query, StringComparison.OrdinalIgnoreCase) || Serial.Contains(query, StringComparison.OrdinalIgnoreCase);
}

public sealed record TenantSearchBatch(int TotalUsers, int UsersChecked, int Unavailable, IReadOnlyList<TenantKeyRow> Rows);
