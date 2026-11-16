using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YubiEnroll.UI;

internal static class TaskbarIdentity
{
    internal static void Initialize() => SetCurrentProcessExplicitAppUserModelID("YubiEnroll.CommunityUI");
    internal static void Apply(Window window)
    {
        // The published executable owns the multi-resolution icon. Set both native window icons,
        // so the title bar, Alt+Tab and unpinned taskbar agree even with an explicit taskbar identity.
        var path = Environment.ProcessPath;
        if (path == null || ExtractIconEx(path, 0, out var large, out var small, 1) == 0) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (large != IntPtr.Zero) SendMessage(hwnd, 0x0080, new IntPtr(1), large);
        if (small != IntPtr.Zero) SendMessage(hwnd, 0x0080, IntPtr.Zero, small);
        window.Closed += (_, _) => { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); };
    }
    internal static bool HasWindowIcons(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return SendMessage(hwnd, 0x007F, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero &&
            SendMessage(hwnd, 0x007F, new IntPtr(1), IntPtr.Zero) != IntPtr.Zero;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
