using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Preview) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion? Parse(string value)
    {
        var m = Regex.Match(value, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var a) || !int.TryParse(m.Groups[2].Value, out var b) || !int.TryParse(m.Groups[3].Value, out var c)) return null;
        var preview = m.Groups[4].Value;
        if (preview.Split('.').Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return null;
        return new(a, b, c, preview);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null) return 1;
        foreach (var n in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) }) if (n != 0) return n;
        if (Preview == other.Preview) return 0;
        if (Preview.Length == 0) return 1;
        if (other.Preview.Length == 0) return -1;
        var a = Preview.Split('.'); var b = other.Preview.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = a[i].All(char.IsAsciiDigit); var bn = b[i].All(char.IsAsciiDigit);
            var n = an && bn ? BigInteger.Parse(a[i]).CompareTo(BigInteger.Parse(b[i])) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a[i], b[i]);
            if (n != 0) return n;
        }
        return a.Length.CompareTo(b.Length);
    }
}
public sealed record PublishedRelease(string Tag, Uri Url, ReleaseVersion Version, bool Preview);
public static class ReleaseUpdates
{
    public const string Repository = "roryvossepoel/PasskeyDesk";
    public static IReadOnlyList<PublishedRelease> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<PublishedRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("published_at").ValueKind == JsonValueKind.Null) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var version = ReleaseVersion.Parse(tag);
            if (version == null) continue;
            var url = new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            result.Add(new(tag, url, version, release.GetProperty("prerelease").GetBoolean()));
        }
        return result;
    }
}
