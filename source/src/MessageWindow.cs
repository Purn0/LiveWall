using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall
{
    // Hidden top-level window (not message-only, so it also receives broadcasts such as WM_DISPLAYCHANGE and
    // "TaskbarCreated"). Other LiveWall processes find it by class + title to forward commands.
    internal sealed class MessageWindow : IDisposable
    {
        public delegate IntPtr? Handler(uint msg, IntPtr wParam, IntPtr lParam);

        public const string ClassName = "LiveWall.Controller";
        static WndProc proc;
        static readonly Dictionary<IntPtr, Handler> handlers = new Dictionary<IntPtr, Handler>();

        public IntPtr Handle { get; private set; }

        public MessageWindow(string title, Handler handler)
        {
            if (proc == null)
            {
                proc = Dispatch;
                var wc = new WNDCLASSEX();
                wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = proc;
                wc.hInstance = Native.GetModuleHandle(IntPtr.Zero);
                wc.lpszClassName = ClassName;
                if (Native.RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            }
            Handle = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, ClassName, title, Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            handlers[Handle] = handler;
        }

        static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            Handler h;
            if (handlers.TryGetValue(hwnd, out h))
            {
                try
                {
                    IntPtr? r = h(msg, wParam, lParam);
                    if (r.HasValue) return r.Value;
                }
                catch (Exception ex) { Log.Error("Message 0x" + msg.ToString("X4"), ex); }
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            handlers.Remove(Handle);
            Native.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
