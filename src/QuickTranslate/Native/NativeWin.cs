using System.Runtime.InteropServices;

namespace QuickTranslate.Native;

/// <summary>user32 直接 P/Invoke（始终可用，不依赖原生库）。</summary>
public static class NativeWin
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const int KEYEVENTF_KEYUP = 0x0002;

    public static bool GetCursor(out int x, out int y)
    {
        if (!GetCursorPos(out var p)) { x = 0; y = 0; return false; }
        x = p.X; y = p.Y;
        return true;
    }

    /// <summary>模拟 Ctrl+C（托管实现，作为原生库的兜底）。</summary>
    public static void SendCopy()
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);               // Ctrl down
        keybd_event(0x43, 0, 0, UIntPtr.Zero);               // C down
        keybd_event(0x43, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // C up
        keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl up
    }

    /// <summary>物理像素 → WPF DIP 的缩放系数（96 / 系统 DPI）。</summary>
    public static double GetDpiScaleSafe()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi > 0 ? 96.0 / dpi : 1.0;
        }
        catch { return 1.0; }
    }
}
