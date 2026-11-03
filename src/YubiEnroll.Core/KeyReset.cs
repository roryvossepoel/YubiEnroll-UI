using System.Globalization;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record ResetKey(string Serial, string Model)
{
    public string Label => $"{Model} · Serial {Serial}";
}

public sealed record KeyInventory(IReadOnlyList<ResetKey> Keys, int Unavailable);

// Deliberately limited to USB FIDO keys with a readable serial number. Never targets the first device.
public sealed class KeyResetService(
    Func<string[], Task<CliResult>> capture,
    Func<string[], Task<int>> interactive)
{
    public static string FindExecutable()
    {
        var candidates = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .SelectMany(folder => new[] { "YubiKey Manager CLI", "YubiKey Manager" }
                .Select(name => Path.Combine(Environment.GetFolderPath(folder), "Yubico", name, "ykman.exe")));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    public static Version ParseVersion(string output)
    {
        var match = Regex.Match(output.Trim(), @"\AYubiKey Manager \(ykman\) version: (?<version>[0-9]+\.[0-9]+\.[0-9]+)\z");
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version) ||
            version < new Version(5, 9, 2) || version.Major != 5)
            throw new InvalidOperationException("Install YubiKey Manager CLI 5.9.2 or a newer 5.x release in its standard location, then Refresh.");
        return version;
    }

    public async Task<Version> CheckVersionAsync() => ParseVersion(await ReadAsync(["--version"]));

    public static KeyInventory ParseKeys(string output)
    {
        var keys = new List<ResetKey>();
        var unavailable = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"\A(?<model>(?:YubiKey|Security Key)[^\r\n]*) \[(?<interfaces>[A-Z+]+)\] Serial: (?<serial>[1-9][0-9]{0,9})\z");
            if (!match.Success || !match.Groups["interfaces"].Value.Split('+').Contains("FIDO"))
            {
                unavailable++;
                continue;
            }
            var serial = match.Groups["serial"].Value;
            ValidateSerial(serial);
            if (keys.Any(key => key.Serial == serial))
                throw new InvalidOperationException("The device list contains duplicate serial numbers. Disconnect the keys and Refresh.");
            keys.Add(new(serial, match.Groups["model"].Value));
        }
        return new(keys, unavailable);
    }

    public async Task<KeyInventory> DiscoverAsync() => ParseKeys(await ReadAsync(["list"]));

    private static void ValidateSerial(string serial)
    {
        if (!Regex.IsMatch(serial, @"\A[1-9][0-9]{0,9}\z") ||
            !uint.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("A readable YubiKey serial number is required for reset.");
    }

    public static string[] ResetArguments(string serial)
    {
        ValidateSerial(serial);
        // Keep native confirmation, same-device reinsertion checks and physical touch. Never --force.
        return ["--device", serial, "fido", "reset"];
    }

    public async Task<int?> ResetAsync(ResetKey selected, Func<ResetKey, bool> confirm)
    {
        var arguments = ResetArguments(selected.Serial);
        await CheckVersionAsync();
        await RequirePresentAsync(selected);
        // Check FIDO access before asking for destructive confirmation. No key changes here.
        await ReadAsync(["--device", selected.Serial, "fido", "info"]);
        if (!confirm(selected)) return null;
        await RequirePresentAsync(selected);
        // No timeout, automatic retry, broad factory reset or cloud-credential deletion.
        return await interactive(arguments);
    }

    private async Task RequirePresentAsync(ResetKey selected)
    {
        if (!(await DiscoverAsync()).Keys.Contains(selected))
            throw new InvalidOperationException("The selected key is no longer available or its details changed. Refresh and select the key again. No reset was started.");
    }

    private async Task<string> ReadAsync(string[] arguments)
    {
        var result = await capture(arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"YubiKey Manager could not read the key or version (exit {result.ExitCode}). Check administrator rights, close other key tools and Refresh.\n{result.Error.Trim()}");
        return result.Output;
    }
}
