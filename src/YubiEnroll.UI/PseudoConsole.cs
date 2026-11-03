using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using YubiEnroll.Core;

namespace YubiEnroll.UI;

// Windows-only ConPTY transport. No shell, console transcript, command retry or operation timeout.
internal sealed class PseudoConsole : IDisposable
{
    private IntPtr _console;
    private FileStream? _input;
    private FileStream? _output;
    private IntPtr _process;
    private readonly object _writeLock = new();
    public event Action<string>? Output;
    public bool HasStarted { get; private set; }

    public async Task<int> RunAsync(string executable, string[] arguments)
    {
        Start(executable, arguments);
        var reading = Task.Run(async () =>
        {
            using var reader = new StreamReader(_output!, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var buffer = new char[4096];
            try { int read; while ((read = await reader.ReadAsync(buffer)) > 0) Output?.Invoke(new string(buffer, 0, read)); }
            catch (IOException) { /* Closing the pseudoconsole breaks its output pipe. */ }
        });
        await Task.Run(() => WaitForSingleObject(_process, 0xffffffff));
        if (!GetExitCodeProcess(_process, out var code)) throw new Win32Exception();
        var exit = unchecked((int)code);
        // ClosePseudoConsole can block while output drains; do it away from the UI thread.
        var handle = Interlocked.Exchange(ref _console, IntPtr.Zero);
        if (handle != IntPtr.Zero) await Task.Run(() => ClosePseudoConsole(handle));
        await reading;
        return exit;
    }

    public void Write(string text)
    {
        lock (_writeLock)
        {
            if (_input == null) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            try { _input.Write(bytes); _input.Flush(); }
            catch (IOException) { }
            finally { Array.Clear(bytes); }
        }
    }

    private void Start(string executable, string[] args)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            var hr = CreatePseudoConsole(new Coord(100, 28), inputRead, outputWrite, 0, out _console);
            Marshal.ThrowExceptionForHR(hr);
            inputRead.Dispose(); outputWrite.Dispose();
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, _console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(args).Select(WindowsCommandLine.Quote)));
            _input = new FileStream(inputWrite, FileAccess.Write, 4096, false); inputWrite = null;
            _output = new FileStream(outputRead, FileAccess.Read, 4096, false); outputRead = null;
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400,
                IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info)) throw new Win32Exception();
            HasStarted = true;
            _process = info.Process;
            CloseHandle(info.Thread);
        }
        finally
        {
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
        }
    }

    public void Dispose()
    {
        _input?.Dispose(); _input = null;
        // Call only after completion or before process launch. Never kill a live operation.
        var handle = Interlocked.Exchange(ref _console, IntPtr.Zero);
        if (handle != IntPtr.Zero) ClosePseudoConsole(handle);
        _output?.Dispose();
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public IntPtr ReservedPtr, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
