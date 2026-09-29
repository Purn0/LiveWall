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
    internal enum EditorTool { Pen, Highlighter, Eraser, Shape, Fill, Text, Picker, Select }

    // Full-screen drawing surface for one screen: a per-pixel-alpha window (see LayeredBitmap) that shows the board or the
    // wallpaper underneath, plus a floating toolbar. Pen (with pressure and the eraser end), touch and mouse; shapes,
    // paint-bucket fills, text with emoji, an eyedropper and any color. Every change is written to the drawing's file
    // immediately.
    internal sealed partial class InkEditor : Form
    {
        sealed class UndoAction
        {
            public readonly List<string> Added = new List<string>();
            public readonly List<string> Erased = new List<string>();
            public readonly List<string[]> Layers = new List<string[]>();   // layer id, what, old value, new value
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
        static int fillGap = 1;         // FillGaps index
        static string penBrush = InkBrush.Pen;
        static int penSpread = 100;     // percent: spray scatter, airbrush softness, neon glow width
        static int glowDim = InkGlow.DefaultDim;
        static bool fillAllLayers;      // fill boundaries from every shown layer, not just the active one
        static bool glowOn;             // new pen strokes, shapes and text glow (G)
        static int glowStrength = 60, glowSpeed = 2;
        static string glowAnim = InkGlow.Steady;
        static readonly Dictionary<string, string> lastLayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // board file -> layer

        const float PenMin = 1, PenMax = 200, HighlighterMin = 6, HighlighterMax = 120, EraserMin = 5, EraserMax = 150, TextMin = 10, TextMax = 300;

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
        Bitmap ink;                      // the active layer's elements alone (transparent elsewhere)
        Bitmap below, above;             // shown layers below the active one (over the background) and above it; null when none
        string activeLayer;              // what drawing, erasing, selecting and filling act on
        int? previewOpacity;             // the active layer's opacity while the panel's slider is dragged
        LayersPanel layersPanel;
        InkMapping map;
        float unit, dpiScale = 1;
        InkToolbar toolbar;
        InkToolbar.Item colorItem, saveItem, eraserItem, selectItem, penItem, layerItem, glowItem;
        ColorPicker colorPicker;
        TextPanel textPanel;

        EditorTool tool, toolBeforePicker = EditorTool.Pen;
        int color;

        // Current contact.
        bool active, erasing, shaping, liveHasPressure;
        readonly List<InkPoint> live = new List<InkPoint>();
        InkTool liveTool;
        string liveId, liveBrush;        // the stroke's id from the start: brushes seed their randomness with it
        int liveSpread;
        string shapeId;                  // the shape being drawn (its brush's randomness is seeded from it)
        uint liveSeed;
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
            activeLayer = PickLayer();
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

        List<InkStroke> activeCache;
        int activeRevision = -1;
        string activeCacheLayer;

        // The active layer's elements: what drawing, erasing, selecting and filling act on.
        List<InkStroke> ActiveStrokes
        {
            get
            {
                if (activeCache == null || activeRevision != Document.Revision || activeCacheLayer != activeLayer)
                {
                    activeCache = VisibleStrokes.Where(s => InkDocument.LayerOf(s) == activeLayer).ToList();
                    activeRevision = Document.Revision;
                    activeCacheLayer = activeLayer;
                }
                return activeCache;
            }
        }

        // What goes on `ink`: the active layer except text being edited.
        IEnumerable<InkStroke> Rendered { get { return hiddenId == null ? ActiveStrokes : ActiveStrokes.Where(s => s.Id != hiddenId); } }

        // The active layer's elements go onto `ink` (transparent), which is then laid over the background and the layers
        // below: the eraser clears only that layer.
        void RenderAll(bool present)
        {
            RenderOtherLayers();
            using (var g = Graphics.FromImage(ink))
            {
                g.Clear(Color.Transparent);
                InkRenderer.Prepare(g);
                InkRenderer.DrawStrokes(g, Rendered, map);
                ClearFloatingSource(g);
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
                ClearFloatingSource(g);
            }
            Compose(r, true);
        }

        void ClearFloatingSource(Graphics inkGraphics)
        {
            if (floating != null && floating.Source != null)
                InkRenderer.DrawEraseArea(inkGraphics, floating.Source.Select(q => map.ToTarget(q.X, q.Y)).ToArray());
        }

        // baseLayer = background, the layers below and the active layer (at its opacity) in `r`; on the screen, the layers
        // above go on top. Live strokes are drawn over baseLayer, then the layers above again.
        void Compose(Rectangle r, bool present)
        {
            r = Rectangle.Intersect(r, ClientArea);
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var g = Graphics.FromImage(baseLayer))
            {
                CopyRect(below ?? background, g, r);
                InkRenderer.DrawWithOpacity(g, ink, r, ActiveOpacity);
            }
            using (var g = Graphics.FromImage(frame.Bitmap))
            {
                CopyRect(baseLayer, g, r);
                DrawAbove(g, r);
            }
            if (present) frame.Present(Handle, Monitor.Location, r == ClientArea ? (Rectangle?)null : r);
            if (present && Selecting && r.IntersectsWith(overlay)) DrawSelection();   // keep the selection on top
        }

        void DrawAbove(Graphics g, Rectangle r) { if (above != null) g.DrawImage(above, r, r, GraphicsUnit.Pixel); }

        int ActiveOpacity
        {
            get
            {
                if (previewOpacity != null) return previewOpacity.Value;
                var l = Document.Layer(activeLayer);
                return l == null || !l.Visible ? 0 : l.Opacity;
            }
        }

        // Flattens the shown layers below the active one (over the background) into `below` and those above it into
        // `above`, once per change of layers: pen input only ever touches `ink`. Nothing extra with a single layer.
        void RenderOtherLayers()
        {
            var layers = Document.Layers;
            int at = layers.FindIndex(l => l.Id == activeLayer);
            var lower = layers.Take(Math.Max(0, at)).Where(l => l.Visible && l.Opacity > 0).ToList();
            var upper = at < 0 ? new List<InkLayerInfo>() : layers.Skip(at + 1).Where(l => l.Visible && l.Opacity > 0).ToList();
            if (lower.Count == 0) Drop(ref below);
            if (upper.Count == 0) Drop(ref above);
            if (lower.Count == 0 && upper.Count == 0) return;
            var all = VisibleStrokes;
            using (var scratch = new Bitmap(Monitor.Width, Monitor.Height, PixelFormat.Format32bppPArgb))
            {
                if (lower.Count > 0)
                {
                    if (below == null) below = new Bitmap(Monitor.Width, Monitor.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(below))
                    {
                        CopyRect(background, g, ClientArea);
                        LayersOnto(g, lower, all, scratch);
                    }
                }
                if (upper.Count > 0)
                {
                    if (above == null) above = new Bitmap(Monitor.Width, Monitor.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(above))
                    {
                        g.Clear(Color.Transparent);
                        LayersOnto(g, upper, all, scratch);
                    }
                }
            }
        }

        void LayersOnto(Graphics g, List<InkLayerInfo> layers, List<InkStroke> all, Bitmap scratch)
        {
            foreach (var l in layers)
            {
                string id = l.Id;
                using (var gs = Graphics.FromImage(scratch))
                {
                    gs.Clear(Color.Transparent);
                    InkRenderer.Prepare(gs);
                    InkRenderer.DrawStrokes(gs, all.Where(s => InkDocument.LayerOf(s) == id), map);
                }
                InkRenderer.DrawWithOpacity(g, scratch, ClientArea, l.Opacity);
            }
        }

        static void Drop(ref Bitmap b) { if (b != null) { b.Dispose(); b = null; } }

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

        void RefreshToolbar()
        {
            if (toolbar != null && !toolbar.IsDisposed) toolbar.RefreshAll();
            UpdateAnimation();
        }

        // ------------------------------------------------------------------ animation preview

        // While the drawing has animated glow it plays here too: up to 20 frames a second (fewer when a frame takes long,
        // so drawing it stays under ~40% of this thread), only the glowing areas, the same moments as the wallpaper's
        // loop. It waits while the pen is down, text is written or a selection floats.
        Timer animTimer;
        System.Diagnostics.Stopwatch animClock;
        int animRevision = -1;
        List<MovingArea> animAreas;

        void UpdateAnimation()
        {
            if (closing || frame == null || Document.Revision == animRevision) return;
            animRevision = Document.Revision;
            DropAnimAreas();
            animAreas = InkGlow.MovingParts(Document.Snapshot(), map, Monitor.Width, Monitor.Height)
                .Select(p => new MovingArea(Rectangle.Intersect(p.Key, ClientArea), p.Value)).Where(a => a.Area.Width > 0 && a.Area.Height > 0).ToList();
            if (animAreas.Count > 0 && animTimer == null)
            {
                animTimer = new Timer { Interval = 50 };
                animTimer.Tick += (o, e) => AnimTick();
                animClock = System.Diagnostics.Stopwatch.StartNew();
                animTimer.Start();
            }
            else if (animAreas.Count == 0) StopAnimation();
        }

        void StopAnimation()
        {
            DropAnimAreas();
            if (animTimer == null) return;
            animTimer.Stop();
            animTimer.Dispose();
            animTimer = null;
        }

        void DropAnimAreas()
        {
            if (animAreas == null) return;
            foreach (var a in animAreas) a.Dispose();
            animAreas = null;
        }

        void AnimTick()
        {
            if (closing || frame == null) return;
            UpdateAnimation();
            if (animTimer == null || animAreas == null || active || Selecting || Writing || previewOpacity != null) return;
            bool timed = InkGlow.Timed, linear = InkRenderer.Linear;
            var took = System.Diagnostics.Stopwatch.StartNew();
            InkGlow.Timed = true;
            InkGlow.Time = animClock.Elapsed.TotalSeconds % InkGlow.Loop;
            InkRenderer.Linear = true;
            Rectangle dirty = Rectangle.Empty;
            try
            {
                using (var g = Graphics.FromImage(frame.Bitmap))
                {
                    InkRenderer.Prepare(g);
                    foreach (var area in animAreas)
                    {
                        Rectangle r = area.Area;
                        g.SetClip(r);
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(background, r, r, GraphicsUnit.Pixel);
                        g.CompositingMode = CompositingMode.SourceOver;
                        area.Draw(g, map);
                        dirty = dirty.IsEmpty ? r : Rectangle.Union(dirty, r);
                    }
                }
            }
            finally
            {
                InkGlow.Timed = timed;
                InkRenderer.Linear = linear;
            }
            if (!dirty.IsEmpty) frame.Present(Handle, Monitor.Location, dirty);
            if (animTimer != null) animTimer.Interval = Math.Max(50, Math.Min(250, (int)(took.ElapsedMilliseconds * 2.5)));
        }

        static string NewId() { return Guid.NewGuid().ToString("N"); }

        // New elements go on the active layer.
        void AddToDoc(InkStroke s) { Document.Add(s.InLayer(activeLayer)); }

        // ------------------------------------------------------------------ freehand strokes

        void Begin(Point client, byte pressure, bool eraserTip)
        {
            if (closing) return;
            if (toolbar != null) toolbar.CloseFlyout();
            if (active) End();
            if (tool != EditorTool.Picker && !ActiveEditable()) return;
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
                case EditorTool.Select: SelectBegin(client); return;
                case EditorTool.Fill: FillAt(client); return;
                case EditorTool.Text: PlaceText(client); return;
                case EditorTool.Shape:
                {
                    PointF c0 = map.ToCanvas(client.X, client.Y);
                    active = true;
                    shaping = true;
                    erasing = false;
                    shapeStart = shapeEnd = new InkPoint(c0.X, c0.Y, 128);
                    shapeId = NewId();
                    liveDirty = Rectangle.Empty;
                    DrawShapePreview();
                    return;
                }
            }
            active = true;
            erasing = false;
            live.Clear();
            liveTool = tool == EditorTool.Highlighter ? InkTool.Highlighter : InkTool.Pen;
            liveId = NewId();
            liveBrush = liveTool == InkTool.Pen && InkBrush.IsBrush(penBrush) ? penBrush : null;
            liveSpread = penSpread;
            liveSeed = InkBrush.Seed(liveId);
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
            if (drag != DragKind.None) { SelectExtend(client); return; }
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
                if (liveBrush != null)
                {
                    // Redraw the area of the new segment from the finished drawing: the brush's own pixels, already final.
                    InkPoint a = live.Count > 1 ? live[live.Count - 2] : live[0], b = live[live.Count - 1];
                    float reach = liveWidth * 1.7f * InkBrush.Extent(liveBrush, liveSpread / 100f) / 2 + 1;
                    var seg = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    dirty = TargetRect(RectangleF.Inflate(seg, reach, reach), 3);
                    CopyRect(baseLayer, g, dirty);
                    g.SetClip(dirty);
                    var arr = live.ToArray();
                    InkBrush.Draw(g, liveBrush, liveArgb, liveWidth, arr, arr.Length, liveSeed, map, dirty, false, liveSpread / 100f);
                    g.ResetClip();
                    DrawAbove(g, dirty);
                }
                else if (liveTool == InkTool.Highlighter)
                {
                    // Translucent: redraw the whole stroke over the untouched layer, or overlaps would darken.
                    RectangleF b = PointBounds(live, liveWidth);
                    Rectangle now = TargetRect(b, 3);
                    dirty = liveDirty.IsEmpty ? now : Rectangle.Union(liveDirty, now);
                    CopyRect(baseLayer, g, dirty);
                    g.SetClip(dirty);
                    var arr = live.ToArray();
                    InkRenderer.DrawPoints(g, liveTool, liveArgb, liveWidth, arr, arr.Length, false, map);
                    g.ResetClip();
                    DrawAbove(g, dirty);
                    liveDirty = now;
                }
                else if (above != null)
                {
                    // Layers above: redraw the new segment's area from baseLayer, then those layers over it.
                    InkPoint a = live.Count > 1 ? live[live.Count - 2] : live[0], b = live[live.Count - 1];
                    var seg = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    dirty = TargetRect(RectangleF.Inflate(seg, liveWidth * 1.8f, liveWidth * 1.8f), 3);
                    CopyRect(baseLayer, g, dirty);
                    g.SetClip(dirty);
                    var arr = live.ToArray();
                    InkRenderer.DrawPointsIn(g, liveTool, liveArgb, liveWidth, arr, arr.Length, liveHasPressure, map, dirty);
                    g.ResetClip();
                    DrawAbove(g, dirty);
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
            if (drag != DragKind.None) { SelectEnd(); return; }
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

            var stroke = Glowing(InkStroke.Freehand(liveId, author, DateTime.UtcNow.Ticks, liveTool, liveArgb, liveWidth, live.ToArray(), liveBrush, liveSpread));
            live.Clear();
            AddToDoc(stroke);
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
            if (!ActiveStrokes.Any(v => v.Tool != InkTool.Erase && v.Bounds.IntersectsWith(b))) return;
            AddToDoc(s);
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
            foreach (var s in ActiveStrokes)
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
            AddToDoc(s);
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
            if (ShapeBrush != null) pad = pad * InkBrush.Extent(ShapeBrush, penSpread / 100f) + width;
            return TargetRect(RectangleF.Inflate(r, pad, pad), 3);
        }

        bool ShapeFilled { get { return shapeFilled && (lastShape == InkTool.Rectangle || lastShape == InkTool.Ellipse); } }

        // Shapes are drawn with the pen's brush (none for the round pen).
        string ShapeBrush { get { return InkBrush.IsBrush(penBrush) ? penBrush : null; } }

        void DrawShapePreview()
        {
            float w = penSize * unit;
            Rectangle now = ShapeTargetRect(w);
            Rectangle dirty = liveDirty.IsEmpty ? now : Rectangle.Union(liveDirty, now);
            using (var g = frame.CreateGraphics())
            {
                CopyRect(baseLayer, g, dirty);
                g.SetClip(now);
                InkRenderer.DrawShape(g, lastShape, color, w, shapeStart, shapeEnd, ShapeFilled, map, ShapeBrush, penSpread / 100f, InkBrush.Seed(shapeId));
                g.ResetClip();
                DrawAbove(g, dirty);
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
            AddElement(Glowing(InkStroke.Shape(shapeId ?? NewId(), author, DateTime.UtcNow.Ticks, lastShape, color, penSize * unit, shapeStart, shapeEnd,
                                               ShapeFilled, ShapeBrush, penSpread)), null, preview);
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
                // Solid ink only: earlier fills don't block a new one (so an outline drawn on a filled area can still be
                // filled) and highlighters don't either (the fill goes under them).
                var order = Rendered.ToList();
                Func<InkStroke, bool> blocks = s => s.Tool != InkTool.Fill && s.Tool != InkTool.Highlighter && (s.Brush == null || InkBrush.Blocks(s.Brush));
                var blockers = order.Where(blocks).ToList();
                var ink = new bool[w * h];
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    if (fillAllLayers && !Document.IsFlat)
                    {
                        // Every shown layer on its own (its erasers clear only it), all adding to the boundaries.
                        var all = VisibleStrokes;
                        foreach (var l in Document.Layers.Where(l => l.Visible))
                        {
                            string id = l.Id;
                            AddInkMask(ink, bmp, id == activeLayer ? blockers : all.Where(s => InkDocument.LayerOf(s) == id && blocks(s)));
                        }
                    }
                    else AddInkMask(ink, bmp, blockers);
                }
                if (ink[sy * w + sx])
                {
                    InkStroke hit = null;
                    for (int i = blockers.Count - 1; i >= 0 && hit == null; i--) if (blockers[i].HitTest(c.X, c.Y, 1)) hit = blockers[i];
                    if (hit != null && hit.Tool != InkTool.Image && (hit.Argb | unchecked((int)0xFF000000)) != color)
                        AddElement(hit.Recolored(NewId(), author, DateTime.UtcNow.Ticks, color), hit, Rectangle.Empty);
                    return;
                }
                var mask = InkFill.Flood(ink, w, h, sx, sy, FillGap);
                if (mask != null) AddElement(InkStroke.FillRegion(NewId(), author, DateTime.UtcNow.Ticks, color, mask, FillUnder(order, mask)), null, Rectangle.Empty);
            }
            finally { UpdateCursor(); }
        }

        // Where `strokes` (drawn solid at canvas scale on `bmp`) cover the canvas, `ink` becomes true.
        void AddInkMask(bool[] ink, Bitmap bmp, IEnumerable<InkStroke> strokes)
        {
            int w = bmp.Width, h = bmp.Height;
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                InkRenderer.Prepare(g);
                InkRenderer.DrawStrokes(g, strokes, new InkMapping { Scale = 1 }, true);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                    for (int x = 0; x < w; x++) if (((row[x] >> 24) & 0xFF) >= FillInkAlpha) ink[y * w + x] = true;
                }
            }
            finally { bmp.UnlockBits(data); }
        }

        // Faint anti-aliased edges don't block a fill: the gap closing takes care of thin lines.
        const int FillInkAlpha = 40;
        static readonly float[] FillGaps = { 1, 2, 5 };   // canvas pixels at 1080p: exact, small gaps (default), bigger gaps

        int FillGap { get { return Math.Max(1, (int)Math.Round(FillGaps[fillGap] * unit)); } }

        // A new fill goes above the fills and eraser marks it overlaps (a new color on an old fill wins; an old eraser mark
        // doesn't cut it) and below everything drawn after them there: highlighters keep their look on top, and lines stay
        // crisp over the fill's edge. Null = on top.
        static string FillUnder(List<InkStroke> order, InkFill.Mask mask)
        {
            var area = new RectangleF(mask.Left, mask.Top, mask.Width, mask.Height);
            int floor = -1;
            for (int i = 0; i < order.Count; i++)
                if ((order[i].Tool == InkTool.Fill || order[i].Tool == InkTool.Erase) && order[i].Bounds.IntersectsWith(area)) floor = i;
            for (int i = floor + 1; i < order.Count; i++)
                if (order[i].Tool != InkTool.Erase && order[i].Bounds.IntersectsWith(area)) return order[i].Id;
            return null;
        }

        void PickColor(Point client)
        {
            int x = Math.Max(0, Math.Min(Monitor.Width - 1, client.X)), y = Math.Max(0, Math.Min(Monitor.Height - 1, client.Y));
            Color c = frame.Bitmap.GetPixel(x, y);   // everything shown, all layers
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
                DrawAbove(g, dirty);
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
            else
            {
                var item = InkStroke.TextItem(NewId(), author, DateTime.UtcNow.Ticks, color, textSize * unit, textAt, text, textFont, textBold,
                                              textItalic, textEffect, old != null ? old.Id : null);
                // Edited text keeps its glow unless glow is on now.
                AddElement(glowOn || old == null ? Glowing(item) : item.WithGlow(old.Glow, old.Anim, old.Speed), old, preview);
            }
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
            if (colorPicker != null && colorPicker.Visible) { colorPicker.Hide(); return; }   // clicked again: close it
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
            if (Selecting) { CancelSelection(); return; }   // undoes the move / resize / rotation
            if (active || Writing || undo.Count == 0) return;
            var a = undo.Pop();
            Document.Erase(a.Added, author);
            Document.Restore(a.Erased, author);
            for (int i = a.Layers.Count - 1; i >= 0; i--) Document.SetLayer(a.Layers[i][0], a.Layers[i][1], a.Layers[i][2], author);
            redo.Push(a);
            if (a.Layers.Count > 0) KeepActiveLayer();
            RenderAll(true);
            RefreshToolbar();
        }

        public void Redo()
        {
            PutDown();
            if (active || Writing || redo.Count == 0) return;
            var a = redo.Pop();
            Document.Restore(a.Added, author);
            Document.Erase(a.Erased, author);
            foreach (var c in a.Layers) Document.SetLayer(c[0], c[1], c[3], author);
            undo.Push(a);
            if (a.Layers.Count > 0) KeepActiveLayer();
            RenderAll(true);
            RefreshToolbar();
        }

        // ------------------------------------------------------------------ layers (boards)

        // The layer to draw on when the editor opens: the one used last on this board, else the top one.
        string PickLayer()
        {
            var layers = Document.Layers;
            if (layers.Count == 0)
            {
                Document.SetLayer(InkDocument.BaseLayer, "deleted", "0", author);
                layers = Document.Layers;
            }
            string id;
            if (lastLayer.TryGetValue(Document.FilePath, out id) && layers.Any(l => l.Id == id)) return id;
            return layers[layers.Count - 1].Id;
        }

        string LayerTitle
        {
            get
            {
                var l = Document.Layer(activeLayer);
                if (l == null) return "";
                return l.Name + (!l.Visible ? " (hidden)" : l.Locked ? " (locked)" : "");
            }
        }

        public List<InkLayerInfo> LayersTopFirst { get { var l = Document.Layers; l.Reverse(); return l; } }
        public string ActiveLayerId { get { return activeLayer; } }

        // Drawing, erasing, filling, pasting... on a hidden or locked layer: a note instead.
        bool ActiveEditable()
        {
            var l = Document.Layer(activeLayer);
            if (l == null || (l.Visible && !l.Locked)) return true;
            if (toolbar != null && layerItem != null)
                toolbar.ShowMessage(layerItem, l.Locked ? "This layer is locked: unlock it in Layers, or pick another layer" : "This layer is hidden: show it in Layers first");
            return false;
        }

        void ShowLayersPanel()
        {
            if (!IsBoard) return;
            if (layersPanel != null && layersPanel.Visible) { layersPanel.Hide(); return; }
            if (active) End();
            if (Writing) CommitText();
            if (layersPanel == null)
            {
                layersPanel = new LayersPanel(this, dpiScale);
                layersPanel.VisibleChanged += (o, e) => { if (!layersPanel.Visible && !closing) Activate(); };
            }
            layersPanel.RefreshLayers();
            layersPanel.PlaceBelow(ToolbarItemScreenRect(layerItem), Monitor);
            layersPanel.Show(this);
            layersPanel.Activate();
        }

        public void SelectLayer(string id)
        {
            if (Document.Layer(id) == null || id == activeLayer) return;
            if (active) End();
            if (Writing) CommitText();
            PutDown();
            activeLayer = id;
            lastLayer[Document.FilePath] = id;
            AfterLayerChange();
        }

        public void NewLayer()
        {
            if (Document.Layers.Count >= InkDocument.MaxLayers) return;
            PutDown();
            var names = new HashSet<string>(Document.Layers.Select(l => l.Name));
            int n = Document.Layers.Count + 1;
            while (names.Contains("Layer " + n)) n++;
            string id = Document.AddLayer("Layer " + n, author);
            var a = new UndoAction();
            a.Layers.Add(new[] { id, "deleted", "1", "0" });
            undo.Push(a);
            redo.Clear();
            activeLayer = id;
            lastLayer[Document.FilePath] = id;
            AfterLayerChange();
        }

        public void DeleteLayer(string id)
        {
            var layers = Document.Layers;
            if (layers.Count <= 1) return;
            ChangeLayer(id, "deleted", "1");
        }

        // dir 1 = up (towards the top), -1 = down.
        public void MoveLayer(string id, int dir)
        {
            var ls = Document.Layers;
            int i = ls.FindIndex(l => l.Id == id), j = i + dir;
            if (i < 0 || j < 0 || j >= ls.Count) return;
            double order = dir > 0 ? (j == ls.Count - 1 ? ls[j].Order + 1 : (ls[j].Order + ls[j + 1].Order) / 2)
                                   : (j == 0 ? ls[0].Order - 1 : (ls[j].Order + ls[j - 1].Order) / 2);
            ChangeLayer(id, "order", order.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        public void RenameLayer(string id, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length > 0) ChangeLayer(id, "name", name.Length > 40 ? name.Substring(0, 40) : name);
        }

        public void ShowLayer(string id, bool show) { ChangeLayer(id, "show", show ? "1" : "0"); }
        public void LockLayer(string id, bool locked) { ChangeLayer(id, "lock", locked ? "1" : "0"); }

        // The active layer's opacity while its slider moves (nothing written yet), then for good.
        public void PreviewLayerOpacity(int value)
        {
            previewOpacity = Math.Max(0, Math.Min(100, value));
            Compose(ClientArea, true);
        }

        public void SetLayerOpacity(string id, int value)
        {
            previewOpacity = null;
            ChangeLayer(id, "opacity", Math.Max(0, Math.Min(100, value)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Compose(ClientArea, true);
        }

        // One layer property, as one undo step.
        void ChangeLayer(string id, string what, string value)
        {
            if (active) End();
            if (Writing) CommitText();
            PutDown();
            string old = Document.LayerValue(id, what);
            if (old == null || old == value) return;
            Document.SetLayer(id, what, value, author);
            var a = new UndoAction();
            a.Layers.Add(new[] { id, what, old, value });
            undo.Push(a);
            redo.Clear();
            AfterLayerChange();
        }

        // The active layer was deleted (or undone away): draw on the top one.
        void KeepActiveLayer()
        {
            var l = Document.Layer(activeLayer);
            if (l != null && !l.Deleted) return;
            var layers = Document.Layers;
            if (layers.Count > 0) activeLayer = layers[layers.Count - 1].Id;
            if (layersPanel != null && layersPanel.Visible) layersPanel.RefreshLayers();
        }

        void AfterLayerChange()
        {
            KeepActiveLayer();
            RenderAll(true);
            RefreshToolbar();
            if (layersPanel != null && layersPanel.Visible) layersPanel.RefreshLayers();
        }

        public void ClearAll()
        {
            if (active) End();
            if (Writing) CancelText();
            CancelSelection();
            if (!ActiveEditable()) return;
            var ids = ActiveStrokes.Select(s => s.Id).ToList();
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
            if (t != EditorTool.Select) PutDown();
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
            PutDown();
            Close();
        }

        // Saves what is on screen (board or wallpaper with the drawing) as a PNG in Pictures\LiveWall Boards. No file
        // dialog: it would load the shell into this always-running process for good.
        void SaveImage()
        {
            if (active) End();
            if (Writing) CommitText();
            PutDown();
            try
            {
                Directory.CreateDirectory(Boards.ExportDir);
                string path = Path.Combine(Boards.ExportDir, exportName + " " + DateTime.Now.ToString("HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                if (above == null) baseLayer.Save(path, ImageFormat.Png);
                else
                    using (var all = new Bitmap(baseLayer))
                    {
                        using (var g = Graphics.FromImage(all)) g.DrawImageUnscaled(above, 0, 0);
                        all.Save(path, ImageFormat.Png);
                    }
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
            InkToolbar.Item pen = null;
            pen = InkToolbar.Item.Button("\uE70F", "P", "Pen and brushes (P). Click again: soft, spray, pencil, marker, calligraphy, chalk, crayon, neon, dashed",
                () =>
                {
                    if (tool != EditorTool.Pen) { SetTool(EditorTool.Pen); toolbar.CloseFlyout(); }
                    else if (toolbar.FlyoutOpen) toolbar.CloseFlyout();
                    else toolbar.ShowFlyout(pen, BuildBrushFlyout());
                },
                () => tool == EditorTool.Pen);
            pen.Icon = (g, r, fg) => InkBrush.DrawIcon(g, r, penBrush, fg);
            pen.IconWhen = () => InkBrush.IsBrush(penBrush);
            pen.HasFlyout = true;
            penItem = pen;
            items.Add(pen);
            items.Add(InkToolbar.Item.Button("\uE7E6", "H", "Highlighter (H)", () => SetTool(EditorTool.Highlighter), () => tool == EditorTool.Highlighter));
            InkToolbar.Item eraser = null;
            eraser = InkToolbar.Item.Button("\uE75C", "E", "Eraser (E, or the pen's eraser end / right mouse button). Click again: erase only what it touches, or whole strokes.",
                () => { SetTool(EditorTool.Eraser); if (toolbar.FlyoutOpen) toolbar.CloseFlyout(); else toolbar.ShowFlyout(eraser, BuildEraserFlyout()); },
                () => tool == EditorTool.Eraser);
            eraser.HasFlyout = true;
            eraserItem = eraser;
            items.Add(eraser);
            InkToolbar.Item select = null;
            select = InkToolbar.Item.Custom((g, r, fg) => DrawSelectIcon(g, r, fg, selectShape == SelectShape.Lasso),
                "Select part of the drawing (V; again: rectangle or lasso). Then drag to move, handles to resize or rotate; Ctrl+C / Ctrl+X / Ctrl+V, Delete.",
                () => { SetTool(EditorTool.Select); if (toolbar.FlyoutOpen) toolbar.CloseFlyout(); else toolbar.ShowFlyout(select, BuildSelectFlyout()); },
                () => tool == EditorTool.Select);
            select.HasFlyout = true;
            selectItem = select;
            items.Add(select);
            InkToolbar.Item shapes = null;
            shapes = InkToolbar.Item.Custom((g, r, fg) => InkToolbar.DrawShapeIcon(g, r, lastShape, ShapeFilled, fg),
                "Shapes: line (L), arrow (A), rectangle (R), ellipse (O), drawn with the pen's brush. Hold Shift for straight lines, squares and circles.",
                () => { SetTool(EditorTool.Shape); if (toolbar.FlyoutOpen) toolbar.CloseFlyout(); else toolbar.ShowFlyout(shapes, BuildShapeFlyout()); },
                () => tool == EditorTool.Shape);
            shapes.HasFlyout = true;
            items.Add(shapes);
            InkToolbar.Item fill = null;
            fill = InkToolbar.Item.Button("\uEB42", "F", "Fill: click inside an area closed by lines, shapes or text to fill it (it goes under highlighter), " +
                "or on a line or shape to recolor it (F). Click again: how big a gap in an outline it may close.",
                () => { SetTool(EditorTool.Fill); if (toolbar.FlyoutOpen) toolbar.CloseFlyout(); else toolbar.ShowFlyout(fill, BuildFillFlyout()); },
                () => tool == EditorTool.Fill);
            fill.HasFlyout = true;
            items.Add(fill);
            items.Add(InkToolbar.Item.Button("\uE8D2", "T", "Text, emoji, kaomoji and symbols: click where they go; click text to change it (T)",
                () => SetTool(EditorTool.Text), () => tool == EditorTool.Text));
            items.Add(InkToolbar.Item.Button("\uEF3C", "I", "Eyedropper: pick a color from the drawing (I)", UsePicker, () => tool == EditorTool.Picker));
            InkToolbar.Item glow = null;
            glow = InkToolbar.Item.Button("\uE706", "G", "Glow (G): pens, shapes and text shine. Click again: how bright, and steady, pulse, twinkle or flicker " +
                "(boards play the animation as the wallpaper)",
                () =>
                {
                    if (!glowOn) { SetGlow(true); toolbar.ShowFlyout(glow, BuildGlowFlyout()); }
                    else if (toolbar.FlyoutOpen) toolbar.CloseFlyout();
                    else toolbar.ShowFlyout(glow, BuildGlowFlyout());
                },
                () => glowOn);
            glow.HasFlyout = true;
            glowItem = glow;
            items.Add(glow);
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
            items.Add(InkToolbar.Item.Button("\uE74D", "X", IsBoard ? "Clear this layer (Delete) - can be undone" : "Clear everything (Delete) - can be undone",
                ClearAll, null, () => ActiveStrokes.Count > 0));
            if (IsBoard)
            {
                items.Add(InkToolbar.Item.Separator());
                layerItem = InkToolbar.Item.Label(() => LayerTitle, "Layers: add, hide, lock, reorder and fade layers; the one named here is the one you draw on",
                    ShowLayersPanel);
                items.Add(layerItem);
            }
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
            items.Add(InkToolbar.Item.Hint("Esc = done"));
            return items;
        }

        List<InkToolbar.Item> BuildSelectFlyout()
        {
            return new List<InkToolbar.Item>
            {
                InkToolbar.Item.Segment(new[] { "Rectangle", "Lasso" }, "Select a rectangle, or draw around any shape (V switches)",
                    () => selectShape == SelectShape.Lasso ? 1 : 0, i => { selectShape = i == 1 ? SelectShape.Lasso : SelectShape.Rectangle; SetTool(EditorTool.Select); toolbar.CloseFlyout(); }),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Button("\uE77F", "P", "Paste a picture or a copied part of a drawing (Ctrl+V)", Paste, null)
            };
        }

        void ToggleSelectShape()
        {
            if (active) return;
            selectShape = selectShape == SelectShape.Lasso ? SelectShape.Rectangle : SelectShape.Lasso;
            RefreshToolbar();
            if (toolbar != null && selectItem != null) toolbar.ShowMessage(selectItem, selectShape == SelectShape.Lasso ? "Select: lasso (draw around it)" : "Select: rectangle");
        }

        // ------------------------------------------------------------------ glow

        // The editor's glow settings on a new pen stroke, shape or text.
        InkStroke Glowing(InkStroke s)
        {
            return glowOn && InkGlow.Applies(s) ? s.WithGlow(glowStrength, glowAnim, glowAnim == InkGlow.Steady ? 0 : glowSpeed, glowDim) : s;
        }

        void SetGlow(bool on)
        {
            glowOn = on;
            RefreshToolbar();
            if (toolbar != null && glowItem != null)
                toolbar.ShowMessage(glowItem, on ? "Glow on: " + InkGlow.Title(glowAnim).ToLowerInvariant() + ", " + glowStrength + "%" : "Glow off");
        }

        List<InkToolbar.Item> BuildGlowFlyout()
        {
            var kinds = InkGlow.Kinds;
            return new List<InkToolbar.Item>
            {
                InkToolbar.Item.Segment(new[] { "Off", "Steady", "Pulse", "Twinkle", "Flicker" },
                    "Glow for what you draw next: off, steady, or animated (on a board it plays as the wallpaper)",
                    () => glowOn ? Array.IndexOf(kinds, glowAnim) + 1 : 0,
                    i => { glowOn = i > 0; if (i > 0) glowAnim = kinds[i - 1]; RefreshToolbar(); }),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Hint("Bright"),
                InkToolbar.Item.Slider("How bright the glow is (" + glowStrength + "%)", () => (glowStrength - 10) / 90f,
                    v => { glowStrength = 10 + (int)Math.Round(v * 90); RefreshToolbar(); },
                    () => (6 + glowStrength * 0.18f) * dpiScale, () => glowOn),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Hint("Dim to"),
                InkToolbar.Item.Slider("Dim to: how dark it gets between flashes (" + glowDim + "%; more = stronger twinkle)", () => glowDim / 100f,
                    v => { glowDim = (int)Math.Round(v * 100); RefreshToolbar(); },
                    () => (22 - glowDim * 0.16f) * dpiScale, () => glowOn && glowAnim != InkGlow.Steady),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Segment(new[] { "Slow", "Medium", "Fast" }, "How fast it pulses, twinkles or flickers",
                    () => glowSpeed - 1, i => { glowSpeed = i + 1; RefreshToolbar(); })
            };
        }

        List<InkToolbar.Item> BuildBrushFlyout()
        {
            var items = new List<InkToolbar.Item>();
            foreach (string b in InkBrush.All)
            {
                string brush = b;
                Action pick = () =>
                {
                    penBrush = brush;
                    SetTool(EditorTool.Pen);
                    RefreshToolbar();   // the flyout stays open for the spread; drawing closes it
                };
                Func<bool> chosen = () => penBrush == brush;
                items.Add(brush == InkBrush.Pen ? InkToolbar.Item.Button("\uE70F", "P", InkBrush.Tip(brush), pick, chosen)
                                                : InkToolbar.Item.Custom((g, r, fg) => InkBrush.DrawIcon(g, r, brush, fg), InkBrush.Tip(brush), pick, chosen));
            }
            items.Add(InkToolbar.Item.Separator());
            items.Add(InkToolbar.Item.Hint("Spread"));
            // Spread 25% .. 400%, on a log scale.
            items.Add(InkToolbar.Item.Slider("Spread: how far spray scatters, how soft the airbrush is, how wide neon glows (" + penSpread + "%)",
                () => (float)(Math.Log(penSpread / 25.0) / Math.Log(16)), v => { penSpread = (int)Math.Round(25 * Math.Pow(16, v)); RefreshToolbar(); },
                () => (4 + 20 * (float)(Math.Log(penSpread / 25.0) / Math.Log(16))) * dpiScale, () => InkBrush.UsesSpread(penBrush)));
            return items;
        }

        List<InkToolbar.Item> BuildFillFlyout()
        {
            return new List<InkToolbar.Item>
            {
                InkToolbar.Item.Segment(new[] { "Exact", "Close small gaps", "Close gaps" },
                    "How big a gap in an outline the fill jumps (a circle that doesn't quite close still fills)",
                    () => fillGap, i => { fillGap = i; SetTool(EditorTool.Fill); toolbar.CloseFlyout(); }),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Segment(new[] { "This layer", "All layers" }, "Which lines stop the fill: the layer you draw on, or every shown layer",
                    () => fillAllLayers ? 1 : 0, i => { fillAllLayers = i == 1; SetTool(EditorTool.Fill); toolbar.CloseFlyout(); })
            };
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
            else if (tool == EditorTool.Select) { Cursor c = SelectionCursor(e.Location); Cursor = c ?? Cursors.Cross; }
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
                case Keys.Escape: case Keys.Enter: if (Selecting) PutDown(); else Finish(); return true;
                case Keys.Control | Keys.A: SelectAll(); return true;
                case Keys.Control | Keys.C: CopySelection(); return true;
                case Keys.Control | Keys.X: CutSelection(); return true;
                case Keys.Control | Keys.V: Paste(); return true;
                case Keys.V: if (tool == EditorTool.Select) ToggleSelectShape(); else SetTool(EditorTool.Select); return true;
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Shift | Keys.Left: case Keys.Shift | Keys.Right: case Keys.Shift | Keys.Up: case Keys.Shift | Keys.Down:
                {
                    if (floating == null) return true;
                    int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
                    Keys k0 = keyData & Keys.KeyCode;
                    Nudge(k0 == Keys.Left ? -step : k0 == Keys.Right ? step : 0, k0 == Keys.Up ? -step : k0 == Keys.Down ? step : 0);
                    return true;
                }
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
                case Keys.G: SetGlow(!glowOn); return true;
                case Keys.OemOpenBrackets: CurrentSize = CurrentSize * 0.8f; return true;
                case Keys.OemCloseBrackets: CurrentSize = CurrentSize * 1.25f; return true;
                case Keys.Delete: if (floating != null) DeleteSelection(); else ClearAll(); return true;
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
            StopAnimation();
            if (toolbar != null && !toolbar.IsDisposed) toolbar.Close();
            if (colorPicker != null && !colorPicker.IsDisposed) colorPicker.Close();
            if (textPanel != null && !textPanel.IsDisposed) textPanel.Close();
            if (frame != null) { frame.Dispose(); frame = null; }
            if (baseLayer != null) { baseLayer.Dispose(); baseLayer = null; }
            if (background != null) { background.Dispose(); background = null; }
            if (ink != null) { ink.Dispose(); ink = null; }
            Drop(ref below);
            Drop(ref above);
            if (layersPanel != null && !layersPanel.IsDisposed) layersPanel.Close();
            if (picture != null) picture.Dispose();
            if (eraserCursor != null) { Cursor = Cursors.Default; eraserCursor.Dispose(); eraserCursor = null; }
            if (eraserCursorIcon != IntPtr.Zero) { DestroyIcon(eraserCursorIcon); eraserCursorIcon = IntPtr.Zero; }
            InkText.ClearCache();
            var h = Finished;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
