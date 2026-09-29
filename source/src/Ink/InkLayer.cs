using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    // Shows a wallpaper's drawings behind the desktop icons, above the wallpaper (picture or video).
    //
    // Per screen, small color-keyed layered child windows sized to just the drawn area, top to bottom: pens, shapes, text
    // and pictures (opaque); highlighters (constant alpha); fills (opaque; under the highlighters, as in the editor).
    // Painted once from a cached bitmap; the compositor keeps the pixels, so they cost nothing while shown.
    // (Per-pixel-alpha children, i.e. UpdateLayeredWindow, are never drawn under Progman on Windows 11 24H2+; color key
    // and constant alpha are.) A color key has no partial transparency, so opaque edges are pre-blended with what shows
    // beneath them (the wallpaper's picture or a video's first frame, and the windows below) to stay smooth.
    // The windows live on their own idle thread: a child of Explorer's desktop window ties its thread's input queue to
    // Explorer's, and that must never be LiveWall's UI thread.
    internal sealed class InkLayer : IDisposable
    {
        public struct Screen
        {
            public RECT Bounds;          // screen coordinates (physical pixels)
            public RECT BoundsInParent;  // same area in the desktop window's client coordinates
        }

        // Where a wallpaper's drawings go: its picture (or a video's first frame) as the wallpaper shows it.
        public struct Backdrop
        {
            public string Path, Fallback;   // Fallback: used when GDI+ can't read Path (e.g. Windows' copy of a WebP)
            public FitMode Fit;
        }

        sealed class Layer
        {
            public IntPtr Dc, Bitmap, Old;
            public int Width, Height;
        }

        const string ClassName = "LiveWall.Ink";
        const uint WM_RUN = Native.WM_APP + 60;
        const int KeyArgb = unchecked((int)0xFFFF00FE);   // transparent color (no palette mix has zero green)
        const uint KeyColorRef = 0x00FE00FF;

        readonly SynchronizationContext ui;
        readonly Thread thread;
        readonly Queue<Action> queue = new Queue<Action>();
        readonly ManualResetEvent started = new ManualResetEvent(false);
        readonly Dictionary<IntPtr, Layer> windows = new Dictionary<IntPtr, Layer>();   // owned by the ink thread
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
                         Backdrop backdrop, Action shown)
        {
            int gen = ++generation;
            current = new List<IntPtr>();
            var screensCopy = new List<Screen>(screens);
            Post(() =>
            {
                DestroyAll();
                IntPtr after = insertAfter;
                var made = new List<IntPtr>();
                Bitmap picture = InkRenderer.LoadImage(backdrop.Path) ?? InkRenderer.LoadImage(backdrop.Fallback);
                try
                {
                    foreach (var s in screensCopy)
                        foreach (IntPtr h in CreateLayers(parent, after, s, strokes, canvasW, canvasH, picture, backdrop.Fit))
                        {
                            made.Add(h);
                            after = h;
                        }
                }
                finally
                {
                    if (picture != null) picture.Dispose();
                    InkText.ClearCache();
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
                    case Native.WM_PAINT:
                    {
                        // Only when shown or when Explorer invalidates the desktop: the compositor keeps the pixels.
                        var ps = new byte[128];
                        IntPtr dc = Native.BeginPaint(hwnd, ps);
                        Layer l;
                        if (dc != IntPtr.Zero && windows.TryGetValue(hwnd, out l)) InkNative.BitBlt(dc, 0, 0, l.Width, l.Height, l.Dc, 0, 0, InkNative.SRCCOPY);
                        Native.EndPaint(hwnd, ps);
                        return IntPtr.Zero;
                    }
                    case Native.WM_DESTROY:
                    {
                        Layer l;
                        if (windows.TryGetValue(hwnd, out l)) { windows.Remove(hwnd); Free(l); }
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Ink layer WndProc", ex); }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        // The pen window, then (below it) the highlighter window, then the fill window, for one screen.
        List<IntPtr> CreateLayers(IntPtr parent, IntPtr insertAfter, Screen s, List<InkStroke> strokes, int canvasW, int canvasH,
                                  Bitmap picture, FitMode fit)
        {
            var made = new List<IntPtr>();
            int w = s.Bounds.Width, h = s.Bounds.Height;
            if (w <= 0 || h <= 0 || !Native.IsWindow(parent)) return made;
            var m = InkMapping.Fill(canvasW, canvasH, w, h);
            // Eraser paths go with each (they clear whatever was drawn before them) but don't make a window bigger.
            var pens = strokes.Where(st => st.Tool != InkTool.Highlighter && st.Tool != InkTool.Fill).ToList();
            var highlights = strokes.Where(st => st.Tool == InkTool.Highlighter || st.Tool == InkTool.Erase).ToList();
            var fills = strokes.Where(st => st.Tool == InkTool.Fill || st.Tool == InkTool.Erase).ToList();
            Rectangle penCrop = Crop(pens, m, w, h), highlightCrop = Crop(highlights, m, w, h), fillCrop = Crop(fills, m, w, h);
            IntPtr after = insertAfter;
            if (!penCrop.IsEmpty)
            {
                int[] px;
                using (Bitmap below = Beneath(penCrop, picture, fit, w, h, fills, highlights, m)) px = RenderOpaque(pens, m, penCrop, below);
                IntPtr hwnd = CreateLayer(parent, after, s, penCrop, px, 255);
                if (hwnd != IntPtr.Zero) { made.Add(hwnd); after = hwnd; }
            }
            if (!highlightCrop.IsEmpty)
            {
                IntPtr hwnd = CreateLayer(parent, after, s, highlightCrop, RenderHighlights(highlights, m, highlightCrop), InkRenderer.HighlighterAlpha);
                if (hwnd != IntPtr.Zero) { made.Add(hwnd); after = hwnd; }
            }
            if (!fillCrop.IsEmpty)
            {
                int[] px;
                using (Bitmap below = Beneath(fillCrop, picture, fit, w, h, null, null, m)) px = RenderOpaque(fills, m, fillCrop, below);
                IntPtr hwnd = CreateLayer(parent, after, s, fillCrop, px, 255);
                if (hwnd != IntPtr.Zero) made.Add(hwnd);
            }
            return made;
        }

        // What shows beneath a window (cropped): the wallpaper's picture, then the fills and highlighters (if given). Null
        // when there is nothing (no picture and no drawing below).
        static Bitmap Beneath(Rectangle crop, Bitmap picture, FitMode fit, int screenW, int screenH, List<InkStroke> fills,
                               List<InkStroke> highlights, InkMapping m)
        {
            bool anyFill = fills != null && fills.Any(st => st.Tool == InkTool.Fill);
            bool anyHighlight = highlights != null && highlights.Any(st => st.Tool == InkTool.Highlighter);
            if (picture == null && !anyFill && !anyHighlight) return null;
            var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                InkRenderer.Prepare(g);
                g.TranslateTransform(-crop.Left, -crop.Top);
                if (picture != null) InkRenderer.DrawPicture(g, picture, new Rectangle(0, 0, screenW, screenH), fit);
                g.ResetTransform();
                if (anyFill) using (Bitmap layer = RenderStrokes(fills, m, crop, false)) g.DrawImageUnscaled(layer, 0, 0);
                if (anyHighlight) using (Bitmap layer = RenderStrokes(highlights, m, crop, false)) g.DrawImageUnscaled(layer, 0, 0);
            }
            return bmp;
        }

        // Only as big as the drawing: less memory, and less for the compositor to blend while a video plays underneath.
        static Rectangle Crop(List<InkStroke> strokes, InkMapping m, int w, int h)
        {
            RectangleF area = RectangleF.Empty;
            foreach (var st in strokes)
            {
                if (st.Tool == InkTool.Erase) continue;
                RectangleF r = m.ToTarget(st.Bounds);
                area = area.IsEmpty ? r : RectangleF.Union(area, r);
            }
            if (area.IsEmpty) return Rectangle.Empty;
            Rectangle crop = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(area, 3, 3)), new Rectangle(0, 0, w, h));
            return crop.Width > 0 && crop.Height > 0 ? crop : Rectangle.Empty;
        }

        IntPtr CreateLayer(IntPtr parent, IntPtr insertAfter, Screen s, Rectangle crop, int[] pixels, byte alpha)
        {
            Layer layer = MakeLayer(crop.Width, crop.Height, pixels);
            if (layer == null) return IntPtr.Zero;
            IntPtr hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | InkNative.WS_EX_NOPARENTNOTIFY,
                ClassName, "LiveWall ink", Native.WS_CHILD | Native.WS_CLIPSIBLINGS | Native.WS_DISABLED,
                s.BoundsInParent.Left + crop.Left, s.BoundsInParent.Top + crop.Top, crop.Width, crop.Height,
                parent, IntPtr.Zero, Native.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Log.Warn("Ink layer window: " + Marshal.GetLastWin32Error()); Free(layer); return IntPtr.Zero; }
            windows[hwnd] = layer;
            if (!Native.SetLayeredWindowAttributes(hwnd, KeyColorRef, alpha, alpha == 255 ? Native.LWA_COLORKEY : Native.LWA_COLORKEY | Native.LWA_ALPHA))
            {
                Log.Warn("Could not show drawings behind the desktop icons: " + Marshal.GetLastWin32Error());
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            Native.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            return hwnd;
        }

        // Pens (or fills): solid, and their anti-aliased edges blended with what shows beneath (`below`, may be null);
        // hard edges where nothing opaque is beneath.
        static int[] RenderOpaque(List<InkStroke> strokes, InkMapping m, Rectangle crop, Bitmap below)
        {
            int[] ink, under = null, blended = null;
            using (Bitmap layer = RenderStrokes(strokes, m, crop, false))
            {
                ink = Pixels(layer);
                if (below != null)
                {
                    under = Pixels(below);
                    using (var bmp = new Bitmap(below))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(layer, 0, 0);
                        blended = Pixels(bmp);
                    }
                }
            }
            for (int i = 0; i < ink.Length; i++)
            {
                int a = (ink[i] >> 24) & 0xFF;
                if (blended != null && ((under[i] >> 24) & 0xFF) == 255) ink[i] = a < 24 ? KeyArgb : NotKey(blended[i] | unchecked((int)0xFF000000));
                else ink[i] = a < 128 ? KeyArgb : NotKey(Unpremultiply(ink[i]));
            }
            return ink;
        }

        // Highlighters: opaque here; the window's constant alpha makes them translucent.
        static int[] RenderHighlights(List<InkStroke> strokes, InkMapping m, Rectangle crop)
        {
            int[] px;
            using (Bitmap layer = RenderStrokes(strokes, m, crop, true)) px = Pixels(layer);
            for (int i = 0; i < px.Length; i++)
                px[i] = ((px[i] >> 24) & 0xFF) < 128 ? KeyArgb : NotKey(Unpremultiply(px[i]));
            return px;
        }

        // The elements on a transparent layer (erasers clear what is under them).
        static Bitmap RenderStrokes(List<InkStroke> strokes, InkMapping m, Rectangle crop, bool opaque)
        {
            var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                InkRenderer.Prepare(g);
                g.TranslateTransform(-crop.Left, -crop.Top);
                foreach (var st in strokes) InkRenderer.DrawStroke(g, st, m, opaque);
            }
            return bmp;
        }

        static int[] Pixels(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var px = new int[bmp.Width * bmp.Height];
                for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width, bmp.Width);
                return px;
            }
            finally { bmp.UnlockBits(data); }
        }

        static int Unpremultiply(int p)
        {
            int a = (p >> 24) & 0xFF;
            if (a == 0 || a == 255) return p | unchecked((int)0xFF000000);
            int r = Math.Min(255, ((p >> 16) & 0xFF) * 255 / a), g = Math.Min(255, ((p >> 8) & 0xFF) * 255 / a), b = Math.Min(255, (p & 0xFF) * 255 / a);
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        static int NotKey(int p) { return p == KeyArgb ? p ^ 1 : p; }

        static Layer MakeLayer(int w, int h, int[] pixels)
        {
            var layer = new Layer { Width = w, Height = h };
            layer.Dc = InkNative.CreateCompatibleDC(IntPtr.Zero);
            var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            layer.Bitmap = InkNative.CreateDIBSection(layer.Dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (layer.Bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Log.Warn("Ink layer bitmap " + w + "x" + h + " could not be created");
                InkNative.DeleteDC(layer.Dc);
                return null;
            }
            layer.Old = InkNative.SelectObject(layer.Dc, layer.Bitmap);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            return layer;
        }

        static void Free(Layer l)
        {
            if (l.Dc == IntPtr.Zero) return;
            InkNative.SelectObject(l.Dc, l.Old);
            InkNative.DeleteObject(l.Bitmap);
            InkNative.DeleteDC(l.Dc);
            l.Dc = IntPtr.Zero;
        }

        void DestroyAll()
        {
            foreach (IntPtr h in windows.Keys.ToArray()) if (Native.IsWindow(h)) Native.DestroyWindow(h);
            foreach (Layer l in windows.Values) Free(l);   // windows Explorer already destroyed
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
