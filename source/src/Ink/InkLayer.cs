using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    // Shows a wallpaper's drawings behind the desktop icons, above the wallpaper (picture or video).
    //
    // One small per-pixel-alpha layered child window per screen, sized to just the drawn area, filled once with
    // UpdateLayeredWindow and never touched again: the compositor keeps the pixels, so it costs nothing while shown.
    // The windows live on their own idle thread: a child of Explorer's desktop window ties its thread's input queue to
    // Explorer's, and that must never be LiveWall's UI thread.
    internal sealed class InkLayer : IDisposable
    {
        public struct Screen
        {
            public RECT Bounds;          // screen coordinates (physical pixels)
            public RECT BoundsInParent;  // same area in the desktop window's client coordinates
        }

        const string ClassName = "LiveWall.Ink";
        const uint WM_RUN = Native.WM_APP + 60;

        readonly SynchronizationContext ui;
        readonly Thread thread;
        readonly Queue<Action> queue = new Queue<Action>();
        readonly ManualResetEvent started = new ManualResetEvent(false);
        readonly List<IntPtr> windows = new List<IntPtr>();     // owned by the ink thread
        WndProc proc;
        IntPtr messageWindow;
        int generation;                                          // UI thread: ignores results of superseded Show calls
        List<IntPtr> current = new List<IntPtr>();               // UI thread copy of the visible windows

        public InkLayer(SynchronizationContext uiContext)
        {
            ui = uiContext;
            thread = new Thread(Run) { IsBackground = true, Name = "LiveWall ink layer", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            started.WaitOne(3000);
        }

        // The ink windows currently on screen (UI thread). The last one is the anchor players are placed below.
        public IList<IntPtr> Windows { get { return current; } }

        public IntPtr Last
        {
            get
            {
                for (int i = current.Count - 1; i >= 0; i--) if (Native.IsWindow(current[i])) return current[i];
                return IntPtr.Zero;
            }
        }

        public bool AllAlive
        {
            get { foreach (IntPtr h in current) if (!Native.IsWindow(h)) return false; return true; }
        }

        // Replaces whatever is shown. `shown` runs on the UI thread with the new windows (topmost first).
        public void Show(IntPtr parent, IntPtr insertAfter, IList<Screen> screens, List<InkStroke> strokes, int canvasW, int canvasH,
                         Action shown)
        {
            int gen = ++generation;
            current = new List<IntPtr>();
            var screensCopy = new List<Screen>(screens);
            Post(() =>
            {
                DestroyAll();
                IntPtr after = insertAfter;
                var made = new List<IntPtr>();
                foreach (var s in screensCopy)
                {
                    IntPtr h = CreateLayer(parent, after, s, strokes, canvasW, canvasH);
                    if (h != IntPtr.Zero) { made.Add(h); after = h; }
                }
                ui.Post(_ =>
                {
                    if (gen != generation) return;
                    current = made;
                    if (shown != null) shown();
                }, null);
            });
        }

        public void Clear()
        {
            generation++;
            if (current.Count == 0) { Post(DestroyAll); return; }
            current = new List<IntPtr>();
            Post(DestroyAll);
        }

        // Puts the ink windows back directly below `insertAfter` (in order), e.g. after Explorer shuffled its children.
        public void Restack(IntPtr insertAfter)
        {
            IntPtr after = insertAfter;
            foreach (IntPtr h in current)
            {
                if (!Native.IsWindow(h)) continue;
                Native.SetWindowPos(h, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
                after = h;
            }
        }

        // ------------------------------------------------------------------ ink thread

        void Post(Action a)
        {
            lock (queue) queue.Enqueue(a);
            if (messageWindow != IntPtr.Zero) Native.PostMessage(messageWindow, WM_RUN, IntPtr.Zero, IntPtr.Zero);
        }

        void Run()
        {
            try
            {
                proc = WindowProc;
                var wc = new WNDCLASSEX();
                wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = proc;
                wc.hInstance = Native.GetModuleHandle(IntPtr.Zero);
                wc.lpszClassName = ClassName;
                Native.RegisterClassEx(ref wc);
                messageWindow = Native.CreateWindowEx(0, ClassName, "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Error("Ink layer thread", ex); }
            started.Set();
            if (messageWindow == IntPtr.Zero) return;
            Native.PostMessage(messageWindow, WM_RUN, IntPtr.Zero, IntPtr.Zero);   // anything queued before we were ready
            MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            DestroyAll();
            Native.DestroyWindow(messageWindow);
        }

        IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_RUN && hwnd == messageWindow)
                {
                    while (true)
                    {
                        Action a;
                        lock (queue) { if (queue.Count == 0) break; a = queue.Dequeue(); }
                        try { a(); } catch (Exception ex) { Log.Error("Ink layer", ex); }
                    }
                    return IntPtr.Zero;
                }
                switch (msg)
                {
                    case Native.WM_ERASEBKGND: return new IntPtr(1);
                    case Native.WM_NCHITTEST: return new IntPtr(Native.HTTRANSPARENT);
                    case Native.WM_DESTROY: windows.Remove(hwnd); break;
                }
            }
            catch (Exception ex) { Log.Error("Ink layer WndProc", ex); }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        IntPtr CreateLayer(IntPtr parent, IntPtr insertAfter, Screen s, List<InkStroke> strokes, int canvasW, int canvasH)
        {
            int w = s.Bounds.Width, h = s.Bounds.Height;
            if (w <= 0 || h <= 0 || !Native.IsWindow(parent)) return IntPtr.Zero;
            var m = InkMapping.Fill(canvasW, canvasH, w, h);

            // Only as big as the drawing: less for the compositor to blend while a video plays underneath.
            RectangleF area = RectangleF.Empty;
            foreach (var st in strokes)
            {
                RectangleF r = m.ToTarget(st.Bounds);
                area = area.IsEmpty ? r : RectangleF.Union(area, r);
            }
            Rectangle crop = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(area, 3, 3)), new Rectangle(0, 0, w, h));
            if (crop.Width <= 0 || crop.Height <= 0) return IntPtr.Zero;

            IntPtr hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | InkNative.WS_EX_NOPARENTNOTIFY,
                ClassName, "LiveWall ink", Native.WS_CHILD | Native.WS_CLIPSIBLINGS | Native.WS_DISABLED,
                s.BoundsInParent.Left + crop.Left, s.BoundsInParent.Top + crop.Top, crop.Width, crop.Height,
                parent, IntPtr.Zero, Native.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Log.Warn("Ink layer window: " + Marshal.GetLastWin32Error()); return IntPtr.Zero; }
            windows.Add(hwnd);

            using (var bmp = new LayeredBitmap(crop.Width, crop.Height))
            {
                using (var g = bmp.CreateGraphics())
                {
                    g.TranslateTransform(-crop.Left, -crop.Top);
                    InkRenderer.DrawStrokes(g, strokes, m);
                }
                if (!bmp.Present(hwnd, null, null))
                {
                    Log.Warn("Could not show drawings behind the desktop icons");
                    Native.DestroyWindow(hwnd);
                    return IntPtr.Zero;
                }
            }
            Native.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            return hwnd;
        }

        void DestroyAll()
        {
            foreach (IntPtr h in windows.ToArray()) if (Native.IsWindow(h)) Native.DestroyWindow(h);
            windows.Clear();
        }

        public void Dispose()
        {
            generation++;
            current = new List<IntPtr>();
            if (messageWindow != IntPtr.Zero)
            {
                Post(DestroyAll);
                Native.PostMessage(messageWindow, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(1500);
            }
        }
    }
}
