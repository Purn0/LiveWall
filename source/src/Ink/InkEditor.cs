using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    internal enum EditorTool { Pen, Highlighter, Eraser }

    // Full-screen drawing surface for one screen: a per-pixel-alpha window (see LayeredBitmap) that shows the board or the
    // wallpaper underneath, plus a small floating toolbar. Pen (with pressure and the eraser end), touch and mouse.
    // Every finished stroke is written to the drawing's file immediately.
    internal sealed class InkEditor : Form
    {
        sealed class UndoAction
        {
            public readonly List<string> Added = new List<string>();
            public readonly List<string> Erased = new List<string>();
        }

        // Remembered between sessions while LiveWall runs.
        static readonly Dictionary<string, int> lastColor = new Dictionary<string, int>();
        static EditorTool lastTool = EditorTool.Pen;
        static int lastSize = 1;

        static readonly float[] PenSizes = { 2.5f, 5f, 10f };        // canvas units per 1080 canvas pixels
        static readonly float[] EraserSizes = { 10f, 22f, 44f };     // screen pixels at 96 DPI

        public readonly InkDocument Document;
        public readonly bool IsBoard;
        public Rectangle Monitor { get; private set; }
        public bool SwitchBoardRequested { get; private set; }
        public event EventHandler Finished;

        readonly string author, header;
        readonly int seed;
        readonly Image picture;          // wallpaper under the drawing (wallpaper mode), may be null
        readonly FitMode fit;
        readonly bool dailyBoard;
        LayeredBitmap frame;             // what is on screen
        Bitmap baseLayer;                // background + finished strokes
        Bitmap background;               // background only
        InkMapping map;
        float unit, dpiScale = 1;
        InkToolbar toolbar;

        EditorTool tool;
        int colorIndex, sizeIndex;

        // Current contact.
        bool active, erasing, liveHasPressure;
        readonly List<InkPoint> live = new List<InkPoint>();
        InkTool liveTool;
        int liveArgb;
        float liveWidth;
        PointF smooth, lastRaw, lastErase;
        byte lastPressure;
        Rectangle liveDirty;
        UndoAction eraseAction;

        readonly Stack<UndoAction> undo = new Stack<UndoAction>(), redo = new Stack<UndoAction>();
        List<InkStroke> visibleCache;
        int visibleRevision = -1;
        Cursor eraserCursor;
        IntPtr eraserCursorIcon;
        bool closing;

        public InkEditor(InkDocument doc, bool isBoard, bool dailyBoard, Rectangle monitor, string author, string header, int seed,
                         Image picture, FitMode fit)
        {
            Document = doc;
            IsBoard = isBoard;
            this.dailyBoard = dailyBoard;
            Monitor = monitor;
            this.author = InkDocument.Safe(author);
            this.header = header;
            this.seed = seed;
            this.picture = picture;
            this.fit = fit;

            Text = isBoard ? "LiveWall board" : "LiveWall drawing";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = monitor;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);

            map = InkMapping.Fill(doc.CanvasWidth, doc.CanvasHeight, monitor.Width, monitor.Height);
            unit = Math.Max(0.5f, doc.CanvasHeight / 1080f);
            tool = lastTool;
            sizeIndex = lastSize;
            int c;
            colorIndex = lastColor.TryGetValue(ColorKey, out c) ? c : InkRenderer.DefaultColor(doc.Background);
        }

        string ColorKey { get { return IsBoard ? (InkRenderer.IsDark(Document.Background) ? "dark" : "light") : "wallpaper"; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW);
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { uint dpi = InkNative.GetDpiForWindow(Handle); if (dpi > 0) dpiScale = dpi / 96f; } catch { }
            // Pen: no press-and-hold right click and no flicks while drawing.
            InkNative.SetProp(Handle, "MicrosoftTabletPenServiceProperty", new IntPtr(0x1 | 0x8 | 0x10 | 0x10000));
            frame = new LayeredBitmap(Monitor.Width, Monitor.Height);
            baseLayer = new Bitmap(Monitor.Width, Monitor.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            background = new Bitmap(Monitor.Width, Monitor.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            RenderBackground();
            RenderAll(false);
            frame.Present(Handle, Monitor.Location, null);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            toolbar = new InkToolbar(this, BuildToolbar(), dpiScale);
            toolbar.Show(this);
            UpdateCursor();
            Activate();
        }

        // ------------------------------------------------------------------ rendering

        void RenderBackground()
        {
            using (var g = Graphics.FromImage(background))
            {
                InkRenderer.Prepare(g);
                var r = new Rectangle(0, 0, Monitor.Width, Monitor.Height);
                if (IsBoard) InkRenderer.DrawBackground(g, Document.Background, r, map, header, seed);
                else if (picture != null) InkRenderer.DrawPicture(g, picture, r, fit);
                else
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, r);
                }
            }
        }

        List<InkStroke> VisibleStrokes
        {
            get
            {
                if (visibleCache == null || visibleRevision != Document.Revision)
                {
                    visibleCache = Document.VisibleStrokes();
                    visibleRevision = Document.Revision;
                }
                return visibleCache;
            }
        }

        void RenderAll(bool present)
        {
            using (var g = Graphics.FromImage(baseLayer))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(background, ClientArea, ClientArea, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                InkRenderer.Prepare(g);
                InkRenderer.DrawStrokes(g, VisibleStrokes, map);
            }
            using (var g = Graphics.FromImage(frame.Bitmap))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(baseLayer, ClientArea, ClientArea, GraphicsUnit.Pixel);
            }
            if (present) frame.Present(Handle, Monitor.Location, null);
        }

        static void CopyRect(Bitmap src, Graphics dst, Rectangle r)
        {
            var mode = dst.CompositingMode;
            dst.CompositingMode = CompositingMode.SourceCopy;
            dst.DrawImage(src, r, r, GraphicsUnit.Pixel);
            dst.CompositingMode = mode;
        }

        Rectangle ClientArea { get { return new Rectangle(0, 0, Monitor.Width, Monitor.Height); } }

        Rectangle TargetRect(RectangleF canvasRect, int pad)
        {
            var r = Rectangle.Inflate(Rectangle.Round(map.ToTarget(canvasRect)), pad, pad);
            return Rectangle.Intersect(r, ClientArea);
        }

        // ------------------------------------------------------------------ strokes

        void Begin(Point client, byte pressure, bool eraserTip)
        {
            if (closing) return;
            if (active) End();
            active = true;
            if (eraserTip || tool == EditorTool.Eraser)
            {
                erasing = true;
                eraseAction = new UndoAction();
                lastErase = client;
                EraseAt(client, client);
                return;
            }
            erasing = false;
            live.Clear();
            liveTool = tool == EditorTool.Highlighter ? InkTool.Highlighter : InkTool.Pen;
            liveArgb = InkRenderer.Palette[colorIndex].ToArgb();
            liveWidth = PenSizes[sizeIndex] * unit * (liveTool == InkTool.Highlighter ? 4f : 1f);
            liveHasPressure = false;
            PointF c = map.ToCanvas(client.X, client.Y);
            smooth = c;
            lastRaw = c;
            lastPressure = pressure;
            live.Add(new InkPoint(c.X, c.Y, pressure));
            liveDirty = Rectangle.Empty;
            DrawLive();
        }

        void Extend(Point client, byte pressure)
        {
            if (!active) return;
            if (erasing) { EraseAt(lastErase, client); lastErase = client; return; }
            PointF raw = map.ToCanvas(client.X, client.Y);
            lastRaw = raw;
            lastPressure = pressure;
            // Light smoothing: removes the jitter of mice and digitizers without visible lag.
            smooth = new PointF(smooth.X + (raw.X - smooth.X) * 0.6f, smooth.Y + (raw.Y - smooth.Y) * 0.6f);
            InkPoint last = live[live.Count - 1];
            float dx = smooth.X - last.X, dy = smooth.Y - last.Y;
            if (dx * dx + dy * dy < 0.8f * unit * 0.8f * unit) return;
            AddLivePoint(new InkPoint(smooth.X, smooth.Y, pressure));
        }

        void AddLivePoint(InkPoint p)
        {
            if (p.P != live[0].P) liveHasPressure = true;
            live.Add(p);
            DrawLive();
        }

        void DrawLive()
        {
            Rectangle dirty;
            using (var g = frame.CreateGraphics())
            {
                if (liveTool == InkTool.Highlighter)
                {
                    // Translucent: redraw the whole stroke over the untouched layer, or overlaps would darken.
                    RectangleF b = PointBounds(live, liveWidth);
                    Rectangle now = TargetRect(b, 3);
                    dirty = liveDirty.IsEmpty ? now : Rectangle.Union(liveDirty, now);
                    CopyRect(baseLayer, g, dirty);
                    g.SetClip(dirty);
                    var arr = live.ToArray();
                    InkRenderer.DrawPoints(g, liveTool, liveArgb, liveWidth, arr, arr.Length, false, map);
                    liveDirty = now;
                }
                else
                {
                    InkPoint a = live.Count > 1 ? live[live.Count - 2] : live[0], b = live[live.Count - 1];
                    if (!liveHasPressure) { a.P = live[0].P; b.P = live[0].P; }
                    if (live.Count == 1) InkRenderer.DrawPoints(g, liveTool, liveArgb, liveWidth, new[] { b }, 1, false, map);
                    else InkRenderer.DrawSegment(g, liveTool, liveArgb, liveWidth, a, b, map);
                    var seg = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    dirty = TargetRect(RectangleF.Inflate(seg, liveWidth * 1.8f, liveWidth * 1.8f), 3);
                }
            }
            frame.Present(Handle, Monitor.Location, dirty);
        }

        static RectangleF PointBounds(List<InkPoint> pts, float width)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            float pad = width;
            return RectangleF.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
        }

        void End()
        {
            if (!active) return;
            active = false;
            if (erasing)
            {
                erasing = false;
                if (eraseAction != null && eraseAction.Erased.Count > 0) { undo.Push(eraseAction); redo.Clear(); }
                eraseAction = null;
                return;
            }
            if (live.Count == 0) return;
            InkPoint tail = live[live.Count - 1];
            if (Math.Abs(tail.X - lastRaw.X) + Math.Abs(tail.Y - lastRaw.Y) > 0.5f) live.Add(new InkPoint(lastRaw.X, lastRaw.Y, lastPressure));

            var stroke = new InkStroke(Guid.NewGuid().ToString("N"), author, DateTime.UtcNow.Ticks, liveTool, liveArgb, liveWidth, live.ToArray());
            live.Clear();
            Document.Add(stroke);
            var action = new UndoAction();
            action.Added.Add(stroke.Id);
            undo.Push(action);
            redo.Clear();

            // Replace the live drawing with the final rendering (identical to how it looks on the wallpaper).
            Rectangle r = TargetRect(stroke.Bounds, 3);
            if (!liveDirty.IsEmpty) r = Rectangle.Union(r, liveDirty);
            using (var g = frame.CreateGraphics())
            {
                CopyRect(baseLayer, g, r);
                g.SetClip(r);
                InkRenderer.DrawStroke(g, stroke, map);
            }
            using (var g = Graphics.FromImage(baseLayer)) CopyRect(frame.Bitmap, g, r);
            liveDirty = Rectangle.Empty;
            frame.Present(Handle, Monitor.Location, r);
            if (toolbar != null) toolbar.Invalidate();
        }

        void EraseAt(PointF from, PointF to)
        {
            float radius = EraserSizes[sizeIndex] * dpiScale / map.Scale;
            PointF a = map.ToCanvas(from.X, from.Y), b = map.ToCanvas(to.X, to.Y);
            float len = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1f, radius * 0.5f)));
            var hit = new List<string>();
            foreach (var s in VisibleStrokes)
            {
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    if (s.HitTest(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, radius)) { hit.Add(s.Id); break; }
                }
            }
            if (hit.Count == 0) return;
            var done = Document.Erase(hit, author);
            if (eraseAction != null) eraseAction.Erased.AddRange(done);
            RenderAll(true);
            if (toolbar != null) toolbar.Invalidate();
        }

        // ------------------------------------------------------------------ commands

        public void Undo()
        {
            if (active || undo.Count == 0) return;
            var a = undo.Pop();
            Document.Erase(a.Added, author);
            Document.Restore(a.Erased, author);
            redo.Push(a);
            RenderAll(true);
            if (toolbar != null) toolbar.Invalidate();
        }

        public void Redo()
        {
            if (active || redo.Count == 0) return;
            var a = redo.Pop();
            Document.Restore(a.Added, author);
            Document.Erase(a.Erased, author);
            undo.Push(a);
            RenderAll(true);
            if (toolbar != null) toolbar.Invalidate();
        }

        public void ClearAll()
        {
            if (active) End();
            var ids = VisibleStrokes.Select(s => s.Id).ToList();
            if (ids.Count == 0) return;
            var a = new UndoAction();
            a.Erased.AddRange(Document.Erase(ids, author));
            undo.Push(a);
            redo.Clear();
            RenderAll(true);
            if (toolbar != null) toolbar.Invalidate();
        }

        public void NextBackground()
        {
            if (!IsBoard) return;
            int i = Array.IndexOf(InkRenderer.Styles, Document.Background);
            string next = InkRenderer.Styles[(i + 1) % InkRenderer.Styles.Length];
            bool wasDark = InkRenderer.IsDark(Document.Background);
            Document.SetBackground(next, author);
            if (wasDark != InkRenderer.IsDark(next))
            {
                // Switch to a pen that shows up on the new background.
                int c;
                colorIndex = lastColor.TryGetValue(ColorKey, out c) ? c : InkRenderer.DefaultColor(next);
            }
            RenderBackground();
            RenderAll(true);
            if (toolbar != null) toolbar.Invalidate();
        }

        public void SetTool(EditorTool t) { tool = t; lastTool = t; UpdateCursor(); if (toolbar != null) toolbar.Invalidate(); }

        public void SetColor(int i)
        {
            colorIndex = Math.Max(0, Math.Min(InkRenderer.Palette.Length - 1, i));
            lastColor[ColorKey] = colorIndex;
            if (tool == EditorTool.Eraser) SetTool(EditorTool.Pen);
            if (toolbar != null) toolbar.Invalidate();
        }

        public void SetSize(int i) { sizeIndex = Math.Max(0, Math.Min(2, i)); lastSize = sizeIndex; UpdateCursor(); if (toolbar != null) toolbar.Invalidate(); }

        public void RequestSwitchBoard() { SwitchBoardRequested = true; Finish(); }

        public void Finish()
        {
            if (closing) return;
            if (active) End();
            Close();
        }

        void UpdateCursor()
        {
            if (tool != EditorTool.Eraser) { Cursor = Cursors.Cross; return; }
            int d = Math.Max(8, Math.Min(250, (int)(EraserSizes[sizeIndex] * dpiScale * 2)));
            Cursor old = eraserCursor;
            IntPtr oldIcon = eraserCursorIcon;
            using (var bmp = new Bitmap(d + 2, d + 2))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var p = new Pen(Color.FromArgb(220, 0, 0, 0), 3)) g.DrawEllipse(p, 1, 1, d - 1, d - 1);
                    using (var p = new Pen(Color.White, 1.4f)) g.DrawEllipse(p, 1, 1, d - 1, d - 1);
                }
                eraserCursorIcon = bmp.GetHicon();   // an icon's hot spot is its center
                eraserCursor = new Cursor(eraserCursorIcon);
            }
            Cursor = eraserCursor;
            if (old != null) old.Dispose();
            if (oldIcon != IntPtr.Zero) DestroyIcon(oldIcon);
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        // ------------------------------------------------------------------ toolbar

        List<InkToolbar.Item> BuildToolbar()
        {
            var items = new List<InkToolbar.Item>();
            items.Add(InkToolbar.Item.Button("\uE70F", "P", "Pen (P)", () => SetTool(EditorTool.Pen), () => tool == EditorTool.Pen));
            items.Add(InkToolbar.Item.Button("\uE7E6", "H", "Highlighter (H)", () => SetTool(EditorTool.Highlighter), () => tool == EditorTool.Highlighter));
            items.Add(InkToolbar.Item.Button("\uE75C", "E", "Eraser (E, or the pen's eraser end / right mouse button)", () => SetTool(EditorTool.Eraser), () => tool == EditorTool.Eraser));
            items.Add(InkToolbar.Item.Separator());
            for (int i = 0; i < InkRenderer.Palette.Length; i++)
            {
                int idx = i;
                items.Add(InkToolbar.Item.ColorSwatch(InkRenderer.Palette[i], "Color " + (i + 1) + " (" + (i + 1) + ")", () => SetColor(idx),
                    () => colorIndex == idx && tool != EditorTool.Eraser));
            }
            items.Add(InkToolbar.Item.Separator());
            string[] names = { "Thin", "Medium", "Thick" };
            float[] dots = { 4, 7, 11 };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                items.Add(InkToolbar.Item.Dot(dots[i], names[i] + " ([ and ] change size)", () => SetSize(idx), () => sizeIndex == idx));
            }
            items.Add(InkToolbar.Item.Separator());
            items.Add(InkToolbar.Item.Button("\uE7A7", "\u21B6", "Undo (Ctrl+Z)", Undo, null, () => undo.Count > 0));
            items.Add(InkToolbar.Item.Button("\uE7A6", "\u21B7", "Redo (Ctrl+Y)", Redo, null, () => redo.Count > 0));
            items.Add(InkToolbar.Item.Button("\uE74D", "X", "Clear everything (Delete) - can be undone", ClearAll, null, () => VisibleStrokes.Count > 0));
            if (IsBoard)
            {
                items.Add(InkToolbar.Item.Separator());
                items.Add(InkToolbar.Item.Button("\uE790", "B", "Background: " + InkRenderer.StyleName(Document.Background) + " (click to change)", NextBackground, null));
                items.Add(InkToolbar.Item.Button(dailyBoard ? "\uE787" : "\uE718", dailyBoard ? "D" : "P",
                    dailyBoard ? "Today's board. Click for the permanent board (Tab)" : "Permanent board. Click for today's board (Tab)",
                    RequestSwitchBoard, null));
            }
            items.Add(InkToolbar.Item.Separator());
            items.Add(InkToolbar.Item.Accent("\uE73E", "OK", "Done (Esc)", Finish));
            return items;
        }

        public string BackgroundTip { get { return "Background: " + InkRenderer.StyleName(Document.Background) + " (click to change)"; } }

        // ------------------------------------------------------------------ input

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Begin(e.Location, 128, false);
            else if (e.Button == MouseButtons.Right) Begin(e.Location, 128, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (active) Extend(e.Location, 128);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (active) End();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (active && !Capture && MouseButtons == MouseButtons.None) End();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == InkNative.WM_POINTERDOWN || m.Msg == InkNative.WM_POINTERUPDATE || m.Msg == InkNative.WM_POINTERUP)
            {
                if (HandlePointer(m.Msg, (uint)(m.WParam.ToInt64() & 0xFFFF))) { m.Result = IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }

        // Pen and touch arrive as WM_POINTER messages (mouse keeps using the normal mouse messages).
        bool HandlePointer(int msg, uint id)
        {
            uint type;
            if (!InkNative.GetPointerType(id, out type) || (type != InkNative.PT_PEN && type != InkNative.PT_TOUCH)) return false;
            POINTER_INFO info;
            byte pressure = 128;
            bool eraserTip = false;
            if (type == InkNative.PT_PEN)
            {
                POINTER_PEN_INFO pen;
                if (!InkNative.GetPointerPenInfo(id, out pen)) return false;
                info = pen.pointerInfo;
                if ((pen.penMask & InkNative.PEN_MASK_PRESSURE) != 0) pressure = (byte)Math.Min(255, pen.pressure * 255 / 1024);
                eraserTip = (pen.penFlags & (InkNative.PEN_FLAG_ERASER | InkNative.PEN_FLAG_INVERTED)) != 0;
            }
            else
            {
                if (!InkNative.GetPointerInfo(id, out info)) return false;
                if ((info.pointerFlags & InkNative.POINTER_FLAG_PRIMARY) == 0) return true;   // one finger draws
            }
            Point c = PointToClient(new Point(info.ptPixelLocation.X, info.ptPixelLocation.Y));
            bool contact = (info.pointerFlags & InkNative.POINTER_FLAG_INCONTACT) != 0;
            bool canceled = (info.pointerFlags & InkNative.POINTER_FLAG_CANCELED) != 0;
            if (msg == InkNative.WM_POINTERDOWN) Begin(c, pressure, eraserTip);
            else if (msg == InkNative.WM_POINTERUPDATE)
            {
                if (canceled) End();
                else if (contact) { if (!active) Begin(c, pressure, eraserTip); else Extend(c, pressure); }
                else if (active) End();
            }
            else { if (active && !canceled) Extend(c, pressure); End(); }
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: case Keys.Enter: Finish(); return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.P: SetTool(EditorTool.Pen); return true;
                case Keys.H: SetTool(EditorTool.Highlighter); return true;
                case Keys.E: SetTool(EditorTool.Eraser); return true;
                case Keys.OemOpenBrackets: SetSize(sizeIndex - 1); return true;
                case Keys.OemCloseBrackets: SetSize(sizeIndex + 1); return true;
                case Keys.Delete: ClearAll(); return true;
                case Keys.B: NextBackground(); return true;
                case Keys.Tab: if (IsBoard) RequestSwitchBoard(); return true;
            }
            Keys k = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.None && k >= Keys.D1 && k <= Keys.D9) { SetColor(k - Keys.D1); return true; }
            if ((keyData & Keys.Modifiers) == Keys.None && k >= Keys.NumPad1 && k <= Keys.NumPad9) { SetColor(k - Keys.NumPad1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (active) End();
            closing = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (toolbar != null && !toolbar.IsDisposed) toolbar.Close();
            if (frame != null) { frame.Dispose(); frame = null; }
            if (baseLayer != null) { baseLayer.Dispose(); baseLayer = null; }
            if (background != null) { background.Dispose(); background = null; }
            if (picture != null) picture.Dispose();
            if (eraserCursor != null) { Cursor = Cursors.Default; eraserCursor.Dispose(); eraserCursor = null; }
            if (eraserCursorIcon != IntPtr.Zero) { DestroyIcon(eraserCursorIcon); eraserCursorIcon = IntPtr.Zero; }
            var h = Finished;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    // The floating toolbar: owner-drawn, never takes focus away from the drawing (so shortcuts keep working).
    internal sealed class InkToolbar : Form
    {
        internal sealed class Item
        {
            public string Glyph, Fallback, Tip;
            public Action Click;
            public Func<bool> Selected, Enabled;
            public Color Swatch = Color.Empty;
            public float DotSize;
            public bool IsSeparator, IsAccent;
            public Rectangle Rect;

            public static Item Button(string glyph, string fallback, string tip, Action click, Func<bool> selected, Func<bool> enabled = null)
            { return new Item { Glyph = glyph, Fallback = fallback, Tip = tip, Click = click, Selected = selected, Enabled = enabled }; }
            public static Item ColorSwatch(Color c, string tip, Action click, Func<bool> selected)
            { return new Item { Swatch = c, Tip = tip, Click = click, Selected = selected }; }
            public static Item Dot(float size, string tip, Action click, Func<bool> selected)
            { return new Item { DotSize = size, Tip = tip, Click = click, Selected = selected }; }
            public static Item Separator() { return new Item { IsSeparator = true }; }
            public static Item Accent(string glyph, string fallback, string tip, Action click)
            { return new Item { Glyph = glyph, Fallback = fallback, Tip = tip, Click = click, IsAccent = true }; }
        }

        readonly InkEditor editor;
        readonly List<Item> items;
        readonly float scale;
        readonly Font glyphFont, textFont;
        readonly bool haveGlyphs;
        readonly ToolTip tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
        int hover = -1, grip;
        string shownTip;

        public InkToolbar(InkEditor editor, List<Item> items, float scale)
        {
            this.editor = editor;
            this.items = items;
            this.scale = scale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 33, 36);
            DoubleBuffered = true;
            haveGlyphs = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons" || f.Name == "Segoe MDL2 Assets");
            string glyphFamily = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            glyphFont = haveGlyphs ? new Font(glyphFamily, 16 * scale, GraphicsUnit.Pixel) : null;
            textFont = new Font("Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);

            int s = (int)(38 * scale), pad = (int)(6 * scale);
            grip = (int)(16 * scale);
            int x = pad + grip;
            foreach (var it in items)
            {
                int w = it.IsSeparator ? (int)(11 * scale) : s;
                it.Rect = new Rectangle(x, pad, w, s);
                x += w;
            }
            ClientSize = new Size(x + pad, s + pad * 2);
            Location = new Point(editor.Monitor.Left + (editor.Monitor.Width - ClientSize.Width) / 2, editor.Monitor.Top + (int)(18 * scale));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // Windows 11: rounded corners (ignored elsewhere)
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // Grip dots.
            using (var b = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
            {
                float d = 3 * scale, cx = (6 * scale + grip) / 2f, cy = ClientSize.Height / 2f;
                for (int i = -1; i <= 1; i++) { g.FillEllipse(b, cx - d * 1.2f, cy + i * d * 2 - d / 2, d, d); g.FillEllipse(b, cx + d * 0.2f, cy + i * d * 2 - d / 2, d, d); }
            }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                Rectangle r = it.Rect;
                if (it.IsSeparator)
                {
                    using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1, scale)))
                        g.DrawLine(p, r.Left + r.Width / 2f, r.Top + 8 * scale, r.Left + r.Width / 2f, r.Bottom - 8 * scale);
                    continue;
                }
                bool enabled = it.Enabled == null || it.Enabled();
                bool selected = it.Selected != null && it.Selected();
                var inner = Rectangle.Inflate(r, -(int)(2 * scale), -(int)(2 * scale));
                if (it.IsAccent) FillRound(g, inner, Color.FromArgb(i == hover ? 255 : 230, 26, 115, 232));
                else if (selected) FillRound(g, inner, Color.FromArgb(255, 80, 84, 90));
                else if (i == hover && enabled) FillRound(g, inner, Color.FromArgb(255, 60, 64, 67));

                Color fg = enabled ? Color.White : Color.FromArgb(90, 255, 255, 255);
                float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
                if (it.Swatch != Color.Empty)
                {
                    float d = 20 * scale;
                    using (var b = new SolidBrush(it.Swatch)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                    using (var p = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1, scale))) g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
                }
                else if (it.DotSize > 0)
                {
                    float d = it.DotSize * scale;
                    using (var b = new SolidBrush(fg)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                }
                else
                {
                    Font f = haveGlyphs ? glyphFont : textFont;
                    string t = haveGlyphs ? it.Glyph : it.Fallback;
                    using (var b = new SolidBrush(fg))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(t, f, b, new RectangleF(r.Left, r.Top, r.Width, r.Height), sf);
                }
            }
        }

        void FillRound(Graphics g, Rectangle r, Color c)
        {
            float rad = 6 * scale;
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.Left, r.Top, rad * 2, rad * 2, 180, 90);
                path.AddArc(r.Right - rad * 2, r.Top, rad * 2, rad * 2, 270, 90);
                path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
                path.AddArc(r.Left, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
                path.CloseFigure();
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
            }
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++) if (!items[i].IsSeparator && items[i].Rect.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            string t = h >= 0 ? (items[h].Glyph == "\uE790" ? editor.BackgroundTip : items[h].Tip) : null;
            if (t != shownTip)
            {
                shownTip = t;
                if (t == null) tip.Hide(this);
                else tip.Show(t, this, items[h].Rect.Left, items[h].Rect.Bottom + (int)(8 * scale), 4000);
            }
            Cursor = e.X < grip + 6 * scale ? Cursors.SizeAll : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            shownTip = null;
            tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.X < grip + 6 * scale)
            {
                // Drag the toolbar by its grip.
                InkNative.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, new IntPtr(2 /*HTCAPTION*/), IntPtr.Zero);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h < 0) return;
            var it = items[h];
            if (it.Enabled != null && !it.Enabled()) return;
            if (it.Click != null) it.Click();
            if (!IsDisposed) Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tip.Dispose();
                if (glyphFont != null) glyphFont.Dispose();
                textFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
