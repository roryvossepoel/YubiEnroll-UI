using System.IO;
using System.Text.RegularExpressions;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

internal sealed record RequirementCheck(bool Ready, string Detail);
internal sealed record RequirementReport(bool Administrator, RequirementCheck Enrollment, RequirementCheck Manager, string EnrollmentPath)
{
    public bool Ready => Administrator && Enrollment.Ready && Manager.Ready;
}

internal static class Requirements
{
    internal static string FindEnrollment()
    {
        var candidates = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Yubico", "YubiEnroll", "yubienroll.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    // Only --version is used here. No authentication, device discovery or configuration writes.
    internal static async Task<RequirementReport> ProbeAsync(bool administrator, string enrollment, string manager,
        Func<string, Task<CliResult>>? version = null)
    {
        version ??= path => new CliRunner(path).CaptureWithTimeoutAsync(TimeSpan.FromSeconds(10), "--version");
        async Task<RequirementCheck> Check(string path, bool isManager)
        {
            if (path.Length == 0 || !File.Exists(path)) return new(false, "Not installed");
            try
            {
                var result = await version(path);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                    return new(false, "Installed, but the version check failed");
                if (isManager)
                {
                    try { return new(true, "Installed · version " + KeyResetService.ParseVersion(result.Output)); }
                    catch (InvalidOperationException) { return new(false, "Unsupported version · install 5.9.2 or newer within 5.x"); }
                }
                var match = Regex.Match(result.Output.Trim(), @"\AYubiEnroll,?\s+(?:version:?\s*)?(?<version>\d+\.\d+\.\d+)(?:[-+][A-Za-z0-9.]+)?\z", RegexOptions.IgnoreCase);
                return match.Success ? new(true, "Installed · version " + match.Groups["version"].Value) :
                    new(false, "Installed, but the YubiEnroll version was not recognized");
            }
            catch (TimeoutException) { return new(false, "Installed, but the version check timed out"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { return new(false, "Installed, but the tool could not be started"); }
        }
        var checks = await Task.WhenAll(Check(enrollment, false), Check(manager, true));
        return new(administrator, checks[0], checks[1], enrollment);
    }
}
