using YubiEnroll.Core;

internal static class EnrollmentBatchTests
{
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
        foreach (var state in Enum.GetValues<FidoPinState>().Where(s => s != FidoPinState.NotSet))
            Reject(() => batch.Start(a, key, state, setup), "Only an explicit unset PIN is eligible");
        batch.Start(a, key, FidoPinState.NotSet, setup);
        Reject(batch.Advance, "A running row cannot advance");
        batch.Complete(0);
        check(batch.Current?.User == a && batch.Current.Status == IssueStatus.Registered, "Reported success stays on the handoff row");
        batch.Advance();
        Reject(() => batch.Start(b, key with { Reader = "NFC 0" }, FidoPinState.NotSet, setup), "The same physical serial cannot be reused via NFC");
        var spare = key with { Serial = "00098765" };
        batch.Start(b, spare, FidoPinState.NotSet, setup);
        batch.Complete(null);
        Reject(() => batch.Start(b, spare, FidoPinState.NotSet, setup), "An uncertain operation cannot be replayed");
        batch.Advance();
        var csv = batch.Csv(";");
        check(batch.Current == null && csv.Contains("Check required") && csv.Contains("Registered") && csv.Contains("'0012345") && csv.Contains("'=Example") && !csv.Contains("PIN"),
            "The report preserves unknown outcomes and text serials and contains no PINs");
    }
}
