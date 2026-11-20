using YubiEnroll.Core;

internal static class RegistrationJournalTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string tenantId = "11111111-2222-3333-4444-555555555555";
        const string userId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var root = Path.Combine(Path.GetTempPath(), "YubiEnroll-journal-test-" + Guid.NewGuid().ToString("N"));
        var preferences = Path.Combine(root, "preferences.json");
        var folder = Path.Combine(root, "records");
        var tenant = new Provider("Demo tenant", "ENTRA", tenantId, "Basic", "fingerprint");
        var user = UserSearch.Parse($"ID: {userId}\nDisplay Name: Alex Example\nUsername: alex@example.test\nEmail: different@example.test").Single();
        var key = new ResetKey("00123456", "YubiKey 5 NFC (5.7.1)");
        var record = RegistrationRecord.Start(tenant, user, key, new ProfileSetup("Basic", Reset: false, ForcePinChange: true), "Desk key");
        try
        {
            var documents = Path.Combine(root, "Redirected documents");
            var journal = new RegistrationJournal(preferences, documents);
            check(journal.Preferences.Directory == Path.Combine(documents, "YubiKey Desk", "RegistrationLog") && !journal.Preferences.SaveGeneratedPin,
                "A fresh account uses its Documents folder without opting into PIN storage");
            check(!journal.Preferences.Enabled && journal.Begin(record) == null && !Directory.Exists(root), "Logging is opt-in and writes no user data when disabled");
            journal.Configure(true, folder);
            check(new RegistrationJournal(preferences).Preferences == journal.Preferences, "Logging preference and chosen folder persist across restarts");
            var attempt = journal.Begin(record)!;
            var recovered = new RegistrationJournal(preferences).Read().Single();
            check(recovered.Outcome == RegistrationOutcome.Unknown && recovered.CompletedAt == null, "A crash after starting leaves an unknown attempt, never a successful registration");
            check(recovered.TargetSerial == "00123456" && recovered.DisplayName == "Alex Example" && recovered.UserPrincipalName == "alex@example.test" && recovered.Email == "different@example.test", "Journal preserves confirmed key and separately labelled user identity");
            check(!recovered.ResetBeforeEnrollment && recovered.ForcePinChangeOnFirstUse, "Reset and forced first-use PIN change remain independent columns");
            using (var lease = new FileStream(Path.Combine(folder, ".journal.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                check(!journal.Complete(attempt, 0) && journal.PendingCount == 1, "A result-write failure retains CLI success in memory without repeating enrollment");
                check(journal.Read().Single().Outcome == RegistrationOutcome.Succeeded, "In-memory result supersedes the unresolved disk record in the list and export");
                check(new RegistrationJournal(preferences).Read().Single().Outcome == RegistrationOutcome.Unknown, "Unwritten success is never presented as persisted success after restart");
                var rejected = false;
                try { journal.Configure(true, Path.Combine(root, "other")); } catch (InvalidOperationException) { rejected = true; }
                check(rejected, "Folder cannot change while final outcomes await saving");
            }
            journal.RetryPending(); journal.RetryPending();
            check(journal.PendingCount == 0 && Directory.GetFiles(folder, "*.json").Length == 1 && new RegistrationJournal(preferences).Read().Single().Outcome == RegistrationOutcome.Succeeded, "Saving retry preserves attempt ID and writes exactly one completed record");
            var previous = File.ReadAllText(Path.Combine(folder, record.AttemptId.ToString("N") + ".json"));
            check(!journal.Complete(attempt, 7) && File.ReadAllText(Path.Combine(folder, record.AttemptId.ToString("N") + ".json")) == previous, "Conflicting final result cannot overwrite an already completed registration");
            // Use a fresh service after deliberately testing an irreconcilable pending conflict.
            journal = new RegistrationJournal(preferences);
            var failed = journal.Begin(record with { AttemptId = Guid.NewGuid() });
            journal.Complete(failed, 7);
            var unknown = journal.Begin(record with { AttemptId = Guid.NewGuid() });
            journal.Complete(unknown, null);
            var all = journal.Read();
            check(all.Count == 3 && all.Count(r => r.Outcome == RegistrationOutcome.Succeeded) == 1 && all.Count(r => r.Outcome == RegistrationOutcome.FailedOrInterrupted) == 1 && all.Count(r => r.Outcome == RegistrationOutcome.Unknown) == 1, "Success, failure/interruption and unknown outcomes stay distinct");
            check(RegistrationJournal.Filter(all, tenantId, DateTime.Today, DateTime.Today, true).Count == 1 && RegistrationJournal.Filter(all, Guid.NewGuid().ToString(), null, null).Count == 0, "Date, tenant and success filters select the intended export rows");
            var csv = RegistrationJournal.Csv(all.Concat(all));
            check(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 4 && csv.Contains("\"'00123456\""), "CSV deduplicates attempts and preserves leading-zero serials as text");
            var hostile = record with { DisplayName = "=HYPERLINK(\"bad\")", Email = "+command@example.test", RegistrationName = "a;\"b\nline" };
            var escaped = RegistrationJournal.Csv([hostile]);
            check(escaped.Contains("\"'=HYPERLINK(\"\"bad\"\")\"") && escaped.Contains("\"'+command@example.test\"") && escaped.Contains("\"a;\"\"b\nline\""), "CSV quotes delimiters/newlines and neutralizes spreadsheet formulas");
            check(!previous.Contains("Details") && !previous.Contains("Transcript") && !previous.Contains("Token") && !previous.Contains("RandomPin"), "Persisted schema contains identity and configured flags, not terminal data or generated PINs");
            journal.Configure(false, folder);
            check(journal.Begin(record with { AttemptId = Guid.NewGuid() }) == null && journal.Read().Count == 3, "Disabling logging preserves history and stops future records");
            File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), "{broken");
            var unreadable = false;
            try { journal.Read(); } catch (System.Text.Json.JsonException) { unreadable = true; }
            check(unreadable, "Corrupt history blocks a deceptively incomplete export");
            File.WriteAllText(preferences, "{broken");
            var invalid = new RegistrationJournal(preferences);
            check(invalid.SettingsError != null, "Corrupt preferences are surfaced instead of silently disabling enabled logging");
            var blocked = false;
            try { invalid.Begin(record); } catch (InvalidOperationException) { blocked = true; }
            check(blocked, "Enrollment cannot proceed with unreadable logging preference until explicitly resolved");

            var parsed = UserSearch.Parse($"ID  Display Name  Username  Email\n{userId}  Alex Example  upn@example.test  mail@example.test").Single();
            check(parsed.DisplayName == "Alex Example" && parsed.UserPrincipalName == "upn@example.test" && parsed.Email == "mail@example.test", "Named table columns preserve display name, UPN and a different email");
            var unnamed = UserSearch.Parse($"{userId}  Alex Example  upn@example.test  mail@example.test").Single();
            check(unnamed.DisplayName == "" && unnamed.Email == "" && unnamed.UserPrincipalName == "", "Unlabelled user output never guesses export identity fields");
            var absentEmail = UserSearch.Parse($"ID: {userId}\nUsername: upn@example.test\nEmail: None").Single();
            check(absentEmail.Email == "" && absentEmail.UserPrincipalName == "upn@example.test", "Missing email is not replaced with UPN");
            var fixedHeader = "ID".PadRight(38) + "Display name".PadRight(24) + "Username".PadRight(28) + "Email\n";
            var aligned = UserSearch.Parse(fixedHeader + userId.PadRight(38) + "Alex  Example".PadRight(24) + "upn@example.test".PadRight(28) + "\n").Single();
            check(aligned.DisplayName == "Alex  Example" && aligned.UserPrincipalName == "upn@example.test" && aligned.Email == "", "Fixed-width user columns preserve double spaces and a blank trailing email");
            var blankName = UserSearch.Parse(fixedHeader + userId.PadRight(38) + new string(' ', 24) + "upn@example.test".PadRight(28) + "mail@example.test\n").Single();
            check(blankName.DisplayName == "" && blankName.UserPrincipalName == "upn@example.test" && blankName.Email == "mail@example.test", "Empty display-name column cannot shift UPN into the name field");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
