using System.Globalization;
using System.Text.RegularExpressions;

namespace YubiEnroll.Core;

public sealed record ResetKey(string Serial, string Model, string? Reader = null)
{
    public bool IsNfc => Reader != null;
    public string ConnectionLabel => IsNfc ? "NFC via " + NfcReaderStatus.DisplayName(Reader!) : "USB";
    public string Label => $"{Model} · Serial {Serial}" + (IsNfc ? " · " + ConnectionLabel : "");
    public string RegistrationName => $"{Regex.Replace(Model, @"\s*\(\d+\.\d+\.\d+\)\s*$", "").Trim()} ({Serial})";
}

public enum FidoPinState { Unknown, NotSet, Set, Blocked, Disabled, Unsupported }

public sealed record KeyStatus(ResetKey Key, FidoPinState Pin, FidoCapabilities? Capabilities = null)
{
    public bool ListingUnsupported => Capabilities?.CanListPasskeys == false ||
        (Capabilities?.CanListPasskeys == null && Version.TryParse(Regex.Match(Key.Model, @"\((\d+\.\d+\.\d+)\)$").Groups[1].Value, out var firmware) && firmware < new Version(5, 2, 1));
    public string Label => Key.Label;
    public string PinLabel => "FIDO PIN: " + (Pin switch
    {
        FidoPinState.NotSet => "Not set",
        FidoPinState.Set => "Set",
        FidoPinState.Blocked => "Blocked — FIDO reset required",
        FidoPinState.Disabled => "Disabled",
        FidoPinState.Unsupported => "Not supported",
        _ => "Unknown — could not verify"
    });
}

public sealed record KeyInventory(IReadOnlyList<ResetKey> Keys, int Unavailable, int NfcUnavailable = 0, int NfcReaders = 0, IReadOnlyList<NfcReaderStatus>? ReaderStates = null);

// USB serial or explicit NFC reader plus verified serial. Never targets the first device.
public sealed class KeyResetService(
    Func<string[], Task<CliResult>> capture,
    Func<string[], Task<int>> interactive,
    Func<Task<KeyInventory>>? discoverNfc = null)
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

    public async Task<KeyInventory> DiscoverAsync()
    {
        var usb = ParseKeys(await ReadAsync(["list"]));
        if (discoverNfc == null) return usb;
        var nfc = await discoverNfc();
        return new(usb.Keys.Concat(nfc.Keys).ToArray(), usb.Unavailable, nfc.NfcUnavailable, nfc.NfcReaders, nfc.ReaderStates);
    }

    public static string[] Selector(ResetKey key)
    {
        ValidateSerial(key.Serial);
        if (!key.IsNfc) return ["--device", key.Serial];
        ValidateReader(key.Reader!);
        return ["--reader", key.Reader!];
    }

    public static void ValidateReader(string reader)
    {
        if (string.IsNullOrWhiteSpace(reader) || reader.Length > 512 || reader.Any(char.IsControl))
            throw new ArgumentException("A valid, unambiguous NFC reader name is required.");
    }

    public static FidoPinState ParsePinState(string output)
    {
        var fields = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("PIN:", StringComparison.Ordinal)).ToArray();
        if (fields.Length != 1) return FidoPinState.Unknown;
        var value = fields[0][4..].Trim();
        return value switch
        {
            "Not set" => FidoPinState.NotSet,
            "Set" => FidoPinState.Set,
            "Blocked" => FidoPinState.Blocked,
            "Disabled" => FidoPinState.Disabled,
            "Not supported" => FidoPinState.Unsupported,
            _ when Regex.IsMatch(value, @"\A[1-9][0-9]* attempt\(s\) remaining\z") => FidoPinState.Set,
            _ => FidoPinState.Unknown
        };
    }

    public async Task<FidoPinState> ReadPinStateAsync(ResetKey key)
    {
        ValidateSerial(key.Serial);
        if (key.IsNfc) await RequirePresentAsync(key);
        return ParsePinState(await ReadAsync([.. Selector(key), "fido", "info"]));
    }

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
        var arguments = selected.IsNfc ? new[] { "--reader", selected.Reader!, "fido", "reset" } : ResetArguments(selected.Serial);
        _ = Selector(selected);
        await CheckVersionAsync();
        await RequirePresentAsync(selected);
        // Check FIDO access before asking for destructive confirmation. No key changes here.
        await ReadAsync([.. Selector(selected), "fido", "info"]);
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
