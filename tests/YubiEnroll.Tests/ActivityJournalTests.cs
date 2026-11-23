using YubiEnroll.Core;
internal static class ActivityJournalTests
{
    internal static void Run(Action<bool,string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "desk-activity-" + Guid.NewGuid().ToString("N"));
        var prefs = Path.Combine(root, "prefs.json");
        try
        {
            var journal = new ActivityJournal(prefs, root);
            var entry = new ActivityEvent(DateTimeOffset.Now, "Info", "Passkey removed from YubiKey · serial 123.",
                "HTTP status: 403\nTemporary PIN: ci-only\nBearer ci-token");
            journal.Append(entry);
            var reopened = new ActivityJournal(prefs, root);
            check(reopened.Read().Single().Detail == "HTTP status: 403", "Activity survives restart without transcripts or sensitive fields");
            var log = Directory.GetFiles(journal.DefaultDirectory, "*.jsonl").Single();
            File.AppendAllText(log, "{incomplete");
            var old = Path.Combine(journal.DefaultDirectory, "activity-2000-01-01-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var unrelated = Path.Combine(journal.DefaultDirectory, "activity-2000-01-01-not-a-session.jsonl");
            File.Copy(log, old); File.Copy(log, unrelated);
            check(reopened.Read().Count == 1 && !File.Exists(old) && File.Exists(unrelated), "Pruning only affects recognizable expired activity files; a partial tail preserves earlier rows");
            var other = Path.Combine(root, "other");
            reopened.Configure(true, other, 7); reopened.Append(entry with { Message = "New folder" });
            check(new ActivityJournal(prefs, root).Read().Single().Message == "New folder" && File.Exists(log), "Changing location persists the choice without moving earlier history");
            reopened.Configure(false, other, 7); reopened.Append(entry);
            check(reopened.Read().Count == 1, "Disabling saving preserves existing history");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
