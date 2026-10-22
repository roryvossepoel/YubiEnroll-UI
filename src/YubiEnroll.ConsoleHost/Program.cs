using System.Diagnostics;

// A real console preserves YubiEnroll's native PIN/device prompts. No transcript or PIN storage.
if (args.Length < 2 || !File.Exists(args[0])) return 125;
var code = 125;
Console.Title = "YubiEnroll UI - interactive operation";
Console.WriteLine("Complete the YubiEnroll steps below. Keep this window open until the command finishes.");
try
{
    var info = new ProcessStartInfo(Path.GetFullPath(args[0]))
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!
    };
    foreach (var arg in args.Skip(1)) info.ArgumentList.Add(arg);
    using var process = Process.Start(info) ?? throw new IOException("Could not start YubiEnroll.");
    await process.WaitForExitAsync();
    code = process.ExitCode;
    Console.WriteLine($"\nYubiEnroll finished with exit code {code}.");
}
catch
{
    Console.Error.WriteLine("YubiEnroll could not be started or monitored. Check installation and permissions.");
}
Console.WriteLine("Record any generated PIN securely before closing. It is not saved by this application.");
Console.WriteLine("Press Enter to return to the UI.");
Console.ReadLine();
return code;
