using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record NfcReaderStatus(string Name, bool IsNfc, string State)
{
    // Cosmetic only: keep the original PC/SC name for routing. Keep nonzero slot indices visible.
    public static string DisplayName(string name) =>
        Regex.Match(name, @"^HID Global (OMNIKEY [^()]+?) Smart Card Reader (\d+)$", RegexOptions.IgnoreCase)
            is { Success: true } match ? match.Groups[1].Value + (match.Groups[2].Value == "0" ? "" : " · reader " + match.Groups[2].Value)
            : name.EndsWith(" 0", StringComparison.Ordinal) ? name[..^2] : name;

    public string Message => State switch
    {
        "ready" or "empty" => "",
        "unsupported" => "The card does not expose supported FIDO2 and a readable serial number.",
        "ambiguous" => "Reader name matches multiple readers. Disconnect duplicate readers and Refresh.",
        _ => "Could not read the card. Reposition it, close other key tools and Refresh."
    };
    public bool HasWarning => State is "unsupported" or "ambiguous" or "unavailable";
}

// Remember verified NFC readers for this session, including while their card is removed.
public sealed class NfcReaderCatalog
{
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    public IReadOnlyList<NfcReaderStatus> Visible(IEnumerable<NfcReaderStatus> readers)
    {
        var all = readers.Where(r => !Regex.IsMatch(r.Name,
            @"\b(?:Windows Hello|Microsoft Passport|virtual|TPM)\b", RegexOptions.IgnoreCase)).ToArray();
        foreach (var r in all)
        {
            if (r.State == "contact") _verified.Remove(r.Name);
            else if (r.IsNfc) _verified.Add(r.Name);
        }
        // Unplugged readers are absent from the current list, regardless of session history.
        return all.Where(r => r.State != "contact" && (r.IsNfc || _verified.Contains(r.Name)))
            .DistinctBy(r => r.Name).ToArray();
    }
}
