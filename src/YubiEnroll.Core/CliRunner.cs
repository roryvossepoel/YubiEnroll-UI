using System.Diagnostics;

namespace YubiEnroll.Core;

public sealed record CliResult(int ExitCode, string Output, string Error);

public sealed class CliRunner(string executable)
{
    public string Executable { get; } = Path.GetFullPath(executable);

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    // Only used for version, activation and read-only queries. Interactive enrollment/reset is never timed out or auto-retried.
    public async Task<CliResult> CaptureAsync(params string[] arguments)
    {
        var info = StartInfo(Executable, arguments);
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        using var process = Process.Start(info) ?? throw new IOException("Cannot start the Yubico command-line tool.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            throw new TimeoutException("The Yubico command-line tool did not respond within two minutes.");
        }
        return new(process.ExitCode, await stdout, await stderr);
    }

    public async Task<int> InteractiveAsync(params string[] arguments)
    {
        var host = Path.Combine(AppContext.BaseDirectory, "YubiEnroll.ConsoleHost.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("Console host is missing. Extract the complete download.");
        var directory = Path.Combine(Path.GetTempPath(), "YubiEnroll-operation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var resultFile = Path.Combine(directory, "exit-code");
        try
        {
            var info = StartInfo(host, new[] { "--result-file", resultFile, Executable }.Concat(arguments));
            using var process = Process.Start(info) ?? throw new IOException("Cannot open the interactive console.");
            await process.WaitForExitAsync();
            // Preserve the native result even when the host is closed at its final Enter prompt.
            if (File.Exists(resultFile) && int.TryParse(await File.ReadAllTextAsync(resultFile), out var code)) return code;
            return process.ExitCode == 0 ? 125 : process.ExitCode; // No confirmed native outcome.
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    }
}

public static class EnrollmentCommand
{
    public static string[] Build(string userId, string profile, string displayName, bool? reset)
    {
        if (!Guid.TryParse(userId, out var id) || id == Guid.Empty)
            throw new ArgumentException("Select a search result with a valid Entra object ID.");
        if (string.IsNullOrWhiteSpace(profile)) throw new ArgumentException("Choose an enrollment profile.");
        var args = new List<string> { "credentials", "add", id.ToString(), "--profile", profile };
        if (reset.HasValue) args.Add(reset.Value ? "--reset" : "--no-reset");
        if (!string.IsNullOrWhiteSpace(displayName)) args.AddRange(["--display-name", displayName.Trim()]);
        // Retain native confirmation prompts; do not use --force.
        return args.ToArray();
    }
}
