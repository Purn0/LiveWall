using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall
{
    // A borderless, click-through, layered child window living behind the desktop icons (created by a player host
    // process). The video engine renders straight into it; the window itself never paints.
    internal sealed class WallpaperWindow : IDisposable
    {
        const string ClassName = "LiveWall.Surface";
        static WndProc wndProc;           // kept alive for the lifetime of the class registration
        static readonly Dictionary<IntPtr, WallpaperWindow> windows = new Dictionary<IntPtr, WallpaperWindow>();

        public IntPtr Handle { get; private set; }
        public event EventHandler DestroyedExternally;
        bool disposing;

        static void EnsureClass()
        {
            if (wndProc != null) return;
            wndProc = WindowProc;
            var wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = wndProc;
            wc.hInstance = Native.GetModuleHandle(IntPtr.Zero);
            wc.hbrBackground = Native.GetStockObject(4 /*BLACK_BRUSH*/);
            wc.lpszClassName = ClassName;
            if (Native.RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        }

        // `bounds` is in the parent's client coordinates. The window starts fully transparent (see Reveal).
        public WallpaperWindow(IntPtr parent, RECT bounds, IntPtr insertAfter)
        {
            EnsureClass();
            // Created directly as a child so WS_EX_LAYERED applies to a child window (needs the Win8+ manifest);
            // Progman on 24H2+ has no redirection surface, and only layered children get their own.
            Handle = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE,
                ClassName, "LiveWall", Native.WS_CHILD | Native.WS_CLIPSIBLINGS | Native.WS_DISABLED,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, parent, IntPtr.Zero, Native.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            windows[Handle] = this;
            // Shown (so the video renderer never sees an occluded window and presents normally) but invisible
            // until the first frame is on it; then Reveal() makes it opaque in a single composition step.
            Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
            Native.SetWindowPos(Handle, insertAfter, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public void Reveal() { SetAlpha(255); }

        // 0 = invisible, 255 = opaque (DWM blends it over whatever is below: the previous wallpaper during a fade).
        public void SetAlpha(byte alpha)
        {
            if (Handle != IntPtr.Zero) Native.SetLayeredWindowAttributes(Handle, 0, alpha, Native.LWA_ALPHA);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            disposing = true;
            if (Native.IsWindow(Handle)) Native.DestroyWindow(Handle);
            windows.Remove(Handle);
            Handle = IntPtr.Zero;
        }

        static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case Native.WM_ERASEBKGND:
                        return new IntPtr(1);
                    case Native.WM_PAINT:
                    {
                        // Validate only: GDI painting here would overwrite the (possibly paused) video frame.
                        var ps = new byte[128];
                        Native.BeginPaint(hwnd, ps);
                        Native.EndPaint(hwnd, ps);
                        return IntPtr.Zero;
                    }
                    case Native.WM_NCHITTEST:
                        return new IntPtr(Native.HTTRANSPARENT);
                    case Native.WM_DESTROY:
                    {
                        WallpaperWindow w;
                        if (windows.TryGetValue(hwnd, out w))
                        {
                            windows.Remove(hwnd);
                            if (!w.disposing)
                            {
                                // Explorer restarted (our parent went away).
                                w.Handle = IntPtr.Zero;
                                var h = w.DestroyedExternally;
                                if (h != null) h(w, EventArgs.Empty);
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Surface WndProc", ex); }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}
