using System.Diagnostics;

// A real console preserves Yubico's native PIN/device prompts. No transcript or PIN storage.
if (args.Length == 1 && args[0] == "--self-test")
{
    if (!ConsoleMessages.IsEnrollment(["yubienroll.exe", "credentials", "add", "test-user"]) ||
        ConsoleMessages.IsEnrollment(["ykman.exe", "--device", "12345678", "fido", "reset"]) ||
        ConsoleMessages.IsEnrollment(["yubienroll.exe", "login"])) return 1;
    Console.WriteLine("Console host started; generated-PIN reminder is limited to enrollment.");
    return 0;
}
string? resultFile = null;
if (args.Length >= 4 && args[0] == "--result-file") { resultFile = args[1]; args = args[2..]; }
if (args.Length < 2 || !File.Exists(args[0])) return 125;
var code = 125;
Console.Title = "YubiEnroll UI - interactive operation";
Console.WriteLine("Complete the Yubico tool steps below. Keep this window open until the command finishes.");
try
{
    var info = new ProcessStartInfo(Path.GetFullPath(args[0]))
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!
    };
    foreach (var arg in args.Skip(1)) info.ArgumentList.Add(arg);
    using var process = Process.Start(info) ?? throw new IOException("Could not start the Yubico tool.");
    await process.WaitForExitAsync();
    code = process.ExitCode;
    if (resultFile != null)
        try { File.WriteAllText(resultFile, code.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Could not preserve the result for the UI. Press Enter when finished instead of closing this window."); }
    Console.WriteLine($"\nThe Yubico tool finished with exit code {code}.");
}
catch
{
    Console.Error.WriteLine("The Yubico tool could not be started or monitored. Check installation and permissions.");
}
if (code == 0 && ConsoleMessages.IsEnrollment(args))
    Console.WriteLine("Record any generated PIN securely before closing. It is not saved by this application.");
if (code == 0 && args.Length == 2 && args[1] == "login") return code;
Console.WriteLine("Press Enter to return to the UI.");
Console.ReadLine();
return code;

internal static class ConsoleMessages
{
    internal static bool IsEnrollment(string[] arguments) => arguments.Length >= 3 &&
        string.Equals(Path.GetFileName(arguments[0]), "yubienroll.exe", StringComparison.OrdinalIgnoreCase) &&
        arguments[1] == "credentials" && arguments[2] == "add";
}
