using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record RegisteredCredential(string Name, string Model, string Id);

public static class RegisteredCredentials
{
    // Reject partial tables, clipped IDs and duplicate identities. Never treat a CLI error as an empty list.
    public static IReadOnlyList<RegisteredCredential> Parse(string text)
    {
        var rows = new List<RegisteredCredential>();
        bool header = false, explicitEmpty = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!header && Regex.IsMatch(line, @"^Fetching credentials(?: for [^\r\n]+)?\.*$", RegexOptions.IgnoreCase)) continue;
            if (!header && Regex.IsMatch(line, @"^No credentials(?: found| registered)?(?: for (?:this user|user [^\r\n]+))?[.!]?$", RegexOptions.IgnoreCase))
            { explicitEmpty = true; continue; }
            var fields = Regex.Split(line.Trim('│', '|').Trim(), @"\s*[│|]\s*|\s{2,}");
            if (!explicitEmpty && !header && fields.SequenceEqual(new[] { "Display name", "Model", "ID" }, StringComparer.OrdinalIgnoreCase))
            { header = true; continue; }
            if (header && Regex.IsMatch(line, @"^[-= ─┼┬┴│+|]+$")) continue;
            if (!header || fields.Length != 3 || fields.Any(string.IsNullOrWhiteSpace) ||
                !Regex.IsMatch(fields[2], @"\A[A-Za-z0-9_+/=-]+\z") || rows.Any(r => r.Id == fields[2]))
                throw new InvalidDataException("Unrecognized registration list. No removal is available.");
            rows.Add(new(fields[0], fields[1], fields[2]));
        }
        if (!header && !explicitEmpty) throw new InvalidDataException("The registration list could not be verified.");
        return rows;
    }
}

// One explicit target; re-read before mutation, verify absence afterwards and never retry deletion.
public sealed class PasskeyRegistrationService(Func<string[], Task<CliResult>> run)
{
    public async Task<IReadOnlyList<RegisteredCredential>> ListAsync(Guid user)
    {
        if (user == Guid.Empty) throw new ArgumentException("Select a valid Entra user.");
        var result = await run(["credentials", "list", "--", user.ToString()]);
        Check(result, "Reading registrations");
        return RegisteredCredentials.Parse(result.Output);
    }

    public async Task<IReadOnlyList<RegisteredCredential>> RemoveAsync(Guid user, RegisteredCredential target)
    {
        var current = await ListAsync(user);
        if (!current.Contains(target)) throw new InvalidOperationException("The selected registration changed. Refresh and review it again.");
        try
        {
            // The UI already confirmed this specific tenant, user and credential. '--' also protects IDs starting with '-'.
            var result = await run(["credentials", "delete", "--force", "--", user.ToString(), target.Id]);
            Check(result, "Removing the registration");
        }
        catch (TimeoutException) { throw new InvalidOperationException("Removal outcome unknown. Refresh before trying again."); }
        IReadOnlyList<RegisteredCredential> after;
        try { after = await ListAsync(user); }
        catch (Exception ex) { throw new InvalidOperationException("The removal was submitted, but the updated list could not be verified. Refresh before trying again.", ex); }
        if (after.Any(r => r.Id == target.Id)) throw new InvalidOperationException("Removal has not been verified. Refresh to check the registration.");
        return after;
    }

    private static void Check(CliResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var diagnostic = InteractiveFailure.Describe(result.Error + "\n" + result.Output);
        throw new InvalidOperationException($"{operation} failed (exit {result.ExitCode}). Check the tenant connection, delegated permissions and active Entra role. {diagnostic}".TrimEnd());
    }
}
