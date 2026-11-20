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

if (args.FirstOrDefault() == "--pin-summary-probe")
{
    Console.WriteLine("YubiKey configuration summary:");
    Console.WriteLine("Serial number: 00123456");
    Console.WriteLine("Temporary PIN: 001234");
    return 0;
}

if (args.FirstOrDefault() == "--login-probe")
{
    Console.WriteLine("Authenticating to ENTRA using the following parameters:");
    Console.WriteLine("client_id: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    Console.WriteLine("redirect_uri: http://localhost/yubienroll-redirect");
    Console.WriteLine("tenant_id: 11111111-2222-3333-4444-555555555555");
    Console.WriteLine("entra_base_url: https://login.microsoftonline.com");
    Console.WriteLine("graph_base_url: https://graph.microsoft.com");
    Console.Write("Proceed? [y/N]: ");
    if (Console.ReadLine() != "y") return 7;
    Console.WriteLine("Go to the following link in your broswer, and complete the sign-in prompts: \n");
    Console.WriteLine("https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/oauth2/v2.0/authorize?client_id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee&redirect_uri=http%3A%2F%2Flocalhost%3A54321%2Fyubienroll-redirect&response_type=code&state=synthetic");
    await Task.Delay(TimeSpan.FromMinutes(2));
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
    ProfileConfigWriter.Save(ConfigSnapshot.Read(args[1]), PasskeyAddition.Settings(new FidoCapabilities("12345678", "5.4.3", true, true, false, false, false, false, 4, 63, false, true, false)) with { Name = "Preserve existing key" });
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
    var nfcHelp = await native.CaptureAsync("--reader", "HID Global OMNIKEY 5022 Smart Card Reader 0", "fido", "reset", "--help");
    if (nfcHelp.ExitCode != 0 || !nfcHelp.Output.Contains("Reset all FIDO applications")) throw new Exception("NFC reset command rejected.");
    var nfcProbe = await native.CaptureWithInputAsync(TimeSpan.FromSeconds(15), NfcDiscovery.Script, "script", "--force", "-");
    if (nfcProbe.ExitCode != 0 || NfcDiscovery.Parse(nfcProbe.Output).Keys.Count != 0)
        throw new Exception("NFC inventory probe failed: " + nfcProbe.Error);
    Console.WriteLine("PASS: native NFC reader reset syntax and bundled NFC inventory; no hardware operations.");
    var inventory = await service.DiscoverAsync();
    if (inventory.Keys.Count != 0) throw new Exception("Unexpected hardware on CI runner.");
    var probe = await native.CaptureWithInputAsync(TimeSpan.FromSeconds(15), FidoCapabilityProbe.Script, "script", "--force", "-", "12345678");
    // Hosted runners can report unavailable PC/SC service on stderr. Exit 4 is the
    // bundled probe's explicit absent/ambiguous-target outcome, not a CLI/import failure.
    if (probe.ExitCode != 4 || probe.Output.Length != 0)
        throw new Exception($"Bundled read-only probe failed in native ykman: exit {probe.ExitCode}; output: {probe.Output}; error: {probe.Error}");
    Console.WriteLine("PASS: bundled capability script runs through native ykman without Python/SDK installation and rejects an absent target.");
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

// Capability-based compatibility: firmware is identity metadata, never a substitute for CTAP options.
const string modernCapabilities = """
{"schema":1,"forcePinChange":false,"serial":"12345678","firmware":"5.7.1","options":{"clientPin":false,"rk":true,"authnrCfg":true,"setMinPINLength":true,"alwaysUv":false},"minimumPinLength":4,"maximumPinLength":63,"resetBlocked":false,"resetOverUsb":true,"alwaysUvLocked":false}
""";
var capTarget = new ResetKey("12345678", "YubiKey 5 NFC (5.7.1)");
var modernCap = FidoCapabilities.Parse(modernCapabilities, capTarget);
var oldCap = modernCap with { Firmware = "5.1.2", PinPolicy = false, AlwaysUvSupported = false };
var basicProfile = ProfileCompatibility.Basic("Older keys");
var managedProfile = ProfileCompatibility.Managed("Managed keys");
Check(ProfileCompatibility.Evaluate(basicProfile, oldCap).Compatible, "Basic profile supports older FIDO2 keys with a generated initial PIN");
Check(!ProfileCompatibility.Evaluate(managedProfile, oldCap).Compatible, "Older key rejects advanced PIN policy");
Check(ProfileCompatibility.Evaluate(managedProfile, modernCap).Compatible, "Modern CTAP capabilities permit managed PIN profile");
Check(ProfileCompatibility.Evaluate(managedProfile, modernCap with { Firmware = "5.5.0" }).Compatible, "Capabilities, not an ambiguous 5.5 version threshold, determine support");
Check(!ProfileCompatibility.Evaluate(managedProfile, modernCap with { PinPolicy = false }).Compatible, "New firmware without advertised PIN policy is not assumed compatible");
Check(!ProfileCompatibility.Evaluate(basicProfile, null).Compatible, "Missing probe fails closed even for a basic profile");
Check(modernCap.ClientPin, "clientPin false means supported but not yet configured");
Check(!ProfileCompatibility.Evaluate(basicProfile, modernCap with { ClientPin = false }).Compatible, "FIDO2 PIN support is required");
Check(!ProfileCompatibility.Evaluate(basicProfile, modernCap with { ResidentKey = false }).Compatible, "Discoverable credential support is required");
Check(!ProfileCompatibility.Evaluate(basicProfile with { ForcePinChange = true }, oldCap).Compatible, "Force PIN change checked independently of minimum length");
Check(!ProfileCompatibility.Evaluate(basicProfile with { MinPinLength = 4 }, oldCap).Compatible, "Explicit minimum still requires capability even when equal to factory floor");
Check(!ProfileCompatibility.Evaluate(basicProfile with { RequireAlwaysUv = true }, oldCap).Compatible, "Always UV request rejected on unsupported key");
Check(!ProfileCompatibility.Evaluate(basicProfile with { RequireEa = true }, modernCap).Compatible, "New firmware alone does not establish enterprise provisioning");
Check(ProfileCompatibility.Evaluate(basicProfile with { RequireEa = true }, modernCap with { EnterpriseAttestation = true }).Compatible, "Provisioned but disabled enterprise attestation is supported");
Check(!ProfileCompatibility.Evaluate(basicProfile, modernCap with { AlwaysUvLocked = true }).Compatible, "FIPS locked Always UV cannot be disabled");
Check(ProfileCompatibility.Evaluate(basicProfile with { RequireAlwaysUv = true }, modernCap with { AlwaysUvLocked = true }).Compatible, "Locked Always UV can be required");
Check(!ProfileCompatibility.Evaluate(basicProfile with { Reset = true }, modernCap with { ResetBlocked = true }).Compatible, "Reset blocked by another application prevents enrollment reset");
Check(!ProfileCompatibility.Evaluate(basicProfile with { Reset = true }, modernCap with { ResetOverUsb = false }).Compatible, "Reset transport restriction checked");
Check(ProfileCompatibility.Evaluate(basicProfile, modernCap with { ResetBlocked = true }).Compatible, "Non-reset profile does not require reset capability");
Check(!ProfileCompatibility.Evaluate(basicProfile, oldCap, FidoPinState.Blocked).Compatible, "Blocked PIN requires an explicit reset profile");
Check(ProfileCompatibility.Evaluate(basicProfile with { Reset = true }, oldCap, FidoPinState.Blocked).Compatible, "Legacy reset is distinct from unsupported forced PIN change");
Check(!ProfileCompatibility.Evaluate(basicProfile with { RandomPinLength = 6 }, modernCap with { MinimumPinLength = 8 }).Compatible, "Generated PIN respects the actual key minimum");
Check(!ProfileCompatibility.Evaluate(basicProfile with { RandomPinLength = 10 }, modernCap with { MaximumPinLength = 8 }).Compatible, "Bio shared PIN upper limit enforced");
Check(!ProfileCompatibility.Evaluate(managedProfile, modernCap with { MinimumPinLength = 8 }).Compatible, "Requested policy may not lower the reported key minimum");
Check(!ProfileCompatibility.Evaluate(basicProfile with { ForcePinChange = null }, modernCap).Compatible, "Ambiguous CLI defaults require an explicit choice");
Check(!ProfileCompatibility.Evaluate(basicProfile with { RandomPinLength = null }, modernCap).Compatible, "Random PIN length must be known before enrollment");
Check(ProfileCompatibility.Evaluate(basicProfile with { RandomPin = false, RandomPinLength = null }, oldCap).Compatible, "Manual PIN entry does not require a generated length");
Reject(() => FidoCapabilities.Parse(modernCapabilities.Replace("12345678", "87654321"), capTarget), "Capability snapshot from another serial rejected");
Reject(() => FidoCapabilities.Parse(modernCapabilities.Replace("5.7.1", "5.7.2"), capTarget), "Capability firmware must match selected device identity");
Reject(() => FidoCapabilities.Parse(modernCapabilities.Replace("\"schema\":1", "\"schema\":2"), capTarget), "Unknown probe schema rejected");
Reject(() => FidoCapabilities.Parse(modernCapabilities.Replace("\"resetBlocked\":false,", ""), capTarget), "Incomplete probe is not treated as supported");
Reject(() => FidoCapabilities.Parse(modernCapabilities.Replace("\"rk\":true", "\"rk\":\"true\""), capTarget), "Malformed capability type rejected");
Reject(() => FidoCapabilities.Parse(modernCapabilities.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), capTarget), "Duplicate probe fields rejected");
Reject(() => FidoCapabilities.Parse("not-json", capTarget), "Malformed capability output rejected");
Check(!FidoCapabilities.Parse(modernCapabilities.Replace("\"setMinPINLength\":true,", ""), capTarget).PinPolicy, "Missing optional CTAP capability means unsupported");
Check(FidoCapabilities.Parse(modernCapabilities.Replace("\"alwaysUv\":false", "\"alwaysUv\":false,\"ep\":false"), capTarget).EnterpriseAttestation, "False EA flag means available but not enabled");
var basicToml = ProfileConfigWriter.Build(null, basicProfile);
Check(ProfileConfigWriter.Read(basicToml, basicProfile.Name) == basicProfile && !basicToml.Contains("min_pin_length"), "Basic preset round-trips without a minimum-policy override");
Check(ProfileCompatibility.Requirements(managedProfile).Contains("forced PIN change") && ProfileCompatibility.Requirements(basicProfile).Contains("Basic FIDO2"), "Editor explains distinct profile capability requirements");

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
Check(ConfigReader.Parse(ProfileConfigWriter.Remove(multiple, "Standard")).Providers.All(p => p.Profile != "Standard"), "Removing a profile clears legacy defaults without removing tenants");
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
foreach (var malformed in new[] { "unrecognized output", "Display name  Model  ID\nDesk key  YubiKey  prefix…",
    "Display name  Model  ID\nDesk key  YubiKey  same\nBackup  Key  same", "Display name  Model  ID\nUnexpected row" })
{
    bool refused = false;
    try { RegisteredCredentials.Parse(malformed); } catch (InvalidDataException) { refused = true; }
    Check(refused, "Unknown, truncated, partial and duplicate registrations fail closed");
}
var regHeader = "Display name  Model  ID\n";
var firstRow = "Desk key  YubiKey 5 Series  -credential-1\n";
var otherRow = "Desk key  YubiKey 5 Series  credential-2\n";
var phoneRow = "Desk key  Microsoft Authenticator - Android  phone-3\n";
var managedUser = Guid.Parse(id);
var wanted = RegisteredCredentials.Parse(regHeader + firstRow).Single();
foreach (var model in new[] { "YubiKey 5 Series with NFC", "YubiKey 5Ci", "YubiKey Bio Series - FIDO Edition",
    "Security Key by Yubico with NFC", "Security Key NFC by Yubico - Enterprise Edition" })
    Check((wanted with { Name = "Phone", Model = model }).IsYubicoHardware, "A renamed Yubico hardware registration is still recognized: " + model);
foreach (var model in new[] { "Microsoft Authenticator - Android", "Windows Hello", "Google Password Manager",
    "Unknown", "Security Key", "Feitian BioPass FIDO2 Authenticator" })
{
    var unrelated = wanted with { Name = "YubiKey 5C NFC", Model = model };
    Check(!unrelated.IsYubicoHardware, "A display name cannot substitute for a recognized Yubico model: " + model);
    var refused = false;
    try { await new PasskeyRegistrationService(_ => throw new Exception("Unexpected CLI call")).RemoveAsync(managedUser, unrelated); }
    catch (InvalidOperationException) { refused = true; }
    Check(refused, "A hidden registration cannot be removed: " + model);
}
foreach (var scenario in new[] { "removed", "changed", "id-prefix", "still-present", "missing-phone", "ambiguous-result", "denied", "timeout", "invalid-user" })
{
    var commands = new List<string[]>();
    var service = new PasskeyRegistrationService(a =>
    {
        commands.Add(a);
        if (a[1] == "delete") return scenario == "timeout" ? throw new TimeoutException() :
            Task.FromResult(scenario == "denied" ? new CliResult(1, "", "ERROR: 403") : new CliResult(0, "", ""));
        var output = commands.Count == 1 ? regHeader + (scenario == "changed" ? "" : firstRow) + otherRow + phoneRow +
            (scenario == "id-prefix" ? "Prefix match  YubiKey 5 Series  -credential-10\n" : "") :
            scenario == "ambiguous-result" ? "failed to read" : regHeader + (scenario == "still-present" ? firstRow : "") + otherRow +
            (scenario == "missing-phone" ? "" : phoneRow);
        return Task.FromResult(new CliResult(0, output, ""));
    });
    bool removed = false;
    try { var remaining = await service.RemoveAsync(scenario == "invalid-user" ? Guid.Empty : managedUser, wanted);
        removed = true;
        Check(remaining.Select(r => r.Id).SequenceEqual(new[] { "credential-2", "phone-3" }), "The identically named YubiKey and phone registration both remain"); }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
    var deletes = commands.Where(a => a[1] == "delete").ToArray();
    Check(removed == (scenario == "removed") && deletes.Length == (scenario is "changed" or "id-prefix" or "invalid-user" ? 0 : 1) &&
        deletes.All(a => a.SequenceEqual(new[]{"credentials", "delete", "--force", "--", managedUser.ToString(), "-credential-1"})),
        "Registration removal binds one user and exact method, never retries, verifies outcome · " + scenario);
}
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
RegistrationJournalTests.Run(Check);
Workflow055Tests.Run(Check);
Workflow056Tests.Run(Check);
await NfcTests.Run(Check);
Console.WriteLine($"{tests} checks passed.");
return 0;

internal static class ConsoleProbe
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
