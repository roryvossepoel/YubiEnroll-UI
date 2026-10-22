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
Check(command.Contains("--no-reset") && !command.Contains("--reset"), "Explicit no-reset overrides profile reset");
Check(command[Array.IndexOf(command, "--profile") + 1] == "Alternative profile", "Chosen profile retained");
Check(!command.Contains("-f") && !command.Contains("--force"), "Native confirmation prompts retained");
Check(EnrollmentCommand.Build(id, "Standard", "", true).Contains("--reset"), "Reset requires explicit flag");
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
Console.WriteLine($"{tests} checks passed.");
return 0;
