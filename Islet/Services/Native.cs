using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Islet.Services;

public static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const byte VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    /// <summary>Cursor position in physical screen pixels (we are per-monitor DPI aware).</summary>
    public static POINT Cursor() { GetCursorPos(out var p); return p; }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    // ───── Glass: DWM blur-behind, clipped to the island's rounded shape ─────
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttrData data);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr hwnd, out RECT r);
    public static string RegionInfo(IntPtr hwnd)
    {
        int t = GetWindowRgnBox(hwnd, out var r);
        GetWindowRect(hwnd, out var w);
        return $"rgnType={t} box=({r.Left},{r.Top})-({r.Right},{r.Bottom}) window=({w.Left},{w.Top})-({w.Right},{w.Bottom})";
    }

    public static void SetBlur(IntPtr hwnd, bool on)
    {
        var accent = new AccentPolicy { AccentState = on ? 3 : 0 }; // 3 = ACCENT_ENABLE_BLURBEHIND
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new WinCompAttrData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = p, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Clips the window (and so the blur) to a rounded rectangle, in physical pixels. Pass w=0 to clear.</summary>
    public static void SetRoundRegion(IntPtr hwnd, int x, int y, int w, int h, int radius)
    {
        if (w <= 0) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
        var rgn = CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, radius * 2, radius * 2);
        SetWindowRgn(hwnd, rgn, true); // the system owns rgn after this call
    }

    public static void MakeToolWindow(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static void EnsureTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE

    public static void PressKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    public static bool IsForegroundFullscreen(IntPtr own)
    {
        // 2 = busy (fullscreen app), 3 = D3D fullscreen game, 4 = presentation mode
        if (SHQueryUserNotificationState(out int st) == 0 && st is 2 or 3 or 4) return true;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == own) return false;
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (IsZoomed(fg) || !GetWindowRect(fg, out var r)) return false;

        var mon = MonitorFromWindow(fg, 2);
        if (mon != MonitorFromWindow(own, 2)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}
