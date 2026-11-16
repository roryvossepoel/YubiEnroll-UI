using System.Text.Json;
using YubiEnroll.Core;

internal static class Workflow056Tests
{
    internal static void Run(Action<bool, string> check)
    {
        var tenant = new TenantSetup("Example", "11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "http://localhost/yubienroll-redirect", "", false);
        var prompt = $"Authenticating to ENTRA using the following parameters:\nclient_id: {tenant.ClientId}\nredirect_uri: {tenant.RedirectUri}\ntenant_id: {tenant.TenantId}\nentra_base_url: https://login.microsoftonline.com\ngraph_base_url: https://graph.microsoft.com\nProceed? [y/N]: ";
        var protocol = new LoginProtocol(tenant);
        check(!protocol.TryConfirm("Proceed? [y/N]: ") && protocol.TryConfirm(prompt) && !protocol.TryConfirm(prompt), "Only recognized matching login parameters are confirmed, once");
        bool Reject(Action run) { try { run(); return false; } catch (InvalidOperationException) { return true; } }
        check(Reject(() => new LoginProtocol(tenant).TryConfirm(prompt.Replace(tenant.ClientId, Guid.NewGuid().ToString()))), "Mismatched login client is not automatically approved");
        var url = $"https://login.microsoftonline.com/{tenant.TenantId}/oauth2/v2.0/authorize?client_id={tenant.ClientId}&redirect_uri={Uri.EscapeDataString(tenant.RedirectUri)}&response_type=code&state=synthetic";
        var output = "Go to the following link in your broswer, and complete the sign-in prompts: \n\n" + url;
        check(protocol.AuthorizationUrl(output)?.AbsoluteUri == url, "Native login address validates tenant, client and redirect");
        check(Reject(() => protocol.AuthorizationUrl(output.Replace("login.microsoftonline.com", "example.test"))), "Unexpected browser destination is rejected");
        var screen = new TerminalScreen(32760, 28);
        screen.Feed(output[..^5]);
        check(protocol.AuthorizationUrl(screen.CompletedText) == null, "Partial authorization URL is never launched");
        screen.Feed(output[^5..] + "\r\n");
        check(protocol.AuthorizationUrl(screen.CompletedText)?.AbsoluteUri == url, "Chunked complete URL is reconstructed without truncation");
        const string summary = "Enter PIN: \nYubiKey configuration summary:\nSerial number: 00123456\nTemporary PIN: 001234\n";
        check(GeneratedPinSummary.Read(summary, "00123456") == "001234", "Generated initial PIN is read from final summary, including leading zeroes");
        check(GeneratedPinSummary.Read(summary, "different") == null && GeneratedPinSummary.Read("Enter PIN: 001234", "00123456") == null && GeneratedPinSummary.Read(summary + "Generated PIN: 556677\n", "00123456") == null, "Wrong target, entered PIN and ambiguous output are never captured");
        var versions = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0" }.Select(v => ReleaseVersion.Parse(v)!).ToArray();
        check(versions.Zip(versions.Skip(1)).All(pair => pair.First.CompareTo(pair.Second) < 0) && ReleaseVersion.Parse("v1.0.0+build")!.CompareTo(ReleaseVersion.Parse("1.0.0")) == 0, "Semantic version comparison handles numeric prereleases and build metadata");
        check(ReleaseVersion.Parse("1.0.0-01") == null && ReleaseVersion.Parse("01.0.0") == null && ReleaseVersion.Parse("latest") == null, "Malformed release tags are ignored");
        var releases = ReleaseUpdates.Parse("""[{"draft":true,"tag_name":"v9.0.0"},{"draft":false,"published_at":"2026-01-01","tag_name":"v0.6.0-beta.1","prerelease":true}]""");
        check(releases.Count == 1 && releases[0].Preview && releases[0].Url.Host == "github.com", "Published preview releases are included and drafts excluded");
        var root = Path.Combine(Path.GetTempPath(), "log-pin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "prefs.json"); var folder = Path.Combine(root, "records");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Enabled = true, Directory = folder }));
            var journal = new RegistrationJournal(path);
            check(!journal.Preferences.SaveGeneratedPin && journal.SettingsError == null, "Existing logging preference migrates with PIN storage off");
            var record = RegistrationRecord.Start(new Provider(tenant.Name, "ENTRA", tenant.TenantId, "", ""), new UserCandidate(Guid.NewGuid().ToString(), "Example"), new ResetKey("00123456", "Synthetic key"), new ProfileSetup("Example"), "Example");
            var first = journal.Begin(record);
            journal.Complete(first, 0, "001234");
            check(journal.Read().Single().GeneratedPin == null, "Opt-out refuses a supplied generated PIN");
            journal.Configure(true, folder, true);
            var second = journal.Begin(record with { AttemptId = Guid.NewGuid() });
            journal.Configure(true, folder, false);
            check(journal.Complete(second, 0, "001234"), "PIN preference is snapshotted at enrollment start");
            var saved = new RegistrationJournal(path).Read().Single(r => r.AttemptId == second!.Record.AttemptId);
            check(saved.GeneratedPin == "001234" && RegistrationJournal.Csv([saved]).Contains("\"'001234\""), "Opted-in generated PIN is in the same JSON record and CSV as text");
        }
        finally { Directory.Delete(root, true); }
    }
}
