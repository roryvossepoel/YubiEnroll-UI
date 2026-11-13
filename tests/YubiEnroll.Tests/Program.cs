using System.Text.Json;
using YubiEnroll.Core;

if (args.FirstOrDefault() == "--version")
{
    Console.WriteLine("Mock YubiEnroll 0.0.0 (CI only)");
    return 0;
}
if (args.FirstOrDefault() == "--exit-probe") return 7;
if (args.FirstOrDefault() == "--probe")
{
    // More than a pipe buffer on each stream exposes sequential ReadToEnd deadlocks.
    var a = Console.Out.WriteAsync(new string('O', 300000));
    var b = Console.Error.WriteAsync(new string('E', 300000));
    await Task.WhenAll(a, b);
    return 0;
}
if (args.FirstOrDefault() == "--echo")
{
    Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
    return 0;
}

if (args.FirstOrDefault() == "--terminal-probe")
{
    Console.WriteLine("TERMINAL_READY");
    var response = Console.ReadLine();
    Console.WriteLine(response == "confirm" ? "INPUT_OK" : "INPUT_FAILED");
    Console.Write("\x1b[32mVT_OK\x1b[0m\r\n");
    var inputHandle = ConsoleProbe.GetStdHandle(-10);
    if (!ConsoleProbe.GetConsoleMode(inputHandle, out var mode) || !ConsoleProbe.SetConsoleMode(inputHandle, (mode | 2u) & ~4u)) return 10;
    Console.WriteLine("HIDDEN_READY");
    var hidden = Console.ReadLine();
    ConsoleProbe.SetConsoleMode(inputHandle, mode);
    Console.WriteLine(hidden == "synthetic-pin" ? "HIDDEN_OK" : "HIDDEN_FAILED");
    return response == "confirm" && hidden == "synthetic-pin" ? 7 : 9;
}

if (args.FirstOrDefault() == "--write-fixture-config")
{
    // Dedicated CI mode: caller supplies an isolated synthetic path, never reads a real tenant.
    TenantConfigWriter.Save(ConfigSnapshot.Read(args[1]), new TenantSetup("UI-test",
        "11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "http://localhost/yubienroll-redirect", "UI-test-profile", true));
    ProfileConfigWriter.Save(ConfigSnapshot.Read(args[1]), new ProfileSetup("Second profile", MinPinLength: 8, RandomPinLength: 10));
    ProfileConfigWriter.Save(ConfigSnapshot.Read(args[1]), new ProfileSetup("UI-test-profile", Reset: true, ForcePinChange: true), "UI-test-profile");
    var fixture = ConfigSnapshot.Read(args[1]);
    ConfigFileWriter.Save(fixture, ConfigTransfer.ValidateImport(ConfigTransfer.Export(fixture.Text!)));
    Console.WriteLine("Synthetic provider and multiple profiles written, edited and transferred.");
    return 0;
}

if (args.FirstOrDefault() == "--rename-fixture-config")
{
    var snapshot = ConfigSnapshot.Read(args[1]);
    var text = TenantConfigWriter.Edit(snapshot.Text!, TenantConfigWriter.Read(snapshot.Text!, "UI-test") with { Name = "UI test renamed" }, "UI-test");
    text = ProfileConfigWriter.Build(text, ProfileConfigWriter.Read(text, "UI-test-profile") with { Name = "Renamed profile" }, "UI-test-profile");
    ConfigFileWriter.Save(snapshot, text);
    return 0;
}
if (args.FirstOrDefault() == "--remove-fixture-config")
{
    var snapshot = ConfigSnapshot.Read(args[1]);
    ConfigFileWriter.Save(snapshot, TenantConfigWriter.Remove(snapshot.Text!, "UI test renamed"));
    return 0;
}

// Read-only compatibility check against the separately extracted official CLI in Windows CI.
if (args.Length == 2 && args[0] == "--check-ykman")
{
    var native = new CliRunner(args[1]);
    var service = new KeyResetService(a => native.CaptureAsync(a), _ => throw new InvalidOperationException("Reset is forbidden in CI."));
    Console.WriteLine($"Native ykman version parsed: {await service.CheckVersionAsync()}");
    // --help exits without invoking the reset callback or opening a device.
    var help = await native.CaptureAsync("--device", "12345678", "fido", "reset", "--help");
    if (help.ExitCode != 0 || !help.Output.Contains("Reset all FIDO applications"))
        throw new Exception("Native reset help did not accept the serial-targeted command.");
    var inventory = await service.DiscoverAsync();
    if (inventory.Keys.Count != 0) throw new Exception("Unexpected hardware on CI runner.");
    Console.WriteLine("PASS: native ykman version, empty USB inventory and serial-targeted reset help; no reset executed.");
    return 0;
}

var tests = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    tests++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
    { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

const string id = "11111111-2222-3333-4444-555555555555";
var configText = """
active_provider = "Demo tenant"
[providers."Demo tenant"]
provider = "ENTRA"
tenant_id = "11111111-2222-3333-4444-555555555555"
client_id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
profile = "Standard"
[profiles.Standard]
reset = true # The UI must override this unless explicitly selected.
min_pin_length = 6
force_pin_change = true
[profiles."Alternative profile"]
reset = false
min_pin_length = 8
""";
var config = ConfigReader.Parse(configText);
Check(config.Providers.Single().Name == "Demo tenant", "Quoted TOML provider name");
Check(config.Profiles.Count == 2, "Full TOML parser accepts inline comments and profile names with spaces");
Check(config.Profiles[0].Summary.Contains("min_pin_length: 6"), "Profile values preserved");
ConfigReader.EnsureUnchanged(config, config.Providers[0], config.Profiles[1]);
Check(true, "Non-default profile accepted without reset to default");
Reject(() => ConfigReader.EnsureUnchanged(ConfigReader.Parse(configText.Replace("min_pin_length = 8", "min_pin_length = 9")),
    config.Providers[0], config.Profiles[1]), "Changed selected profile is rejected");
Reject(() => ConfigReader.EnsureUnchanged(ConfigReader.Parse(configText.Replace("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "ffffffff-bbbb-cccc-dddd-eeeeeeeeeeee")),
    config.Providers[0], config.Profiles[1]), "Changed provider is rejected");
Reject(() => ConfigReader.Parse("[invalid"), "Malformed TOML rejected");
Reject(() => ConfigReader.Parse(configText.Replace(id, "not-a-tenant")), "Invalid Entra tenant rejected");

var users = UserSearch.Parse($"ID  Display Name Username Email\n{id}  Demo User demo@example.test other@example.test\n");
Check(users.Count == 1 && users[0].Id == id, "Object ID used when email differs from username");
Check(UserSearch.Parse("demo@example.test Error: lookup failed").Count == 0, "No email fallback");
Check(UserSearch.Parse($"Error: request-id {id}").Count == 0, "Diagnostic GUID is not a user");
Check(UserSearch.Parse($"│ {id} │ Demo │ demo@example.test │").Count == 1, "Rich table row accepted");
Check(UserSearch.Parse($"ID: {id}\nDisplay Name: Demo User\nUsername: demo@example.test")[0].Details.Contains("Demo User"), "Labelled user block");
Check(UserSearch.Parse($"{id} Demo\n{id} Demo").Count == 1, "Repeated user IDs deduplicated");
Check(UserSearch.Parse("00000000-0000-0000-0000-000000000000 Empty").Count == 0, "Empty GUID rejected");

var command = EnrollmentCommand.Build(id, "Alternative profile", "Demo key", false);
Check(command.Contains("--no-reset") && !command.Contains("--reset"), "Explicit false profile policy sends no-reset");
Check(command[Array.IndexOf(command, "--profile") + 1] == "Alternative profile", "Chosen profile retained");
Check(!command.Contains("-f") && !command.Contains("--force"), "Native confirmation prompts retained");
Check(EnrollmentCommand.Build(id, "Standard", "", true).Contains("--reset"), "True profile policy sends reset");
Reject(() => EnrollmentCommand.Build("name@example.test", "Standard", "", false), "Enrollment requires selected object ID");
Reject(() => EnrollmentCommand.Build(id, "", "", false), "Enrollment requires explicit profile");

var runner = new CliRunner(Environment.ProcessPath!);
var pipeResult = await runner.CaptureAsync("--probe");
Check(pipeResult.ExitCode == 0 && pipeResult.Output.Length == 300000 && pipeResult.Error.Length == 300000,
    "Both output streams drained concurrently");
string[] special = ["space in name", "quote\"test", "& whoami", "%PATH%", "trailing\\", "--switch-looking-value"];
var echo = await runner.CaptureAsync(new[] { "--echo" }.Concat(special).ToArray());
var roundtrip = JsonSerializer.Deserialize<string[]>(echo.Output);
Check(echo.ExitCode == 0 && roundtrip != null && roundtrip.SequenceEqual(special), "Arguments round-trip literally without a shell");
var missingFolder = FailureReport.Create(new DirectoryNotFoundException(), "Load configuration", "test/config.toml", "test/cli.exe");
Check(missingFolder.Summary.Contains("folder") && missingFolder.Diagnostic.Contains("DirectoryNotFoundException"), "Missing folder has a specific diagnostic");
Check(missingFolder.Diagnostic.Contains("Step: Load configuration") && missingFolder.Diagnostic.Contains("Configuration path: test/config.toml"), "Diagnostic identifies step and paths");
var accessDenied = FailureReport.Create(new System.ComponentModel.Win32Exception(5), "Version check", "", "test/cli.exe");
Check(accessDenied.Diagnostic.Contains("Native error: 5") && accessDenied.Summary.Contains("denied"), "Native executable access error identified");
var unknown = FailureReport.Create(new Exception("secret-marker"), "Startup", "", "");
Check(!unknown.Diagnostic.Contains("secret-marker") && !unknown.Summary.Contains("secret-marker"), "Unexpected exception text not copied to diagnostics");
var setup = new TenantSetup("Second tenant", id, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "http://localhost/yubienroll-redirect", "Fresh profile", true);
var added = TenantConfigWriter.Build(configText, setup);
Check(added.Contains(configText) && ConfigReader.Parse(added).Providers.Count == 2, "Add tenant preserves existing comments and providers");
Check(ConfigReader.Parse(added).ActiveProvider == config.ActiveProvider, "Adding a tenant preserves current activation");
Check(ConfigReader.Parse(added).Profiles.Single(x => x.Name == "Fresh profile").Summary.Contains("reset: False"), "New profile does not reset keys");
Check(ConfigReader.Parse(TenantConfigWriter.Build(null, setup)).ActiveProvider == setup.Name, "First tenant becomes active in new configuration");
Check(ConfigReader.Parse(TenantConfigWriter.Build(configText, setup with { CreateProfile = false, ProfileName = "Standard" })).Profiles.Count == 2,
    "Existing profile reused without modification");
Reject(() => TenantConfigWriter.Build(configText, setup with { Name = "demo TENANT" }), "Duplicate provider names rejected without overwrite");
Reject(() => TenantConfigWriter.Build(configText, setup with { ProfileName = "Standard" }), "Duplicate new profile rejected");
Reject(() => TenantConfigWriter.Build(configText, setup with { CreateProfile = false }), "Missing selected profile rejected");
Reject(() => TenantConfigWriter.Build("[broken-secret-marker", setup), "Malformed existing configuration is not overwritten");
Reject(() => (setup with { TenantId = Guid.Empty.ToString() }).Validate(), "Empty tenant ID rejected");
Reject(() => (setup with { ClientId = "not-a-guid" }).Validate(), "Invalid client ID rejected");
Reject(() => (setup with { RedirectUri = "http://localhost.evil.test/" }).Validate(), "Redirect URI must use localhost");
Reject(() => (setup with { PinLength = 3 }).Validate(), "Invalid PIN policy rejected");
var quoted = TenantConfigWriter.Build(null, setup with { Name = "Name with \"quotes\" and dots.", ProfileName = "Profile.with.dots" });
Check(ConfigReader.Parse(quoted).Providers.Single().Name == "Name with \"quotes\" and dots.", "Tenant and profile names are TOML-escaped");
// Two tenants share one profile; a second profile remains independent.
var shared = TenantConfigWriter.Build(configText, setup with { CreateProfile = false, ProfileName = "Standard" });
var policy = new ProfileSetup("Standard", Reset: true, ForcePinChange: true);
var edited = ProfileConfigWriter.Build(shared, policy, "Standard");
Check(ProfileConfigWriter.Read(edited, "Standard") == policy, "All seven supplied profile settings round-trip");
Check(ConfigReader.Parse(edited).Providers.SequenceEqual(ConfigReader.Parse(shared).Providers), "Shared tenant references and provider values survive profile editing");
Check(ConfigReader.Parse(edited).ActiveProvider == config.ActiveProvider, "Profile edit preserves active tenant");
var separate = new ProfileSetup("Independent profile", MinPinLength: 8, RandomPinLength: 10);
var multiple = ProfileConfigWriter.Build(edited, separate);
Check(ConfigReader.Parse(multiple).Profiles.Count == 3 && ProfileConfigWriter.Read(multiple, "Standard") == policy,
    "Multiple profiles coexist without modifying existing profiles");
Check(ProfileConfigWriter.Read(multiple, separate.Name) == separate, "Minimum and generated PIN lengths are independent");
var omitted = ProfileConfigWriter.Read(configText, "Alternative profile");
Check(omitted.RandomPin == null && omitted.RequireEa == null && omitted.RandomPinLength == null, "Missing options remain CLI defaults");
Check(ProfileConfigWriter.Read(ProfileConfigWriter.Build(configText, omitted, omitted.Name), omitted.Name) == omitted,
    "Editing does not replace omitted options with GUI defaults");
Reject(() => ProfileConfigWriter.Build(multiple, separate with { Name = "INDEPENDENT PROFILE" }), "Duplicate profile names rejected");
var renamedProfile = ProfileConfigWriter.Build(multiple, policy with { Name = "Renamed profile" }, "Standard");
Check(ConfigReader.Parse(renamedProfile).Providers.Where(x => x.Profile == "Renamed profile").Count() == ConfigReader.Parse(multiple).Providers.Where(x => x.Profile == "Standard").Count(), "Profile rename updates every linked tenant atomically");
Check(ProfileConfigWriter.Read(renamedProfile, "Renamed profile") == policy with { Name = "Renamed profile" }, "Renaming preserves profile values and accepts spaces");
Reject(() => ProfileConfigWriter.Build(multiple, policy with { Name = separate.Name }, "Standard"), "Profile rename rejects duplicate names");
Reject(() => ProfileConfigWriter.Remove(multiple, "Standard"), "Linked profile cannot be removed");
Check(!ConfigReader.Parse(ProfileConfigWriter.Remove(multiple, separate.Name)).Profiles.Any(x => x.Name == separate.Name), "Unused profile can be removed");
Reject(() => ProfileConfigWriter.Build(multiple, separate with { RandomPinLength = 6 }), "Generated PIN cannot be shorter than minimum");
Reject(() => ProfileConfigWriter.Build(multiple, separate with { MinPinLength = 64 }), "Profile PIN limit enforced");
var endpoints = shared.Replace("provider = \"ENTRA\"", "provider = \"ENTRA\"\nentra_base_url = \"https://login.microsoftonline.com\"\ngraph_base_url = \"https://graph.microsoft.com\"");
Check(ConfigReader.Parse(ProfileConfigWriter.Build(endpoints, policy, "Standard")).Providers.SequenceEqual(ConfigReader.Parse(endpoints).Providers),
    "Custom provider endpoints are preserved");
var unspecifiedReset = EnrollmentCommand.Build(id, "Standard", "", null);
Check(!unspecifiedReset.Contains("--reset") && !unspecifiedReset.Contains("--no-reset"), "Omitted reset remains controlled by native profile/default prompt");
var transferable = TenantConfigWriter.Build(null, setup);
transferable = TenantConfigWriter.Build(transferable, setup with { Name = "Shared tenant", CreateProfile = false });
var exported = ConfigTransfer.Export(transferable);
Check(ConfigReader.Parse(exported).Providers.Count == 2 && ConfigReader.Parse(exported).Profiles.Count == 1,
    "Export/import retains multiple tenants sharing a profile");
Check(ConfigTransfer.ValidateImport(exported) == exported, "Export is valid import input");
Check(!ConfigTransfer.Export(transferable + "\n[private_cache]\nsecret = \"marker-token\"\n").Contains("marker-token"), "Export excludes unrelated data and auth fields");
Reject(() => ConfigTransfer.ValidateImport(exported.Replace("client_id =", "client_secret =")), "Import rejects unsupported secret fields");
Reject(() => ConfigTransfer.ValidateImport(exported.Replace("profile = \"Fresh profile\"", "profile = \"Missing\"")), "Import rejects dangling profile references");
Reject(() => ConfigTransfer.ValidateImport("[broken"), "Import rejects malformed input");
Reject(() => ConfigTransfer.ValidateImport(""), "Empty import cannot wipe configuration");
var changedTenant = setup with { CreateProfile = false, ProfileName = "Standard" };
var reassigned = TenantConfigWriter.Edit(shared, changedTenant, setup.Name);
Check(ConfigReader.Parse(reassigned).Providers.Single(x => x.Name == setup.Name).Profile == "Standard", "Tenant editor can assign an existing profile");
Check(ConfigReader.Parse(reassigned).Profiles.Count == ConfigReader.Parse(shared).Profiles.Count, "Tenant editing retains shared profiles");
var changedWithEndpoints = TenantConfigWriter.Edit(endpoints, changedTenant, setup.Name);
Check(changedWithEndpoints.Contains("graph_base_url") && changedWithEndpoints.Contains("entra_base_url"), "Tenant edit preserves custom cloud endpoints");
var tempRoot = Path.Combine(Path.GetTempPath(), "YubiEnroll-setup-test-" + Guid.NewGuid().ToString("N"));
try
{
    var tempPath = Path.Combine(tempRoot, "config", "yubienroll.toml");
    var empty = ConfigSnapshot.Read(tempPath);
    Check(TenantConfigWriter.Save(empty, setup) == null && File.Exists(tempPath), "First save creates the configuration directory and file");
    var first = ConfigSnapshot.Read(tempPath);
    var backup = TenantConfigWriter.Save(first, setup with { Name = "Another", CreateProfile = false });
    Check(backup != null && File.ReadAllText(backup) == first.Text && ConfigReader.Read(tempPath).Providers.Count == 2,
        "Second save backs up the exact previous configuration");
    var profileSnapshot = ConfigSnapshot.Read(tempPath);
    var profileBackup = ProfileConfigWriter.Save(profileSnapshot, separate);
    Check(profileBackup != null && File.ReadAllText(profileBackup) == profileSnapshot.Text, "Profile save backs up exact original content");
    Reject(() => ProfileConfigWriter.Save(profileSnapshot, policy), "Stale profile dialog cannot overwrite changes");
    var after = File.ReadAllText(tempPath);
    Reject(() => TenantConfigWriter.Save(first, setup with { Name = "Stale", CreateProfile = false }), "Stale dialog cannot overwrite configuration");
    Check(File.ReadAllText(tempPath) == after, "Rejected stale save leaves file unchanged");
}
finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
Check(KeyResetService.ParseVersion("YubiKey Manager (ykman) version: 5.9.2\r\n") == new Version(5, 9, 2), "Official ykman version format accepted");
Reject(() => KeyResetService.ParseVersion("YubiKey Manager (ykman) version: 5.7.2"), "Old ykman rejected before reset");
Reject(() => KeyResetService.ParseVersion("YubiKey Manager (ykman) version: 6.0.0"), "Unknown ykman major rejected");
Reject(() => KeyResetService.ParseVersion("YubiKey Manager (ykman) version: 5.9.2-dev"), "Unverified development version rejected");
const string keyRow = "YubiKey 5 NFC (5.7.1) [OTP+FIDO+CCID] Serial: 12345678";
var keys = KeyResetService.ParseKeys(keyRow + "\nSecurity Key NFC (5.7.1) [FIDO]\nYubiKey [FIDO] <access denied>\nYubiKey 5 [OTP+CCID] Serial: 87654321");
Check(keys.Keys.Single().Serial == "12345678" && keys.Unavailable == 3, "Only serial-addressable FIDO devices can be selected");
Check(KeyResetService.ParseKeys("").Keys.Count == 0, "Empty device list is a normal state");
Check(KeyResetService.ParseKeys("Error: " + keyRow).Keys.Count == 0, "Diagnostic lines are not accepted as devices");
Reject(() => KeyResetService.ParseKeys(keyRow + "\n" + keyRow), "Duplicate key identities block reset");
Reject(() => KeyResetService.ResetArguments("12345678 --force"), "Serial cannot inject options");
Reject(() => KeyResetService.ResetArguments("0"), "Missing serial cannot target an arbitrary key");
Reject(() => KeyResetService.ResetArguments("4294967296"), "Out-of-range serial rejected");
Check(KeyResetService.ResetArguments("12345678").SequenceEqual(new[] { "--device", "12345678", "fido", "reset" }),
    "Standalone reset pins serial and retains native confirmation without force");

async Task ExerciseReset(string scenario, bool approved, bool removeAfterConfirm, bool infoFails, bool staleBefore, int nativeExit)
{
    var inventory = staleBefore ? "" : keyRow;
    var launched = 0;
    var confirmations = 0;
    var service = new KeyResetService(a => Task.FromResult(a[0] switch
    {
        "--version" => new CliResult(0, "YubiKey Manager (ykman) version: 5.9.2", ""),
        "list" => new CliResult(0, inventory, ""),
        "--device" when a.SequenceEqual(new[] { "--device", "12345678", "fido", "info" }) => new CliResult(infoFails ? 1 : 0, "", ""),
        _ => throw new Exception("Unexpected read command")
    }), a =>
    {
        Check(a.SequenceEqual(KeyResetService.ResetArguments("12345678")), scenario + ": serial retained through launch");
        launched++;
        return Task.FromResult(nativeExit);
    });
    int? result = null;
    var blocked = false;
    try
    {
        result = await service.ResetAsync(keys.Keys.Single(), _ =>
        {
            confirmations++;
            if (removeAfterConfirm) inventory = keyRow.Replace("12345678", "87654321");
            return approved;
        });
    }
    catch (InvalidOperationException) { blocked = true; }
    var expectedBlock = staleBefore || infoFails || (approved && removeAfterConfirm);
    var expectedLaunch = approved && !expectedBlock;
    Check(blocked == expectedBlock && launched == (expectedLaunch ? 1 : 0), scenario + ": reset invocation count");
    Check(confirmations == (staleBefore || infoFails ? 0 : 1), scenario + ": read checks precede confirmation");
    Check(result == (expectedLaunch ? (int?)nativeExit : null), scenario + ": exit status preserved without retry");
}
await ExerciseReset("Cancellation", false, false, false, false, 0);
await ExerciseReset("Key swapped during confirmation", true, true, false, false, 0);
await ExerciseReset("FIDO access denied", true, false, true, false, 0);
await ExerciseReset("Disconnected before confirmation", true, false, false, true, 0);
await ExerciseReset("Confirmed reset", true, false, false, false, 0);
await ExerciseReset("Native reset failure", true, false, false, false, 1);
Check(KeyResetService.ParsePinState("AAGUID: example\r\nPIN:                Not set\r\nMinimum PIN length: 4") == FidoPinState.NotSet,
    "Aligned native FIDO output recognizes an unset PIN");
Check(KeyResetService.ParsePinState("PIN: 8 attempt(s) remaining") == FidoPinState.Set, "Remaining attempts mean a PIN is configured");
Check(KeyResetService.ParsePinState("PIN: 1 attempt(s) remaining\nPIN is temporarily blocked.") == FidoPinState.Set, "Temporary lock does not imply PIN removed");
Check(KeyResetService.ParsePinState("PIN: Blocked") == FidoPinState.Blocked, "Blocked PIN remains set");
Check(KeyResetService.ParsePinState("PIN: Disabled") == FidoPinState.Disabled, "Disabled application is distinct from unset PIN");
Check(KeyResetService.ParsePinState("PIN: Not supported") == FidoPinState.Unsupported, "Unsupported PIN is distinct from unset PIN");
Check(KeyResetService.ParsePinState("PIN: Set") == FidoPinState.Set, "Legacy explicit PIN status supported");
Check(KeyResetService.ParsePinState("NOTE: PIN: Not set") == FidoPinState.Unknown, "Diagnostic text cannot claim PIN removal");
Check(KeyResetService.ParsePinState("PIN: Not set\nPIN: 8 attempt(s) remaining") == FidoPinState.Unknown, "Contradictory PIN fields remain unknown");
Check(KeyResetService.ParsePinState("PIN: Unexpected format") == FidoPinState.Unknown &&
    KeyResetService.ParsePinState("") == FidoPinState.Unknown, "Missing and unfamiliar PIN status never imply no PIN");
var statusReader = new KeyResetService(a =>
{
    Check(a.SequenceEqual(new[] { "--device", "12345678", "fido", "info" }), "PIN status uses the selected serial and only fido info");
    return Task.FromResult(new CliResult(0, "PIN: Not set", ""));
}, _ => throw new Exception("Status reading must never launch an interactive operation"));
Check(await statusReader.ReadPinStateAsync(keys.Keys.Single()) == FidoPinState.NotSet, "Read-only status result reaches caller");
var tenantRename = TenantConfigWriter.Read(shared, setup.Name) with { Name = "Renamed tenant" };
var renamedTenant = TenantConfigWriter.Edit(shared, tenantRename, setup.Name);
Check(ConfigReader.Parse(renamedTenant).Providers.Any(x => x.Name == "Renamed tenant") && !ConfigReader.Parse(renamedTenant).Providers.Any(x => x.Name == setup.Name), "Tenant rename replaces local name without duplication");
var firstTenantText = TenantConfigWriter.Build(null, setup);
var activeRenamed = TenantConfigWriter.Edit(firstTenantText, setup with { Name = "Active renamed", CreateProfile = false }, setup.Name);
Check(ConfigReader.Parse(activeRenamed).ActiveProvider == "Active renamed", "Renaming active tenant updates active_provider");
var lastRemoved = TenantConfigWriter.Remove(activeRenamed, "Active renamed");
Check(ConfigReader.Parse(lastRemoved).Providers.Count == 0 && ConfigReader.Parse(lastRemoved).Profiles.Count == 1 && !lastRemoved.Contains("active_provider"), "Removing last tenant clears activation and preserves profiles");
var screen = new TerminalScreen(20, 4);
screen.Feed("hello\r\nPIN: "); screen.Feed("\x1b[32mNot set\x1b[0m");
Check(screen.Text.Contains("PIN: Not set") && !screen.Text.Contains('\x1b'), "Terminal renders split output and strips display attributes");
screen.Feed("\r\x1b[2KDone");
Check(screen.Text.EndsWith("Done") && !screen.Text.Contains("Not set"), "Terminal carriage return and erase line remove stale output");
screen.Feed("\x1b]52;c;do-not-copy\a");
Check(!screen.Text.Contains("do-not-copy"), "Terminal ignores clipboard control sequences");
screen.Clear(); Check(screen.Text.Length == 0, "Terminal buffers clear after dismissal");
Check(WindowsCommandLine.Quote("name with spaces") == "\"name with spaces\"", "Native process argument quoting preserves spaces");
Check(WindowsCommandLine.Quote("x\"y") == "\"x\\\"y\"", "Native process argument quoting escapes quotes");
var registrations = RegisteredCredentials.Parse("Fetching credentials...\nDisplay name  Model  ID\nDesk key  YubiKey 5 NFC  credential-1\nBackup key  Security Key  credential-2\n");
Check(registrations.Count == 2 && registrations[0].Name == "Desk key" && registrations[1].Id == "credential-2", "Registered keys table preserves names and credential identifiers");
Check(RegisteredCredentials.Parse("unrecognized output\nkey  model  id").Count == 0, "Unknown registration format is not guessed");
var failure = InteractiveFailure.Describe("ERROR: 405 Method Not Allowed\nrequest-id: 11111111-2222-3333-4444-555555555555\nPIN: secret-marker\naccess_token: secret-token");
Check(failure.Contains("405") && failure.Contains("11111111-2222-3333-4444-555555555555") && !failure.Contains("secret") && !failure.Contains("PIN"), "Interactive diagnostic extraction keeps codes and excludes PINs and tokens");
Check(new ResetKey("12345678", "YubiKey 5C (5.7.1)").RegistrationName == "YubiKey 5C (12345678)", "Suggested registration name omits firmware and includes serial");
Check(new ResetKey("87654321", "Security Key NFC").RegistrationName == "Security Key NFC (87654321)", "Suggested registration name preserves reported model");
Check(InteractiveFailure.Describe("PIN: 123456\nLogin successful").Length == 0, "No terminal content logged for successful operation");
Check(!new UserCandidate(id, id + "  Alex Example  alex@example.test  None").Label.Contains("None"), "Empty optional user column is hidden");
var exitFile = Path.GetTempFileName();
try
{
    File.WriteAllText(exitFile, "2");
    Check(CliRunner.ReadNativeOutcome(exitFile, -1073741510) == 2, "Native failure survives closing the console host");
    File.WriteAllText(exitFile, "0");
    Check(CliRunner.ReadNativeOutcome(exitFile, -1073741510) == 0, "Completed native success survives closing the console host");
    File.WriteAllText(exitFile, "");
    Check(CliRunner.ReadNativeOutcome(exitFile, 0) != 0, "Missing native outcome cannot be interpreted as success");
}
finally { File.Delete(exitFile); }
var targetKey = new ResetKey("12345678", "YubiKey 5C (5.7.1)");
var adminKey = new ResetKey("87654321", "YubiKey 5C (5.7.1)");
Check(!EnrollmentTargetGuard.CanStart(null, new[] { targetKey }, 0), "No implicit enrollment target for a single connected device");
Check(EnrollmentTargetGuard.CanStart(targetKey, new[] { targetKey }, 0), "Single target permits the explicitly documented legacy CLI flow");
Check(!EnrollmentTargetGuard.CanStart(targetKey, new[] { targetKey, adminKey }, 0), "Admin and target connected blocks untargeted enrollment");
Check(!EnrollmentTargetGuard.CanStart(targetKey, new[] { adminKey }, 0), "Missing target is never substituted by admin key");
Check(!EnrollmentTargetGuard.CanStart(targetKey, new[] { targetKey }, 1), "Unreadable extra device blocks enrollment");
Check(!EnrollmentTargetGuard.CanStart(targetKey, Array.Empty<ResetKey>(), 0), "Disconnected target blocks enrollment");
Check(!EnrollmentTargetGuard.CanStart(targetKey, new[] { targetKey, targetKey }, 0), "Ambiguous device inventory blocks enrollment");
Check(!EnrollmentTargetGuard.CanStart(targetKey, new[] { targetKey with { Model = "Other model" } }, 0), "Changed target identity requires verification");
try { EnrollmentTargetGuard.Require(targetKey, new(new[] { adminKey }, 0)); throw new Exception("Missing target accepted"); }
catch (InvalidOperationException) { Check(true, "Fresh preflight rejects target removal"); }
Check(EnrollmentTargetGuard.Explain(targetKey, new[] { targetKey, adminKey }, 0, false).Contains("Use your administrator key"), "Two keys before login explain administrator sign-in first");
Check(EnrollmentTargetGuard.Explain(targetKey, new[] { targetKey, adminKey }, 0, true).Contains($"serial: {adminKey.Serial}") &&
    EnrollmentTargetGuard.Explain(targetKey, new[] { targetKey, adminKey }, 0, true).Contains($"keep target {targetKey.Serial}"), "After login guidance distinguishes admin from target serial");
Check(EnrollmentTargetGuard.Explain(targetKey, new[] { targetKey }, 0, true).Contains("Only the target is connected"), "Single-key continuation states the connected-key requirement");
Console.WriteLine($"{tests} checks passed.");
return 0;

internal static class ConsoleProbe
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
