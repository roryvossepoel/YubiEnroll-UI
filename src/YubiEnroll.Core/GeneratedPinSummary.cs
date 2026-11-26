using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public static class GeneratedPinSummary
{
    public static bool IsValid(string? pin) => pin is { Length: >= 4 and <= 63 } && pin.All(char.IsAsciiDigit);
    // YubiEnroll 1.2.0 prints these labels only in the completed configuration summary.
    // Never inspect user input or the Enter PIN prompt. Ambiguous output is not captured.
    public static string? ResetReceiptSerial(string output)
    {
        if (!output.Contains("The YubiKey has been reset.", StringComparison.Ordinal) ||
            !output.Contains("New credential registered!", StringComparison.Ordinal)) return null;
        return Summary(output) is { } summary ? Serial(summary) : null;
    }

    private static string? Summary(string output)
    {
        const string heading = "YubiKey configuration summary:";
        var start = output.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0 || output.LastIndexOf(heading, StringComparison.Ordinal) != start) return null;
        return output[(start + heading.Length)..];
    }

    private static string? Serial(string summary)
    {
        var serials = Regex.Matches(summary, @"(?m)^\s*Serial number:\s*([0-9]+)\s*$");
        return serials.Count == 1 ? serials[0].Groups[1].Value : null;
    }

    public static string? Read(string output, string targetSerial)
    {
        if (Summary(output) is not { } summary || Serial(summary) != targetSerial) return null;
        var pins = Regex.Matches(summary, @"(?m)^\s*(?:Generated PIN|Temporary PIN):[ \t]*([0-9]{4,63})[ \t]*\r?$");
        return pins.Count == 1 ? pins[0].Groups[1].Value : null;
    }
}
