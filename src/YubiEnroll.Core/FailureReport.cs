using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YubiEnroll.Core;

public sealed record FailureReport(string Summary, string Diagnostic)
{
    public static FailureReport Create(Exception exception, string operation, string configPath, string executablePath)
    {
        var summary = exception switch
        {
            DirectoryNotFoundException => "A required folder does not exist. Check the paths and Windows account in Diagnostics.",
            FileNotFoundException => "A required file was not found. Check the paths in Diagnostics.",
            UnauthorizedAccessException => "Access denied. Check file permissions and the Windows account in Diagnostics.",
            Win32Exception { NativeErrorCode: 740 } => "Windows requires elevation. Try Restart as administrator using the same account.",
            Win32Exception { NativeErrorCode: 193 } => "Windows cannot run this executable. Check its architecture and the installation.",
            Win32Exception { NativeErrorCode: 5 } => "Windows denied access to the executable. Check permissions and application controls.",
            Win32Exception { NativeErrorCode: 1223 } => "The Windows elevation request was cancelled.",
            Win32Exception win32 => $"Windows could not start the executable (native error {win32.NativeErrorCode}). See Diagnostics.",
            InvalidOperationException or InvalidDataException or TimeoutException or ArgumentException => exception.Message,
            IOException => "A file could not be read or opened. See the error code and paths in Diagnostics.",
            _ => $"Unexpected {exception.GetType().Name}. See Diagnostics for the failing step and call stack."
        };
        var nativeCode = exception is Win32Exception native ? native.NativeErrorCode.ToString() : "n/a";
        // Do not serialize arbitrary Exception.Message/InnerException or any TOML/CLI output.
        // Parser errors are wrapped with a fixed message in ConfigReader.
        var diagnostic = $"Time: {DateTimeOffset.Now:O}\n" +
            $"Step: {operation}\nWindows account: {Environment.UserDomainName}\\{Environment.UserName}\n" +
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}\nOS architecture: {RuntimeInformation.OSArchitecture}\n" +
            $".NET runtime: {Environment.Version}\nApplication directory: {AppContext.BaseDirectory}\n" +
            $"Configuration path: {configPath}\nYubiEnroll executable: {executablePath}\n" +
            $"Exception: {exception.GetType().FullName}\nHRESULT: 0x{exception.HResult:X8}\nNative error: {nativeCode}\n" +
            $"Action: {summary}\nCall stack:\n{exception.StackTrace ?? "(not available)"}";
        return new($"{operation}: {summary}", diagnostic);
    }
}
