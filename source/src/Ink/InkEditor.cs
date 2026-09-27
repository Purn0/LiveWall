using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    internal enum EditorTool { Pen, Highlighter, Eraser, Shape, Fill, Text, Picker }

    // Full-screen drawing surface for one screen: a per-pixel-alpha window (see LayeredBitmap) that shows the board or the
    // wallpaper underneath, plus a floating toolbar. Pen (with pressure and the eraser end), touch and mouse; shapes,
    // paint-bucket fills, text with emoji, an eyedropper and any color. Every change is written to the drawing's file
    // immediately.
    internal sealed class InkEditor : Form
    {
        sealed class UndoAction
        {
            public readonly List<string> Added = new List<string>();
            public readonly List<string> Erased = new List<string>();
        }

        // Remembered between sessions while LiveWall runs.
        static readonly Dictionary<string, int> lastColor = new Dictionary<string, int>();
        static readonly List<int> recentColors = new List<int>();
        static EditorTool lastTool = EditorTool.Pen;
        static InkTool lastShape = InkTool.Rectangle;
        static bool shapeFilled, textBold, textItalic;
        static float penSize = 5, highlighterSize = 20, eraserSize = 22, textSize = 48;   // pens and text: canvas units per 1080 canvas pixels; eraser: screen pixels at 96 DPI
        static string textFont = "Segoe UI", textEffect = InkText.Plain;
        static int textTab;
        static bool eraseWhole;         // eraser removes whole strokes (instead of only what it touches)

        const float PenMin = 1, PenMax = 60, HighlighterMin = 6, HighlighterMax = 120, EraserMin = 5, EraserMax = 150, TextMin = 10, TextMax = 300;

        public readonly InkDocument Document;
        public readonly bool IsBoard;
        public Rectangle Monitor { get; private set; }
        public Rectangle HeaderBounds { get; private set; }   // the board's date (screen coordinates), kept clear by the toolbar
        public bool SwitchBoardRequested { get; private set; }
        public event EventHandler Finished;

        readonly string author, header, dailyLabel, exportName;
        readonly int seed;
        readonly Image picture;          // wallpaper under the drawing (wallpaper mode), may be null
        readonly FitMode fit;
        readonly bool dailyBoard;
        LayeredBitmap frame;             // what is on screen
        Bitmap baseLayer;                // background + finished elements
        Bitmap background;               // background only
        Bitmap ink;                      // the elements alone (transparent elsewhere)
        InkMapping map;
        float unit, dpiScale = 1;
        InkToolbar toolbar;
        InkToolbar.Item colorItem, saveItem, eraserItem;
        ColorPicker colorPicker;
        TextPanel textPanel;

        EditorTool tool, toolBeforePicker = EditorTool.Pen;
        int color;

        // Current contact.
        bool active, erasing, shaping, liveHasPressure;
        readonly List<InkPoint> live = new List<InkPoint>();
        InkTool liveTool;
        int liveArgb;
        float liveWidth;
        PointF smooth, lastRaw, lastErase;
        byte lastPressure;
        Rectangle liveDirty;
        UndoAction eraseAction;
        InkPoint shapeStart, shapeEnd;
        readonly List<InkPoint> erasePath = new List<InkPoint>();   // partial eraser: this drag so far

        // Text being written (the panel is open).
        PointF textAt;
        InkStroke textEditing;           // existing text being changed (hidden meanwhile)
        Rectangle textPreview;
        string hiddenId;

        readonly Stack<UndoAction> undo = new Stack<UndoAction>(), redo = new Stack<UndoAction>();
        List<InkStroke> visibleCache;
        int visibleRevision = -1;
        Cursor eraserCursor;
        IntPtr eraserCursorIcon;
        bool closing;

        // `dailyLabel`: the daily board's name on the board switch ("Today" or a date); `exportName`: default file name
        // for "Save as picture".
        public InkEditor(InkDocument doc, bool isBoard, bool dailyBoard, string dailyLabel, Rectangle monitor, string author, string header,
                         int seed, Image picture, FitMode fit, string exportName)
        {
            Document = doc;
            IsBoard = isBoard;
            this.dailyBoard = dailyBoard;
            this.dailyLabel = dailyLabel ?? "Today";
            this.exportName = exportName ?? "Drawing";
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
            int c;
            color = lastColor.TryGetValue(ColorKey, out c) ? c : InkRenderer.Palette[InkRenderer.DefaultColor(doc.Background)].ToArgb();
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
            baseLayer = new Bitmap(Monitor.Width, Monitor.Height, PixelFormat.Format32bppPArgb);
            background = new Bitmap(Monitor.Width, Monitor.Height, PixelFormat.Format32bppPArgb);
            ink = new Bitmap(Monitor.Width, Monitor.Height, PixelFormat.Format32bppPArgb);
            RenderBackground();
            RenderAll(false);
            frame.Present(Handle, Monitor.Location, null);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            toolbar = new InkToolbar(this, Monitor, BuildToolbar(), dpiScale, HeaderBounds);
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
                if (IsBoard)
                {
                    RectangleF h = InkRenderer.DrawBackground(g, Document.Background, r, map, header, seed);
                    HeaderBounds = h.IsEmpty ? Rectangle.Empty : Rectangle.Round(new RectangleF(h.X + Monitor.Left, h.Y + Monitor.Top, h.Width, h.Height));
                }
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

        // What is drawn: everything visible except text being edited.
        IEnumerable<InkStroke> Rendered { get { return hiddenId == null ? VisibleStrokes : VisibleStrokes.Where(s => s.Id != hiddenId); } }

        // The elements go onto `ink` (transparent), which is then laid over the background: the eraser clears ink only.
        void RenderAll(bool present)
        {
            using (var g = Graphics.FromImage(ink))
            {
                g.Clear(Color.Transparent);
                InkRenderer.Prepare(g);
                InkRenderer.DrawStrokes(g, Rendered, map);
            }
            Compose(ClientArea, present);
        }

        // Redraws one area from the elements (keeps their order: a new fill under an older line stays under it).
        void RenderRegion(Rectangle r)
        {
            r = Rectangle.Intersect(r, ClientArea);
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var g = Graphics.FromImage(ink))
            {
                g.SetClip(r);
                g.Clear(Color.Transparent);
                InkRenderer.Prepare(g);
                foreach (var s in Rendered) if (TargetRect(s.Bounds, 4).IntersectsWith(r)) InkRenderer.DrawStroke(g, s, map);
            }
            Compose(r, true);
        }

        // baseLayer = background + ink in `r`, then onto the screen.
        void Compose(Rectangle r, bool present)
        {
            r = Rectangle.Intersect(r, ClientArea);
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var g = Graphics.FromImage(baseLayer))
            {
                CopyRect(background, g, r);
                g.DrawImage(ink, r, r, GraphicsUnit.Pixel);
            }
            using (var g = Graphics.FromImage(frame.Bitmap)) CopyRect(baseLayer, g, r);
            if (present) frame.Present(Handle, Monitor.Location, r == ClientArea ? (Rectangle?)null : r);
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

        void RefreshToolbar() { if (toolbar != null && !toolbar.IsDisposed) toolbar.RefreshAll(); }

        static string NewId() { return Guid.NewGuid().ToString("N"); }

        // ------------------------------------------------------------------ freehand strokes

        void Begin(Point client, byte pressure, bool eraserTip)
        {
            if (closing) return;
            if (toolbar != null) toolbar.CloseFlyout();
            if (active) End();
            if (eraserTip || tool == EditorTool.Eraser)
            {
                active = true;
                erasing = true;
                lastErase = client;
                if (eraseWhole)
                {
                    eraseAction = new UndoAction();
                    EraseAt(client, client);
                }
                else
                {
                    PointF c1 = map.ToCanvas(client.X, client.Y);
                    erasePath.Clear();
                    erasePath.Add(new InkPoint(c1.X, c1.Y, 128));
                    EraseLive(client, client);
                }
                return;
            }
            switch (tool)
            {
                case EditorTool.Picker: PickColor(client); return;
                case EditorTool.Fill: FillAt(client); return;
                case EditorTool.Text: PlaceText(client); return;
                case EditorTool.Shape:
                {
                    PointF c0 = map.ToCanvas(client.X, client.Y);
                    active = true;
                    shaping = true;
                    erasing = false;
                    shapeStart = shapeEnd = new InkPoint(c0.X, c0.Y, 128);
                    liveDirty = Rectangle.Empty;
                    DrawShapePreview();
                    return;
                }
            }
            active = true;
            erasing = false;
            live.Clear();
            liveTool = tool == EditorTool.Highlighter ? InkTool.Highlighter : InkTool.Pen;
            liveArgb = color;
            liveWidth = (liveTool == InkTool.Highlighter ? highlighterSize : penSize) * unit;
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
            if (erasing)
            {
                if (eraseWhole) EraseAt(lastErase, client);
                else
                {
                    PointF c1 = map.ToCanvas(client.X, client.Y);
                    InkPoint prev = erasePath[erasePath.Count - 1];
                    if (Math.Abs(c1.X - prev.X) + Math.Abs(c1.Y - prev.Y) < 1) return;
                    erasePath.Add(new InkPoint(c1.X, c1.Y, 128));
                    EraseLive(lastErase, client);
                }
                lastErase = client;
                return;
            }
            if (shaping) { shapeEnd = Constrain(map.ToCanvas(client.X, client.Y)); DrawShapePreview(); return; }
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
                if (eraseWhole)
                {
                    if (eraseAction != null && eraseAction.Erased.Count > 0) { undo.Push(eraseAction); redo.Clear(); }
                    eraseAction = null;
                }
                else CommitErasePath();
                return;
            }
            if (shaping) { shaping = false; CommitShape(); return; }
            if (live.Count == 0) return;
            InkPoint tail = live[live.Count - 1];
            if (Math.Abs(tail.X - lastRaw.X) + Math.Abs(tail.Y - lastRaw.Y) > 0.5f) live.Add(new InkPoint(lastRaw.X, lastRaw.Y, lastPressure));

            var stroke = new InkStroke(NewId(), author, DateTime.UtcNow.Ticks, liveTool, liveArgb, liveWidth, live.ToArray());
            live.Clear();
            Document.Add(stroke);
            var action = new UndoAction();
            action.Added.Add(stroke.Id);
            undo.Push(action);
            redo.Clear();

            // Replace the live drawing with the final rendering (identical to how it looks on the wallpaper). It is the
            // newest element, so it simply goes on top of the ink.
            Rectangle r = TargetRect(stroke.Bounds, 3);
            if (!liveDirty.IsEmpty) r = Rectangle.Union(r, liveDirty);
            using (var g = Graphics.FromImage(ink))
            {
                InkRenderer.Prepare(g);
                g.SetClip(r);
                InkRenderer.DrawStroke(g, stroke, map);
            }
            liveDirty = Rectangle.Empty;
            Compose(r, true);
            RefreshToolbar();
        }

        // ------------------------------------------------------------------ partial eraser

        float EraseDiameter { get { return eraserSize * dpiScale * 2 / map.Scale; } }   // canvas units, same as the cursor circle

        // Clears the ink under one step of the eraser, right away.
        void EraseLive(PointF from, PointF to)
        {
            float d = EraseDiameter * map.Scale;
            var r = Rectangle.FromLTRB((int)Math.Min(from.X, to.X), (int)Math.Min(from.Y, to.Y), (int)Math.Max(from.X, to.X) + 1, (int)Math.Max(from.Y, to.Y) + 1);
            r = Rectangle.Inflate(r, (int)(d / 2) + 3, (int)(d / 2) + 3);
            using (var g = Graphics.FromImage(ink))
            {
                InkRenderer.Prepare(g);
                InkRenderer.DrawErasePath(g, from == to ? new PointF[] { from } : new PointF[] { from, to }, d);
            }
            Compose(r, true);
        }

        // One eraser drag = one element (and one undo step), if it went over anything.
        void CommitErasePath()
        {
            if (erasePath.Count == 0) return;
            var s = new InkStroke(NewId(), author, DateTime.UtcNow.Ticks, InkTool.Erase, 0, EraseDiameter, erasePath.ToArray());
            erasePath.Clear();
            RectangleF b = s.Bounds;
            if (!VisibleStrokes.Any(v => v.Tool != InkTool.Erase && v.Bounds.IntersectsWith(b))) return;
            Document.Add(s);
            var action = new UndoAction();
            action.Added.Add(s.Id);
            undo.Push(action);
            redo.Clear();
            RefreshToolbar();
        }

        void EraseAt(PointF from, PointF to)
        {
            float radius = eraserSize * dpiScale / map.Scale;
            PointF a = map.ToCanvas(from.X, from.Y), b = map.ToCanvas(to.X, to.Y);
            float len = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1f, radius * 0.5f)));
            var hit = new List<string>();
            RectangleF dirty = RectangleF.Empty;
            foreach (var s in VisibleStrokes)
            {
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    if (s.HitTest(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, radius))
                    {
                        hit.Add(s.Id);
                        dirty = dirty.IsEmpty ? s.Bounds : RectangleF.Union(dirty, s.Bounds);
                        break;
                    }
                }
            }
            if (hit.Count == 0) return;
            var done = Document.Erase(hit, author);
            if (eraseAction != null) eraseAction.Erased.AddRange(done);
            RenderRegion(TargetRect(dirty, 4));
            RefreshToolbar();
        }

        // Adds a finished element (shape, fill, text) as one undo step, optionally replacing `replaced`.
        void AddElement(InkStroke s, InkStroke replaced, Rectangle alsoDirty)
        {
            var action = new UndoAction();
            RectangleF dirty = s.Bounds;
            if (replaced != null)
            {
                action.Erased.AddRange(Document.Erase(new[] { replaced.Id }, author));
                dirty = RectangleF.Union(dirty, replaced.Bounds);
            }
            Document.Add(s);
            action.Added.Add(s.Id);
            undo.Push(action);
            redo.Clear();
            Rectangle r = TargetRect(dirty, 4);
            if (!alsoDirty.IsEmpty) r = Rectangle.Union(r, alsoDirty);
            RenderRegion(r);
            RefreshToolbar();
        }

        // ------------------------------------------------------------------ shapes

        InkPoint Constrain(PointF c)
        {
            if ((ModifierKeys & Keys.Shift) == 0) return new InkPoint(c.X, c.Y, 128);
            float dx = c.X - shapeStart.X, dy = c.Y - shapeStart.Y;
            if (lastShape == InkTool.Line || lastShape == InkTool.Arrow)
            {
                // Horizontal, vertical or diagonal.
                double a = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                return new InkPoint(shapeStart.X + (float)Math.Cos(a) * len, shapeStart.Y + (float)Math.Sin(a) * len, 128);
            }
            float m = Math.Max(Math.Abs(dx), Math.Abs(dy));   // square / circle
            return new InkPoint(shapeStart.X + (dx < 0 ? -m : m), shapeStart.Y + (dy < 0 ? -m : m), 128);
        }

        Rectangle ShapeTargetRect(float width)
        {
            var r = RectangleF.FromLTRB(Math.Min(shapeStart.X, shapeEnd.X), Math.Min(shapeStart.Y, shapeEnd.Y),
                                        Math.Max(shapeStart.X, shapeEnd.X), Math.Max(shapeStart.Y, shapeEnd.Y));
            float pad = (lastShape == InkTool.Arrow ? InkRenderer.ArrowHead(width) : width / 2) + 2;
            return TargetRect(RectangleF.Inflate(r, pad, pad), 3);
        }

        bool ShapeFilled { get { return shapeFilled && (lastShape == InkTool.Rectangle || lastShape == InkTool.Ellipse); } }

        void DrawShapePreview()
        {
            float w = penSize * unit;
            Rectangle now = ShapeTargetRect(w);
            Rectangle dirty = liveDirty.IsEmpty ? now : Rectangle.Union(liveDirty, now);
            using (var g = frame.CreateGraphics())
            {
                CopyRect(baseLayer, g, dirty);
                g.SetClip(now);
                InkRenderer.DrawShape(g, lastShape, color, w, shapeStart, shapeEnd, ShapeFilled, map);
            }
            liveDirty = now;
            frame.Present(Handle, Monitor.Location, dirty);
        }

        void CommitShape()
        {
            Rectangle preview = liveDirty;
            liveDirty = Rectangle.Empty;
            if (Math.Abs(shapeEnd.X - shapeStart.X) < 2 && Math.Abs(shapeEnd.Y - shapeStart.Y) < 2)
            {
                RenderRegion(preview);   // a click, not a shape
                return;
            }
            AddElement(InkStroke.Shape(NewId(), author, DateTime.UtcNow.Ticks, lastShape, color, penSize * unit, shapeStart, shapeEnd, ShapeFilled),
                       null, preview);
        }

        // ------------------------------------------------------------------ fill and eyedropper

        // Fills the area around the point that is enclosed by lines, shapes or text; on a line, shape or text: recolors it.
        void FillAt(Point client)
        {
            PointF c = map.ToCanvas(client.X, client.Y);
            int w = Document.CanvasWidth, h = Document.CanvasHeight, sx = (int)Math.Floor(c.X), sy = (int)Math.Floor(c.Y);
            if (sx < 0 || sy < 0 || sx >= w || sy >= h) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                // Earlier fills don't block a new one (so an outline drawn on a filled area can still be filled).
                var blockers = Rendered.Where(s => s.Tool != InkTool.Fill).ToList();
                var ink = new bool[w * h];
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        InkRenderer.Prepare(g);
                        InkRenderer.DrawStrokes(g, blockers, new InkMapping { Scale = 1 });
                    }
                    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        var row = new int[w];
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                            for (int x = 0; x < w; x++) ink[y * w + x] = ((row[x] >> 24) & 0xFF) >= 64;
                        }
                    }
                    finally { bmp.UnlockBits(data); }
                }
                if (ink[sy * w + sx])
                {
                    InkStroke hit = null;
                    for (int i = blockers.Count - 1; i >= 0 && hit == null; i--) if (blockers[i].HitTest(c.X, c.Y, 1)) hit = blockers[i];
                    if (hit != null && (hit.Argb | unchecked((int)0xFF000000)) != color)
                        AddElement(hit.Recolored(NewId(), author, DateTime.UtcNow.Ticks, color), hit, Rectangle.Empty);
                    return;
                }
                var mask = InkFill.Flood(ink, w, h, sx, sy);
                if (mask != null) AddElement(InkStroke.FillRegion(NewId(), author, DateTime.UtcNow.Ticks, color, mask), null, Rectangle.Empty);
            }
            finally { UpdateCursor(); }
        }

        void PickColor(Point client)
        {
            int x = Math.Max(0, Math.Min(Monitor.Width - 1, client.X)), y = Math.Max(0, Math.Min(Monitor.Height - 1, client.Y));
            Color c = baseLayer.GetPixel(x, y);
            SetColor(Color.FromArgb(255, c).ToArgb());
            AddRecent(color);
            SetTool(toolBeforePicker == EditorTool.Picker ? EditorTool.Pen : toolBeforePicker);
        }

        static void AddRecent(int argb)
        {
            if (InkRenderer.Palette.Any(p => p.ToArgb() == argb)) return;
            recentColors.Remove(argb);
            recentColors.Insert(0, argb);
            if (recentColors.Count > 9) recentColors.RemoveAt(recentColors.Count - 1);
        }

        // ------------------------------------------------------------------ text

        void PlaceText(Point client)
        {
            PointF c = map.ToCanvas(client.X, client.Y);
            if (textPanel != null && textPanel.Visible)
            {
                // Clicking the drawing while writing moves the text there.
                textAt = c;
                UpdateTextPreview();
                textPanel.Activate();
                textPanel.FocusText();
                return;
            }
            InkStroke hit = Rendered.LastOrDefault(s => s.Tool == InkTool.Text && s.Bounds.Contains(c));
            textEditing = hit;
            if (hit != null)
            {
                textAt = new PointF(hit.Points[0].X, hit.Points[0].Y);
                color = hit.Argb | unchecked((int)0xFF000000);
                textSize = hit.Width / unit;
                textFont = hit.Font ?? textFont;
                textBold = hit.Bold;
                textItalic = hit.Italic;
                textEffect = hit.Effect ?? InkText.Plain;
                hiddenId = hit.Id;
                RenderRegion(TargetRect(hit.Bounds, 4));
            }
            else textAt = c;

            if (textPanel == null)
            {
                textPanel = new TextPanel(dpiScale, TextMin, TextMax);
                textPanel.Changed += OnTextPanelChanged;
                textPanel.Commit += CommitText;
                textPanel.Cancel += CancelText;
                textPanel.ColorRequested += () => ShowColorPicker(textPanel.Bounds, false);
            }
            textPanel.Reset(hit != null ? hit.Text : "", textFont, textSize, textBold, textItalic, textEffect, color, textTab);
            PointF at = map.ToTarget(textAt.X, textAt.Y);
            var near = new Rectangle(Monitor.Left + (int)at.X, Monitor.Top + (int)at.Y, (int)(textSize * unit * map.Scale * 4), (int)(textSize * unit * map.Scale * 1.4f));
            textPanel.PlaceNear(near, Monitor);
            textPreview = Rectangle.Empty;
            textPanel.Show(this);
            textPanel.Activate();
            UpdateTextPreview();
            RefreshToolbar();
        }

        bool Writing { get { return textPanel != null && textPanel.Visible; } }

        void OnTextPanelChanged()
        {
            textFont = textPanel.FontName;
            textBold = textPanel.IsBold;
            textItalic = textPanel.IsItalic;
            textEffect = textPanel.Effect;
            textSize = textPanel.SizeValue;
            UpdateTextPreview();
            RefreshToolbar();
        }

        // The text as it will look, with a small marker where it starts.
        void UpdateTextPreview()
        {
            if (!Writing) return;
            PointF p = map.ToTarget(textAt.X, textAt.Y);
            float px = textSize * unit * map.Scale;
            InkText.Raster r = string.IsNullOrEmpty(textPanel.Value) ? null
                : InkText.Render(textPanel.Value, textFont, px, textBold, textItalic, color, textEffect);
            var marker = new Rectangle((int)p.X - 2, (int)p.Y, 4, (int)Math.Max(8, px * 1.25f));
            Rectangle now = marker;
            Point at = Point.Empty;
            if (r != null)
            {
                at = new Point((int)Math.Round(p.X + r.Offset.X), (int)Math.Round(p.Y + r.Offset.Y));
                now = Rectangle.Union(now, new Rectangle(at, r.Image.Size));
            }
            Rectangle dirty = Rectangle.Intersect(textPreview.IsEmpty ? now : Rectangle.Union(textPreview, now), ClientArea);
            using (var g = frame.CreateGraphics())
            {
                CopyRect(baseLayer, g, dirty);
                if (r != null) g.DrawImageUnscaled(r.Image, at);
                using (var b = new SolidBrush(Color.FromArgb(200, 26, 115, 232))) g.FillRectangle(b, marker);
            }
            if (r != null) r.Image.Dispose();
            textPreview = now;
            frame.Present(Handle, Monitor.Location, dirty);
        }

        void CommitText()
        {
            if (!Writing) return;
            string text = textPanel.Value.Replace("\r\n", "\n").TrimEnd('\n', ' ');
            if (textPanel.Tab >= 0) textTab = textPanel.Tab;
            textPanel.Hide();
            var old = textEditing;
            Rectangle preview = textPreview;
            textEditing = null;
            hiddenId = null;
            textPreview = Rectangle.Empty;
            if (text.Trim().Length == 0)
            {
                if (old != null)
                {
                    // Emptied: remove it.
                    var action = new UndoAction();
                    action.Erased.AddRange(Document.Erase(new[] { old.Id }, author));
                    undo.Push(action);
                    redo.Clear();
                    preview = Rectangle.Union(preview, TargetRect(old.Bounds, 4));
                }
                RenderRegion(preview);
            }
            else AddElement(InkStroke.TextItem(NewId(), author, DateTime.UtcNow.Ticks, color, textSize * unit, textAt, text, textFont, textBold,
                                               textItalic, textEffect, old != null ? old.Id : null), old, preview);
            if (!closing) Activate();
            RefreshToolbar();
        }

        void CancelText()
        {
            if (!Writing) return;
            if (textPanel.Tab >= 0) textTab = textPanel.Tab;
            textPanel.Hide();
            Rectangle r = textPreview;
            if (textEditing != null) r = Rectangle.Union(r, TargetRect(textEditing.Bounds, 4));
            textEditing = null;
            hiddenId = null;
            textPreview = Rectangle.Empty;
            RenderRegion(r);
            Activate();
        }

        // ------------------------------------------------------------------ colors and sizes

        public void SetColor(int argb) { SetColor(argb, true); }

        void SetColor(int argb, bool syncPicker)
        {
            color = argb | unchecked((int)0xFF000000);
            lastColor[ColorKey] = color;
            if (tool == EditorTool.Eraser) SetTool(EditorTool.Pen);
            if (Writing) { textPanel.SetColor(color); UpdateTextPreview(); }
            if (syncPicker && colorPicker != null && colorPicker.Visible && colorPicker.Current != color) colorPicker.SetColor(color);
            RefreshToolbar();
        }

        bool IsPaletteColor(int argb) { return InkRenderer.Palette.Any(p => p.ToArgb() == argb); }

        void ShowColorPicker(Rectangle near, bool below)
        {
            if (colorPicker == null)
            {
                colorPicker = new ColorPicker(dpiScale, recentColors);
                colorPicker.ColorChanged += a => SetColor(a, false);
                colorPicker.EyedropperRequested += () => { colorPicker.Hide(); UsePicker(); };
                colorPicker.VisibleChanged += (o, e) =>
                {
                    if (colorPicker.Visible || closing) return;
                    AddRecent(color);
                    if (Writing) textPanel.Activate(); else Activate();
                };
            }
            colorPicker.SetColor(color);
            if (below) colorPicker.PlaceBelow(near, Monitor); else colorPicker.PlaceNear(near, Monitor);
            colorPicker.Show(this);
            colorPicker.Activate();
        }

        Rectangle ToolbarItemScreenRect(InkToolbar.Item it)
        {
            return new Rectangle(toolbar.Left + it.Rect.Left, toolbar.Top + it.Rect.Top, it.Rect.Width, it.Rect.Height);
        }

        void UsePicker()
        {
            if (tool != EditorTool.Picker) toolBeforePicker = tool;
            SetTool(EditorTool.Picker);
        }

        float SizeMin { get { return tool == EditorTool.Highlighter ? HighlighterMin : tool == EditorTool.Eraser ? EraserMin : tool == EditorTool.Text ? TextMin : PenMin; } }
        float SizeMax { get { return tool == EditorTool.Highlighter ? HighlighterMax : tool == EditorTool.Eraser ? EraserMax : tool == EditorTool.Text ? TextMax : PenMax; } }

        float CurrentSize
        {
            get { return tool == EditorTool.Highlighter ? highlighterSize : tool == EditorTool.Eraser ? eraserSize : tool == EditorTool.Text ? textSize : penSize; }
            set
            {
                value = Math.Max(SizeMin, Math.Min(SizeMax, value));
                if (tool == EditorTool.Highlighter) highlighterSize = value;
                else if (tool == EditorTool.Eraser) eraserSize = value;
                else if (tool == EditorTool.Text) textSize = value;
                else penSize = value;
                if (tool == EditorTool.Eraser) UpdateCursor();
                if (tool == EditorTool.Text && Writing) { textPanel.SetSize(textSize); UpdateTextPreview(); }
                RefreshToolbar();
            }
        }

        bool SizeApplies { get { return tool != EditorTool.Fill && tool != EditorTool.Picker; } }

        // Slider position 0..1 (logarithmic: fine control of small sizes).
        float SliderValue { get { return (float)(Math.Log(CurrentSize / SizeMin) / Math.Log(SizeMax / SizeMin)); } }
        void SetSliderValue(float t) { CurrentSize = (float)(SizeMin * Math.Pow(SizeMax / SizeMin, t)); }

        float SizePreview
        {
            get
            {
                switch (tool)
                {
                    case EditorTool.Eraser: return eraserSize * dpiScale * 2;
                    case EditorTool.Highlighter: return highlighterSize * unit * map.Scale;
                    case EditorTool.Text: return textSize * unit * map.Scale * 0.5f;
                    default: return penSize * unit * map.Scale;
                }
            }
        }

        // ------------------------------------------------------------------ commands

        public void Undo()
        {
            if (active || Writing || undo.Count == 0) return;
            var a = undo.Pop();
            Document.Erase(a.Added, author);
            Document.Restore(a.Erased, author);
            redo.Push(a);
            RenderAll(true);
            RefreshToolbar();
        }

        public void Redo()
        {
            if (active || Writing || redo.Count == 0) return;
            var a = redo.Pop();
            Document.Restore(a.Added, author);
            Document.Erase(a.Erased, author);
            undo.Push(a);
            RenderAll(true);
            RefreshToolbar();
        }

        public void ClearAll()
        {
            if (active) End();
            if (Writing) CancelText();
            var ids = VisibleStrokes.Select(s => s.Id).ToList();
            if (ids.Count == 0) return;
            var a = new UndoAction();
            a.Erased.AddRange(Document.Erase(ids, author));
            undo.Push(a);
            redo.Clear();
            RenderAll(true);
            RefreshToolbar();
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
                color = lastColor.TryGetValue(ColorKey, out c) ? c : InkRenderer.Palette[InkRenderer.DefaultColor(next)].ToArgb();
            }
            RenderBackground();
            RenderAll(true);
            RefreshToolbar();
        }

        public void SetTool(EditorTool t)
        {
            if (Writing && t != EditorTool.Text) CommitText();
            tool = t;
            if (t != EditorTool.Picker) lastTool = t;
            UpdateCursor();
            RefreshToolbar();
        }

        void SetShape(InkTool shape)
        {
            lastShape = shape;
            SetTool(EditorTool.Shape);
            if (toolbar != null) toolbar.CloseFlyout();
        }

        public void RequestSwitchBoard() { SwitchBoardRequested = true; Finish(); }

        public void Finish()
        {
            if (closing) return;
            if (active) End();
            if (Writing) CommitText();
            Close();
        }

        // Saves what is on screen (board or wallpaper with the drawing) as a PNG in Pictures\LiveWall Boards. No file
        // dialog: it would load the shell into this always-running process for good.
        void SaveImage()
        {
            if (active) End();
            if (Writing) CommitText();
            try
            {
                Directory.CreateDirectory(Boards.ExportDir);
                string path = Path.Combine(Boards.ExportDir, exportName + " " + DateTime.Now.ToString("HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                baseLayer.Save(path, ImageFormat.Png);
                Log.Info("Saved drawing as " + path);
                if (toolbar != null) toolbar.ShowMessage(saveItem, "Saved: Pictures\\LiveWall Boards\\" + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not save the picture: " + ex.Message);
                if (toolbar != null) toolbar.ShowMessage(saveItem, "Could not save the picture: " + ex.Message);
            }
        }

        void UpdateCursor()
        {
            if (tool == EditorTool.Text) { Cursor = Cursors.IBeam; return; }
            if (tool != EditorTool.Eraser) { Cursor = Cursors.Cross; return; }
            int d = Math.Max(8, Math.Min(250, (int)(eraserSize * dpiScale * 2)));
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
            InkToolbar.Item eraser = null;
            eraser = InkToolbar.Item.Button("\uE75C", "E", "Eraser (E, or the pen's eraser end / right mouse button). Click again: erase only what it touches, or whole strokes.",
                () => { SetTool(EditorTool.Eraser); if (toolbar.FlyoutOpen) toolbar.CloseFlyout(); else toolbar.ShowFlyout(eraser, BuildEraserFlyout()); },
                () => tool == EditorTool.Eraser);
            eraser.HasFlyout = true;
            eraserItem = eraser;
            items.Add(eraser);
            InkToolbar.Item shapes = null;
            shapes = InkToolbar.Item.Custom((g, r, fg) => InkToolbar.DrawShapeIcon(g, r, lastShape, ShapeFilled, fg),
                "Shapes: line (L), arrow (A), rectangle (R), ellipse (O). Hold Shift for straight lines, squares and circles.",
                () => { SetTool(EditorTool.Shape); if (toolbar.FlyoutOpen) toolbar.CloseFlyout(); else toolbar.ShowFlyout(shapes, BuildShapeFlyout()); },
                () => tool == EditorTool.Shape);
            shapes.HasFlyout = true;
            items.Add(shapes);
            items.Add(InkToolbar.Item.Button("\uEB42", "F", "Fill: click inside a closed area to fill it, or on a line or shape to recolor it (F)", () => SetTool(EditorTool.Fill), () => tool == EditorTool.Fill));
            items.Add(InkToolbar.Item.Button("\uE8D2", "T", "Text, emoji, kaomoji and symbols: click where they go; click text to change it (T)",
                () => SetTool(EditorTool.Text), () => tool == EditorTool.Text));
            items.Add(InkToolbar.Item.Button("\uEF3C", "I", "Eyedropper: pick a color from the drawing (I)", UsePicker, () => tool == EditorTool.Picker));
            items.Add(InkToolbar.Item.Separator());
            for (int i = 0; i < InkRenderer.Palette.Length; i++)
            {
                int argb = InkRenderer.Palette[i].ToArgb();
                items.Add(InkToolbar.Item.ColorSwatch(InkRenderer.Palette[i], "Color " + (i + 1) + " (" + (i + 1) + ")", () => SetColor(argb), () => color == argb));
            }
            colorItem = InkToolbar.Item.Custom((g, r, fg) => InkToolbar.DrawColorWheelIcon(g, r, Color.FromArgb(color)),
                "Any color: color square, RGB, HSV, hex, recent colors", () => ShowColorPicker(ToolbarItemScreenRect(colorItem), true), () => !IsPaletteColor(color));
            items.Add(colorItem);
            items.Add(InkToolbar.Item.Separator());
            items.Add(InkToolbar.Item.Slider("Size ([ and ], or the mouse wheel here)", () => SliderValue, SetSliderValue, () => SizePreview, () => SizeApplies));
            items.Add(InkToolbar.Item.Separator());
            items.Add(InkToolbar.Item.Button("\uE7A7", "\u21B6", "Undo (Ctrl+Z)", Undo, null, () => undo.Count > 0));
            items.Add(InkToolbar.Item.Button("\uE7A6", "\u21B7", "Redo (Ctrl+Y)", Redo, null, () => redo.Count > 0));
            items.Add(InkToolbar.Item.Button("\uE74D", "X", "Clear everything (Delete) - can be undone", ClearAll, null, () => VisibleStrokes.Count > 0));
            if (IsBoard)
            {
                items.Add(InkToolbar.Item.Separator());
                items.Add(InkToolbar.Item.Button("\uE790", "B", "Board background: whiteboard, blackboard, grid, dots (B)", NextBackground, null));
                items.Add(InkToolbar.Item.Segment(new[] { dailyLabel, "Permanent" }, "Switch between the daily board and the permanent board (Tab)",
                    () => dailyBoard ? 0 : 1, i => { if (i != (dailyBoard ? 0 : 1)) RequestSwitchBoard(); }));
            }
            items.Add(InkToolbar.Item.Separator());
            saveItem = InkToolbar.Item.Button("\uE74E", "S", "Save a copy as a picture in Pictures\\LiveWall Boards (Ctrl+S). Boards are also saved there automatically.",
                SaveImage, null);
            items.Add(saveItem);
            items.Add(InkToolbar.Item.Accent("\uE73E", "OK", "Done (Esc)", Finish));
            return items;
        }

        List<InkToolbar.Item> BuildEraserFlyout()
        {
            return new List<InkToolbar.Item>
            {
                InkToolbar.Item.Segment(new[] { "Erase parts", "Whole strokes" },
                    "Erase only what the eraser touches, or every stroke, shape or text it touches (E switches while erasing)",
                    () => eraseWhole ? 1 : 0, i => { eraseWhole = i == 1; SetTool(EditorTool.Eraser); toolbar.CloseFlyout(); })
            };
        }

        void ToggleEraserMode()
        {
            if (active) return;
            eraseWhole = !eraseWhole;
            RefreshToolbar();
            if (toolbar != null && eraserItem != null)
                toolbar.ShowMessage(eraserItem, eraseWhole ? "Eraser: whole strokes" : "Eraser: only what it touches");
        }

        List<InkToolbar.Item> BuildShapeFlyout()
        {
            var items = new List<InkToolbar.Item>();
            var shapes = new[] { InkTool.Line, InkTool.Arrow, InkTool.Rectangle, InkTool.Ellipse };
            var names = new[] { "Line (L)", "Arrow (A)", "Rectangle (R)", "Ellipse (O)" };
            for (int i = 0; i < shapes.Length; i++)
            {
                InkTool sh = shapes[i];
                items.Add(InkToolbar.Item.Custom((g, r, fg) => InkToolbar.DrawShapeIcon(g, r, sh, shapeFilled, fg), names[i] + ". Hold Shift for straight lines, squares and circles.",
                    () => SetShape(sh), () => tool == EditorTool.Shape && lastShape == sh));
            }
            items.Add(InkToolbar.Item.Separator());
            items.Add(InkToolbar.Item.Custom((g, r, fg) =>
            {
                var rr = RectangleF.Inflate(r, -1, -r.Height * 0.12f);
                using (var b = new SolidBrush(fg)) g.FillPolygon(b, new[] { new PointF(rr.Left, rr.Bottom), new PointF(rr.Right, rr.Top), new PointF(rr.Right, rr.Bottom) });
                using (var p = new Pen(fg, Math.Max(1.5f, r.Width / 10))) g.DrawRectangle(p, rr.X, rr.Y, rr.Width, rr.Height);
            }, "Filled rectangles and ellipses (on / off)", () => { shapeFilled = !shapeFilled; RefreshToolbar(); }, () => shapeFilled));
            return items;
        }

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
            if (active) { Extend(e.Location, 128); End(); }   // the release point: its last move can arrive with the release
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
                case Keys.Control | Keys.S: SaveImage(); return true;
                case Keys.P: SetTool(EditorTool.Pen); return true;
                case Keys.H: SetTool(EditorTool.Highlighter); return true;
                case Keys.E: if (tool == EditorTool.Eraser) ToggleEraserMode(); else SetTool(EditorTool.Eraser); return true;
                case Keys.S: SetTool(EditorTool.Shape); return true;
                case Keys.L: SetShape(InkTool.Line); return true;
                case Keys.A: SetShape(InkTool.Arrow); return true;
                case Keys.R: SetShape(InkTool.Rectangle); return true;
                case Keys.O: SetShape(InkTool.Ellipse); return true;
                case Keys.F: SetTool(EditorTool.Fill); return true;
                case Keys.T: SetTool(EditorTool.Text); return true;
                case Keys.I: UsePicker(); return true;
                case Keys.OemOpenBrackets: CurrentSize = CurrentSize * 0.8f; return true;
                case Keys.OemCloseBrackets: CurrentSize = CurrentSize * 1.25f; return true;
                case Keys.Delete: ClearAll(); return true;
                case Keys.B: NextBackground(); return true;
                case Keys.Tab: if (IsBoard) RequestSwitchBoard(); return true;
            }
            Keys k = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.None && k >= Keys.D1 && k <= Keys.D9) { SetColor(InkRenderer.Palette[k - Keys.D1].ToArgb()); return true; }
            if ((keyData & Keys.Modifiers) == Keys.None && k >= Keys.NumPad1 && k <= Keys.NumPad9) { SetColor(InkRenderer.Palette[k - Keys.NumPad1].ToArgb()); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (active) End();
            if (Writing) CommitText();
            closing = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (toolbar != null && !toolbar.IsDisposed) toolbar.Close();
            if (colorPicker != null && !colorPicker.IsDisposed) colorPicker.Close();
            if (textPanel != null && !textPanel.IsDisposed) textPanel.Close();
            if (frame != null) { frame.Dispose(); frame = null; }
            if (baseLayer != null) { baseLayer.Dispose(); baseLayer = null; }
            if (background != null) { background.Dispose(); background = null; }
            if (ink != null) { ink.Dispose(); ink = null; }
            if (picture != null) picture.Dispose();
            if (eraserCursor != null) { Cursor = Cursors.Default; eraserCursor.Dispose(); eraserCursor = null; }
            if (eraserCursorIcon != IntPtr.Zero) { DestroyIcon(eraserCursorIcon); eraserCursorIcon = IntPtr.Zero; }
            InkText.ClearCache();
            var h = Finished;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
