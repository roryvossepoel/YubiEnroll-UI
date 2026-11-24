using YubiEnroll.Core;

internal static class EnrollmentBatchTests
{
    static List<string[]> Parse(string csv, string separator)
    {
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv));
        parser.SetDelimiters(separator);
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        return rows;
    }

    internal static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string why)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { check(true, why); return; }
            check(false, why);
        }
        var a = new UserCandidate(Guid.NewGuid().ToString(), "") { DisplayName = "=Example", UserPrincipalName = "a@example.test" };
        var b = new UserCandidate(Guid.NewGuid().ToString(), "") { UserPrincipalName = "b@example.test" };
        var tenant = new Provider("Demo", "ENTRA", Guid.NewGuid().ToString(), "", "t");
        var profile = new EnrollmentProfile("Setup", "", "p");
        var setup = ProfileCompatibility.Basic("Setup");
        var key = new ResetKey("0012345", "YubiKey");
        check(BatchRoster.Import("\uFEFFName;UserPrincipalName\r\n\"Example; A\";a@example.test\r\nB;b@example.test").SequenceEqual(new[] { a.UserPrincipalName, b.UserPrincipalName }),
            "Excel CSV with quoted cells imports the named column");
        check(BatchRoster.Import("a@example.test\r\nb@example.test").Length == 2, "A pasted UPN column needs no header");
        var lines = new List<int>();
        BatchRoster.Import("UserPrincipalName\n\na@example.test\n\nb@example.test", lines);
        check(lines.SequenceEqual(new[] { 3, 5 }), "Input validation uses physical editor line numbers");
        var nfc = key with { Reader = "OMNIKEY 0" };
        var current = new KeyInventory([nfc], 0);
        var transcript = $"The YubiKey has been reset.\nNew credential registered!\nYubiKey configuration summary:\nSerial number: {key.Serial}\nTemporary PIN: 012345";
        var serial = GeneratedPinSummary.ResetReceiptSerial(transcript);
        check(EnrollmentTargetGuard.ConfirmNfcReset(nfc, current, true, serial) &&
            GeneratedPinSummary.Read(transcript, key.Serial) == "012345", "Same-key NFC reset keeps native success and the generated PIN");
        check(!EnrollmentTargetGuard.ConfirmNfcReset(nfc, new([nfc with { Serial = "222" }], 0), true, serial) &&
            !EnrollmentTargetGuard.ConfirmNfcReset(nfc, new([nfc with { Reader = "Different reader" }], 0), true, serial) &&
            !EnrollmentTargetGuard.ConfirmNfcReset(nfc, new([], 0), true, serial) &&
            !EnrollmentTargetGuard.ConfirmNfcReset(nfc, current, true, "222") &&
            !EnrollmentTargetGuard.ConfirmNfcReset(nfc, current, true, null),
            "Wrong card, reader, missing card or mismatched native summary stays uncertain");
        Reject(() => BatchRoster.Import("Name,Email\na,a@example.test"), "Unnamed identity columns cannot be guessed");
        Reject(() => BatchRoster.Import("Name,UserPrincipalName\na,b,c"), "Ragged rows cannot shift a recipient");
        check(BatchRoster.Exact(a.UserPrincipalName, [a with { UserPrincipalName = "", Email = a.UserPrincipalName }]) == null &&
            BatchRoster.Exact(a.UserPrincipalName, [a, b with { UserPrincipalName = a.UserPrincipalName }]) == null &&
            BatchRoster.Exact(a.UserPrincipalName.ToUpperInvariant(), [b, a]) == a, "Reject ambiguous, partial and email-only matches");
        Reject(() => new EnrollmentBatch(tenant, profile, [a, b with { Id = a.Id }]), "One directory object cannot be issued twice");
        var batch = new EnrollmentBatch(tenant, profile, [a, b]);
        Reject(() => batch.Start(b, key, FidoPinState.NotSet, setup), "A different verified user cannot replace the current recipient");
        var resetBatch = new EnrollmentBatch(tenant, profile, [a,b]);
        Reject(() => resetBatch.Start(a, key, FidoPinState.Unknown, setup with { Reset = true }), "Unknown PIN state must not start a batch reset");
        resetBatch.Start(a, key, FidoPinState.NotSet, setup with { Reset = true }); resetBatch.Complete(0); resetBatch.Advance();
        Reject(() => resetBatch.Start(b, key, FidoPinState.Set, setup with { Reset = true }), "A reset profile cannot reuse the previous serial");
        resetBatch.Start(b, key with { Serial = "98765" }, FidoPinState.Set, setup with { Reset = true });
        check(resetBatch.Current?.Status == IssueStatus.InProgress, "Reset profile supports consecutive recipients and a fresh serial");
        var handoff = new EnrollmentBatch(tenant, profile, [a, b]);
        var generate = setup with { RandomPin = true };
        handoff.Start(a, key, FidoPinState.NotSet, generate);
        handoff.Complete(0, "012345");
        handoff.Advance();
        handoff.Start(b, key with { Serial = "321" }, FidoPinState.NotSet, generate);
        handoff.Complete(0, "543210");
        handoff.Advance();
        check(handoff.Rows[0].GeneratedPin == "012345" && handoff.Rows[1].GeneratedPin == "543210",
            "A multi-recipient batch keeps each verified PIN with its own recipient without a journal");
        foreach (var state in Enum.GetValues<FidoPinState>().Where(s => s != FidoPinState.NotSet))
            Reject(() => batch.Start(a, key, state, setup), "Only an explicit unset PIN is eligible");
        batch.Start(a, key, FidoPinState.NotSet, setup with { RandomPin = true });
        Reject(batch.Advance, "A running row cannot advance");
        batch.Complete(0, "004681");
        check(batch.Current?.User == a && batch.Current.Status == IssueStatus.Registered, "Reported success stays on the handoff row");
        batch.Advance();
        Reject(() => batch.Start(b, key with { Reader = "NFC 0" }, FidoPinState.NotSet, setup), "The same physical serial cannot be reused via NFC");
        var spare = key with { Serial = "00098765" };
        batch.Start(b, spare, FidoPinState.NotSet, setup with { RandomPin = true });
        batch.Complete(null, "018457");
        Reject(() => batch.Start(b, spare, FidoPinState.NotSet, setup), "An uncertain operation cannot be replayed");
        batch.Advance();
        var csv = batch.Csv(";");
        check(batch.Current == null && csv.Contains("Check required") && csv.Contains("Registered") && csv.Contains("'0012345") && csv.Contains("'=Example") && !csv.Contains("PIN"),
            "The report preserves unknown outcomes and text serials and contains no PINs");
        check(batch.Rows[0].GeneratedPin == "004681" && batch.Rows[1].GeneratedPin == null,
            "Only the verified successful recipient has a temporary batch PIN, without any journal");
        foreach (var separator in new[] { ";", "," })
        {
            var plain = Parse(batch.Csv(separator, includeInitialPins: false), separator);
            var withPins = Parse(batch.Csv(separator, includeInitialPins: true), separator);
            check(plain.Count == 3 && plain.All(row => row.Length == 8) &&
                plain.All(row => !row.Contains("Initial PIN") && !row.Contains("'004681")), "Clearing Include initial PINs omits the PIN field");
            check(withPins.Count == 3 && withPins.All(row => row.Length == 9) && withPins[0][8] == "Initial PIN",
                "Including PINs adds one PIN column");
            check(withPins[1][8] == "'004681", "Saved initial PIN is exported as spreadsheet text with leading zeroes");
            check(withPins[2][8] == "", "Unverified recipient has an empty PIN field");
        }
        check(!batch.Rows[0].ToString().Contains("004681"), "Logging a row cannot reveal its initial PIN");
        batch.ClearPins();
        check(batch.Rows.All(row => row.GeneratedPin == null) && Parse(batch.Csv(";", true), ";").Skip(1).All(row => row[8] == ""),
            "Leaving the batch releases its PINs");
    }
}
