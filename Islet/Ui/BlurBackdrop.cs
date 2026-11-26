using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Islet.Services;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Islet.Ui;

/// <summary>
/// A tiny content-less window that sits directly behind the island and shows a live blur of whatever is
/// underneath, clipped to the island's exact rounded shape.
///
/// Uses Windows.UI.Composition's host-backdrop brush (the same blur Windows uses for acrylic) with a rounded
/// geometric clip. If that's unavailable, falls back to the DWM blur-behind accent (rectangular).
/// The island window is owned by this one, which keeps the island above it in z-order.
/// </summary>
public sealed class BlurBackdrop
{
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    readonly WndProc _proc; // keep the delegate alive
    bool _shown;
    (int x, int y, int w, int h, int r) _last;

    // Composition path
    readonly Compositor? _comp;
    readonly DesktopWindowTarget? _target;
    readonly SpriteVisual? _visual;
    readonly CompositionRoundedRectangleGeometry? _shape;
    static IntPtr _dispatcherQueueController;

    public IntPtr Handle { get; }
    public bool UsingComposition => _visual != null;

    public BlurBackdrop()
    {
        _proc = (h, m, w, l) => m == 0x0084 /* WM_NCHITTEST */ ? new IntPtr(-1) /* HTTRANSPARENT */ : DefWindowProc(h, m, w, l);
        var hinst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hinst,
            hbrBackground = GetStockObject(4), // BLACK_BRUSH (only used by the fallback path)
            lpszClassName = "IsletBlurBackdrop",
        };
        RegisterClassEx(ref wc);

        try
        {
            // Composition path: no GDI surface at all, the visual tree is the whole window
            Handle = CreateWindowEx(EX_BASE | 0x00200000 /* WS_EX_NOREDIRECTIONBITMAP */, "IsletBlurBackdrop", "",
                                    0x80000000 /* WS_POPUP */, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
            int on = 1;
            DwmSetWindowAttribute(Handle, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, sizeof(int));

            EnsureDispatcherQueue();
            _comp = new Compositor();
            _target = CreateTarget(_comp, Handle);
            _shape = _comp.CreateRoundedRectangleGeometry();
            _visual = _comp.CreateSpriteVisual();
            _visual.Brush = _comp.CreateHostBackdropBrush();
            _visual.Clip = _comp.CreateGeometricClip(_shape);
            _target.Root = _visual;
        }
        catch
        {
            // Fallback: classic DWM blur-behind (can't be rounded, but still glassy)
            if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            _comp = null; _target = null; _visual = null; _shape = null;
            Handle = CreateWindowEx(EX_BASE, "IsletBlurBackdrop", "", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
            Native.SetBlur(Handle, true);
        }
    }

    const uint EX_BASE = 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */;

    /// <summary>Moves the blur to a rounded rect in physical screen pixels.</summary>
    public void Place(int x, int y, int w, int h, int r)
    {
        if (w < 2 || h < 2) { Hide(); return; }
        var now = (x, y, w, h, r);
        if (now == _last && _shown) return;
        bool sizeChanged = now.w != _last.w || now.h != _last.h || now.r != _last.r;
        _last = now;
        // SWP_NOZORDER | SWP_NOACTIVATE (+ SWP_SHOWWINDOW the first time)
        SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010 | (_shown ? 0u : 0x0040));
        if (sizeChanged)
        {
            if (_visual != null && _shape != null)
            {
                var size = new Vector2(w, h);
                _visual.Size = size;
                _shape.Size = size;
                _shape.CornerRadius = new Vector2(r, r);
            }
            else SetWindowRgn(Handle, CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2), true);
        }
        _shown = true;
    }

    public void Hide()
    {
        if (!_shown) return;
        ShowWindow(Handle, 0);
        _shown = false;
    }

    // ───────────── Composition interop ─────────────

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || _dispatcherQueueController != IntPtr.Zero) return;
        var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(o, out _dispatcherQueueController));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateDesktopWindowTargetFn(IntPtr self, IntPtr hwnd, int isTopmost, out IntPtr target);

    static DesktopWindowTarget CreateTarget(Compositor comp, IntPtr hwnd)
    {
        // ICompositorDesktopInterop::CreateDesktopWindowTarget (vtable slot 3)
        IntPtr unk = ((WinRT.IWinRTObject)comp).NativeObject.ThisPtr;
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out IntPtr interop));
        try
        {
            IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTargetFn>(fn);
            Marshal.ThrowExceptionForHR(create(interop, hwnd, 1, out IntPtr target));
            return DesktopWindowTarget.FromAbi(target);
        }
        finally { Marshal.Release(interop); }
    }

    // ───────────── Win32 ─────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}
