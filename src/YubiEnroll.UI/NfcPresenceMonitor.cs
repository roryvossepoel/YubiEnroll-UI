using System.Runtime.InteropServices;

namespace YubiEnroll.UI;

// Poll only PC/SC presence, without opening a card or competing with CLI operations.
internal sealed class NfcPresenceMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    internal NfcPresenceMonitor(Action changed)
    {
        _worker = Task.Run(async () =>
        {
            string? previous = null;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var snapshot = Snapshot();
                    if (previous != null && previous != snapshot) changed();
                    previous = snapshot;
                    await Task.Delay(500, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }
    internal static string Snapshot()
    {
        if (SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out var context) != 0) return "service unavailable";
        try
        {
            uint length = 0;
            var result = SCardListReaders(context, null, null, ref length);
            if (result != 0 || length < 2) return "no readers";
            var buffer = new char[length];
            if (SCardListReaders(context, null, buffer, ref length) != 0) return "reader list unavailable";
            var states = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(n => !n.Contains("yubico yubikey", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal).Select(n => new ReaderState { Reader = n, UserData = IntPtr.Zero, CurrentState = 0, EventState = 0, AtrLength = 0, Atr = new byte[36] }).ToArray();
            if (states.Length == 0) return "no readers";
            if (SCardGetStatusChange(context, 0, states, (uint)states.Length) != 0) return "reader status unavailable";
            // Keep card event counter/presence/ATR, ignore sharing and powered-state changes caused by our own probes.
            return string.Join("\n", states.Select(s => $"{s.Reader}:{s.EventState & 0xffff007c:x}:{Convert.ToHexString(s.Atr.AsSpan(0, (int)Math.Min(s.AtrLength, 36u)))}"));
        }
        finally { SCardReleaseContext(context); }
    }
    public void Dispose() { _stop.Cancel(); _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReaderState
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Reader;
        public IntPtr UserData;
        public uint CurrentState;
        public uint EventState;
        public uint AtrLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] Atr;
    }
    [DllImport("winscard.dll")] private static extern int SCardEstablishContext(uint scope, IntPtr reserved1, IntPtr reserved2, out IntPtr context);
    [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr context);
    [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardListReadersW")]
    private static extern int SCardListReaders(IntPtr context, string? groups, [Out] char[]? readers, ref uint length);
    [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardGetStatusChangeW")]
    private static extern int SCardGetStatusChange(IntPtr context, uint timeout, [In, Out] ReaderState[] states, uint count);
}
