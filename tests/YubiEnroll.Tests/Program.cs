using System.Text.Json;
using YubiEnroll.Core;

if (args.FirstOrDefault() == "--version")
{
    Console.WriteLine("Mock YubiEnroll 0.0.0 (CI only)");
    return 0;
}
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
Reject(() => ProfileConfigWriter.Build(multiple, policy with { Name = "Renamed" }, "Standard"), "Shared profile cannot be silently renamed");
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
Console.WriteLine($"{tests} checks passed.");
return 0;
