using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LiveWall.Ink
{
    // Canvas -> target (screen/bitmap) mapping. Always "fill": uniform scale, centered, cropping the overflow, which is
    // exactly how the same drawing is shown on screens of any size.
    internal struct InkMapping
    {
        public float Scale, OffsetX, OffsetY;

        public static InkMapping Fill(int canvasW, int canvasH, int targetW, int targetH)
        {
            var m = new InkMapping();
            m.Scale = Math.Max(targetW / (float)canvasW, targetH / (float)canvasH);
            m.OffsetX = (targetW - canvasW * m.Scale) / 2;
            m.OffsetY = (targetH - canvasH * m.Scale) / 2;
            return m;
        }

        public PointF ToTarget(float x, float y) { return new PointF(x * Scale + OffsetX, y * Scale + OffsetY); }
        public PointF ToCanvas(float x, float y) { return new PointF((x - OffsetX) / Scale, (y - OffsetY) / Scale); }

        public RectangleF ToTarget(RectangleF r)
        {
            return new RectangleF(r.X * Scale + OffsetX, r.Y * Scale + OffsetY, r.Width * Scale, r.Height * Scale);
        }
    }

    internal static class InkRenderer
    {
        public static readonly string[] Styles = { "whiteboard", "blackboard", "grid", "dots" };

        public static readonly Color[] Palette =
        {
            Color.FromArgb(unchecked((int)0xFF1F1F1F)), Color.FromArgb(unchecked((int)0xFFF4F4EE)), Color.FromArgb(unchecked((int)0xFFE53935)),
            Color.FromArgb(unchecked((int)0xFFFB8C00)), Color.FromArgb(unchecked((int)0xFFFFD60A)), Color.FromArgb(unchecked((int)0xFF43A047)),
            Color.FromArgb(unchecked((int)0xFF1E88E5)), Color.FromArgb(unchecked((int)0xFF8E24AA)), Color.FromArgb(unchecked((int)0xFFFF6FB1))
        };

        public const int HighlighterAlpha = 96;

        public static string StyleName(string style)
        {
            switch (style)
            {
                case "blackboard": return "Blackboard";
                case "grid": return "Grid paper";
                case "dots": return "Dotted paper";
                case "whiteboard": return "Whiteboard";
                default: return "Transparent";
            }
        }

        public static string NormalizeStyle(string style)
        {
            return Array.IndexOf(Styles, style) >= 0 ? style : "whiteboard";
        }

        public static bool IsDark(string style) { return style == "blackboard"; }

        // Default pen color index for a background.
        public static int DefaultColor(string style)
        {
            if (style == InkDocument.NoBackground) return 4;   // yellow on wallpapers
            return IsDark(style) ? 1 : 0;
        }

        public static float PressureFactor(byte p) { return 0.3f + 1.4f * p / 255f; }   // 128 -> ~1.0

        public static float MaxWidth(InkStroke s)
        {
            if (s.IsShape && InkBrush.IsBrush(s.Brush)) return s.Width * InkBrush.Extent(s.Brush, s.Spread / 100f);
            if (s.Tool == InkTool.Highlighter || s.IsShape || s.Tool == InkTool.Erase) return s.Width;
            if (s.Tool == InkTool.Fill || s.Tool == InkTool.Text || s.Tool == InkTool.Image) return 0;
            float k = InkBrush.IsBrush(s.Brush) ? InkBrush.Extent(s.Brush, s.Spread / 100f) : 1;
            if (!s.HasPressure) return s.Width * PressureFactor(s.Points.Length > 0 ? s.Points[0].P : (byte)128) * k;
            return s.Width * 1.7f * k;
        }

        static Color StrokeColor(InkTool tool, int argb)
        {
            Color c = Color.FromArgb(argb);
            return tool == InkTool.Highlighter ? Color.FromArgb(HighlighterAlpha, c.R, c.G, c.B) : Color.FromArgb(255, c.R, c.G, c.B);
        }

        // Animated boards (their picture and every video frame) blend without gamma correction: several times faster,
        // and the same in both, so the picture Windows shows while the video sleeps is exactly its first frame.
        [ThreadStatic] public static bool Linear;

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = Linear ? CompositingQuality.AssumeLinear : CompositingQuality.HighQuality;
        }

        // ------------------------------------------------------------------ strokes

        // `solid`: plain coverage for the fill tool's boundaries (brushes without grain, gaps or glow).
        public static void DrawStrokes(Graphics g, IEnumerable<InkStroke> strokes, InkMapping m, bool solid = false)
        {
            foreach (var s in strokes) DrawStroke(g, s, m, false, solid);
        }

        // Pens with constant pressure (mouse) and highlighters are drawn as one smooth polyline; pens with pressure as
        // round-capped segments whose width follows the pressure (the same way they are drawn live).
        // `opaque`: highlighters at full strength (the desktop ink layer applies their transparency to the whole window).
        public static void DrawStroke(Graphics g, InkStroke s, InkMapping m, bool opaque = false, bool solid = false)
        {
            if (s.Points.Length == 0) return;
            if (s.Glow > 0 && !solid)
            {
                InkGlow.Draw(g, s, m, (gg, mm) => DrawElement(gg, s, mm, opaque));
                return;
            }
            DrawElement(g, s, m, opaque, solid);
        }

        internal static void DrawElement(Graphics g, InkStroke s, InkMapping m, bool opaque, bool solid = false)
        {
            if (s.Tool == InkTool.Pen && InkBrush.IsBrush(s.Brush))
            {
                InkBrush.Draw(g, s.Brush, s.Argb, s.Width, s.Points, s.Points.Length, InkBrush.Seed(s.Id), m, RectangleF.Empty, solid, s.Spread / 100f);
                return;
            }
            switch (s.Tool)
            {
                case InkTool.Erase: DrawErase(g, s, m); return;
                case InkTool.Image: InkImage.Draw(g, s, m); return;
                case InkTool.Fill: InkFill.Draw(g, s, m); return;
                case InkTool.Text: InkText.Draw(g, s, m); return;
                case InkTool.Pen: case InkTool.Highlighter:
                    DrawPoints(g, s.Tool, s.Argb, s.Width, s.Points, s.Points.Length, s.HasPressure, m, opaque);
                    return;
            }
            if (s.Points.Length >= 2)
                DrawShape(g, s.Tool, s.Argb, s.Width, s.Points[0], s.Points[1], s.Filled, m, solid ? null : s.Brush, s.Spread / 100f, InkBrush.Seed(s.Id));
        }

        // The partial eraser: makes its path transparent. Only ever drawn onto an ink-only layer (never onto a background).
        public static void DrawErase(Graphics g, InkStroke s, InkMapping m)
        {
            var pts = new PointF[s.Points.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = m.ToTarget(s.Points[i].X, s.Points[i].Y);
            if (s.Filled) DrawEraseArea(g, pts);
            else DrawErasePath(g, pts, s.Width * m.Scale);
        }

        // Makes the area inside the outline transparent (ink-only layers, like DrawErasePath).
        public static void DrawEraseArea(Graphics g, PointF[] outline)
        {
            if (outline.Length < 3) return;
            var mode = g.CompositingMode;
            var smoothing = g.SmoothingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.SmoothingMode = SmoothingMode.None;   // exactly the pixels a selection lifts (no half-cleared edge)
            using (var b = new SolidBrush(Color.Transparent)) g.FillPolygon(b, outline);
            g.SmoothingMode = smoothing;
            g.CompositingMode = mode;
        }

        public static void DrawErasePath(Graphics g, PointF[] pts, float diameter)
        {
            if (pts.Length == 0) return;
            var mode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            float d = Math.Max(1, diameter);
            if (pts.Length == 1)
                using (var b = new SolidBrush(Color.Transparent)) g.FillEllipse(b, pts[0].X - d / 2, pts[0].Y - d / 2, d, d);
            else
                using (var pen = new Pen(Color.Transparent, d) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, pts);
            g.CompositingMode = mode;
        }

        // Line, arrow, rectangle or ellipse from `a` to `b` (canvas units). `brush`: the outline drawn with that pen brush
        // (a filled shape is filled plain first).
        public static void DrawShape(Graphics g, InkTool tool, int argb, float width, InkPoint a, InkPoint b, bool filled, InkMapping m,
                                     string brushKind = null, float spread = 1, uint seed = 0)
        {
            if (InkBrush.IsBrush(brushKind)) { DrawBrushShape(g, tool, argb, width, a, b, filled, m, brushKind, spread, seed); return; }
            Color color = Color.FromArgb(255, Color.FromArgb(argb));
            float w = Math.Max(0.8f, width * m.Scale);
            PointF pa = m.ToTarget(a.X, a.Y), pb = m.ToTarget(b.X, b.Y);
            var r = RectangleF.FromLTRB(Math.Min(pa.X, pb.X), Math.Min(pa.Y, pb.Y), Math.Max(pa.X, pb.X), Math.Max(pa.Y, pb.Y));
            using (var pen = new Pen(color, w))
            using (var brush = new SolidBrush(color))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                switch (tool)
                {
                    case InkTool.Line: g.DrawLine(pen, pa, pb); break;
                    case InkTool.Arrow:
                    {
                        PointF[] head = ArrowHeadPoints(a, b, width);
                        for (int i = 0; i < head.Length; i++) head[i] = m.ToTarget(head[i].X, head[i].Y);
                        // The shaft stops inside the head so its round cap doesn't poke out of the tip.
                        float len = (float)Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Y - pa.Y) * (pb.Y - pa.Y));
                        float cut = len <= 0.01f ? 0 : Math.Min(1, ArrowHead(width) * m.Scale * 0.6f / len);
                        g.DrawLine(pen, pa, new PointF(pb.X - (pb.X - pa.X) * cut, pb.Y - (pb.Y - pa.Y) * cut));
                        pen.LineJoin = LineJoin.Round;
                        g.FillPolygon(brush, head);
                        g.DrawPolygon(pen, head);
                        break;
                    }
                    case InkTool.Rectangle:
                        pen.LineJoin = LineJoin.Miter;
                        if (filled) g.FillRectangle(brush, r);
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                        break;
                    case InkTool.Ellipse:
                        if (filled) g.FillEllipse(brush, r);
                        g.DrawEllipse(pen, r);
                        break;
                }
            }
        }

        static void DrawBrushShape(Graphics g, InkTool tool, int argb, float width, InkPoint a, InkPoint b, bool filled, InkMapping m,
                                   string brush, float spread, uint seed)
        {
            var r = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            if (filled && (tool == InkTool.Rectangle || tool == InkTool.Ellipse))
                using (var fill = new SolidBrush(Color.FromArgb(255, Color.FromArgb(argb))))
                {
                    RectangleF t = m.ToTarget(r);
                    if (tool == InkTool.Rectangle) g.FillRectangle(fill, t); else g.FillEllipse(fill, t);
                }
            foreach (InkPoint[] path in ShapePaths(tool, width, a, b, r))
                InkBrush.Draw(g, brush, argb, width, path, path.Length, seed++, m, RectangleF.Empty, false, spread);
        }

        // A shape's outline as pen paths (canvas units, even pressure): what a brush draws along.
        static List<InkPoint[]> ShapePaths(InkTool tool, float width, InkPoint a, InkPoint b, RectangleF r)
        {
            var list = new List<InkPoint[]>();
            switch (tool)
            {
                case InkTool.Line: list.Add(new[] { P(a.X, a.Y), P(b.X, b.Y) }); break;
                case InkTool.Arrow:
                {
                    PointF[] head = ArrowHeadPoints(a, b, width);
                    list.Add(new[] { P(a.X, a.Y), P((head[1].X + head[2].X) / 2, (head[1].Y + head[2].Y) / 2) });
                    list.Add(new[] { P(head[1].X, head[1].Y), P(head[0].X, head[0].Y), P(head[2].X, head[2].Y), P(head[1].X, head[1].Y) });
                    break;
                }
                case InkTool.Rectangle:
                    list.Add(new[] { P(r.Left, r.Top), P(r.Right, r.Top), P(r.Right, r.Bottom), P(r.Left, r.Bottom), P(r.Left, r.Top) });
                    break;
                case InkTool.Ellipse:
                {
                    float rx = r.Width / 2, ry = r.Height / 2, cx = r.Left + rx, cy = r.Top + ry;
                    int n = Math.Max(24, Math.Min(360, (int)((rx + ry) / 3)));
                    var pts = new InkPoint[n + 1];
                    for (int i = 0; i <= n; i++) { double t = i * 2 * Math.PI / n; pts[i] = P(cx + rx * (float)Math.Cos(t), cy + ry * (float)Math.Sin(t)); }
                    list.Add(pts);
                    break;
                }
            }
            return list;
        }

        static InkPoint P(float x, float y) { return new InkPoint(x, y, 128); }

        public static float ArrowHead(float width) { return Math.Max(width * 3.2f, 10f); }

        // Tip, then the two back corners (canvas units).
        public static PointF[] ArrowHeadPoints(InkPoint a, InkPoint b, float width)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) { dx = 1; dy = 0; len = 1; }
            float ux = dx / len, uy = dy / len, head = Math.Min(ArrowHead(width), Math.Max(len, 1) * 0.9f + width), half = head * 0.55f;
            float bx = b.X - ux * head, by = b.Y - uy * head;
            return new[] { new PointF(b.X, b.Y), new PointF(bx - uy * half, by + ux * half), new PointF(bx + uy * half, by - ux * half) };
        }

        public static void DrawPoints(Graphics g, InkTool tool, int argb, float width, InkPoint[] pts, int count, bool pressure, InkMapping m,
                                      bool opaque = false)
        {
            if (count == 0) return;
            Color color = opaque ? Color.FromArgb(255, Color.FromArgb(argb)) : StrokeColor(tool, argb);
            if (count == 1)
            {
                float d = Math.Max(1f, width * (tool == InkTool.Highlighter ? 1 : PressureFactor(pts[0].P)) * m.Scale);
                PointF c = m.ToTarget(pts[0].X, pts[0].Y);
                using (var b = new SolidBrush(color)) g.FillEllipse(b, c.X - d / 2, c.Y - d / 2, d, d);
                return;
            }
            if (tool == InkTool.Highlighter || !pressure)
            {
                float w = width * (tool == InkTool.Highlighter ? 1 : PressureFactor(pts[0].P)) * m.Scale;
                var arr = new PointF[count];
                for (int i = 0; i < count; i++) arr[i] = m.ToTarget(pts[i].X, pts[i].Y);
                using (var pen = MakePen(color, w, tool == InkTool.Highlighter)) g.DrawLines(pen, arr);
                return;
            }
            for (int i = 1; i < count; i++) DrawSegment(g, color, width, pts[i - 1], pts[i], m);
        }

        // Only the segments of a freehand stroke that touch `clip` (target pixels), each as the live editor draws it.
        public static void DrawPointsIn(Graphics g, InkTool tool, int argb, float width, InkPoint[] pts, int count, bool pressure, InkMapping m, Rectangle clip)
        {
            if (count <= 1) { DrawPoints(g, tool, argb, width, pts, count, pressure, m); return; }
            Color color = StrokeColor(tool, argb);
            float reach = width * 1.7f * m.Scale / 2 + 2;
            for (int i = 1; i < count; i++)
            {
                InkPoint a = pts[i - 1], b = pts[i];
                PointF pa = m.ToTarget(a.X, a.Y), pb = m.ToTarget(b.X, b.Y);
                var r = RectangleF.FromLTRB(Math.Min(pa.X, pb.X) - reach, Math.Min(pa.Y, pb.Y) - reach, Math.Max(pa.X, pb.X) + reach, Math.Max(pa.Y, pb.Y) + reach);
                if (!r.IntersectsWith(clip)) continue;
                if (!pressure) { a.P = pts[0].P; b.P = pts[0].P; }
                DrawSegment(g, color, width, a, b, m);
            }
        }

        public static void DrawSegment(Graphics g, InkTool tool, int argb, float width, InkPoint a, InkPoint b, InkMapping m)
        {
            DrawSegment(g, StrokeColor(tool, argb), width, a, b, m);
        }

        static void DrawSegment(Graphics g, Color color, float width, InkPoint a, InkPoint b, InkMapping m)
        {
            float w = Math.Max(0.8f, width * PressureFactor((byte)((a.P + b.P) / 2)) * m.Scale);
            PointF pa = m.ToTarget(a.X, a.Y), pb = m.ToTarget(b.X, b.Y);
            using (var pen = MakePen(color, w, false)) g.DrawLine(pen, pa, pb);
        }

        static Pen MakePen(Color c, float w, bool highlighter)
        {
            var pen = new Pen(c, Math.Max(0.8f, w));
            pen.StartCap = pen.EndCap = highlighter ? LineCap.Square : LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            return pen;
        }

        // ------------------------------------------------------------------ backgrounds

        // Returns where the header went (empty without one).
        public static RectangleF DrawBackground(Graphics g, string style, Rectangle target, InkMapping m, string header, int seed)
        {
            RectangleF headerRect = RectangleF.Empty;
            bool dark = IsDark(style);
            Color baseColor = dark ? Color.FromArgb(255, 0x22, 0x2E, 0x28) : Color.FromArgb(255, 0xF7, 0xF7, 0xF4);
            using (var b = new SolidBrush(baseColor)) g.FillRectangle(b, target);

            var state = g.Save();
            g.SetClip(target);
            if (dark)
            {
                // Faint chalk dust so it reads as a blackboard rather than a flat color.
                // Soft-edged smudges (radial fade to nothing), like half-wiped chalk.
                // Plain (not gamma-correct) blending: faint gradients then step by one level instead of three or four,
                // which showed as rings, and it is several times faster.
                var rnd = new Random(seed);
                var quality = g.CompositingQuality;
                g.CompositingQuality = CompositingQuality.AssumeLinear;
                float unit = Math.Max(target.Width, target.Height) / 10f;
                for (int i = 0; i < 26; i++)
                {
                    float w = unit * (1.2f + (float)rnd.NextDouble() * 2.5f), h = w * (0.25f + (float)rnd.NextDouble() * 0.35f);
                    float x = target.Left + (float)rnd.NextDouble() * target.Width - w / 2, y = target.Top + (float)rnd.NextDouble() * target.Height - h / 2;
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(x, y, w, h);
                        using (var br = new PathGradientBrush(path))
                        {
                            br.CenterColor = Color.FromArgb((7 + rnd.Next(6)) * 2, 255, 255, 255);
                            br.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                            g.FillPath(br, path);
                        }
                    }
                }
                g.CompositingQuality = quality;
            }
            else if (style == "grid")
            {
                float step = 48 * m.Scale;
                using (var pen = new Pen(Color.FromArgb(255, 0xE2, 0xE6, 0xEA), Math.Max(1f, m.Scale)))
                {
                    for (float x = target.Left + Mod(m.OffsetX, step); x < target.Right; x += step) g.DrawLine(pen, x, target.Top, x, target.Bottom);
                    for (float y = target.Top + Mod(m.OffsetY, step); y < target.Bottom; y += step) g.DrawLine(pen, target.Left, y, target.Right, y);
                }
            }
            else if (style == "dots")
            {
                float step = 32 * m.Scale, d = Math.Max(2f, 3 * m.Scale);
                using (var br = new SolidBrush(Color.FromArgb(255, 0xC9, 0xCF, 0xD6)))
                {
                    for (float y = target.Top + Mod(m.OffsetY, step); y < target.Bottom; y += step)
                        for (float x = target.Left + Mod(m.OffsetX, step); x < target.Right; x += step)
                            g.FillEllipse(br, x - d / 2, y - d / 2, d, d);
                }
            }

            if (!string.IsNullOrEmpty(header))
            {
                float size = Math.Max(11f, 30 * m.Scale);
                using (var font = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(140, 60, 64, 67)))
                {
                    SizeF sz = g.MeasureString(header, font);
                    // Top right, clear of the desktop icons (which Windows puts on the left).
                    float x = target.Right - sz.Width - 56 * m.Scale, y = target.Top + 40 * m.Scale;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(header, font, br, x, y);
                    headerRect = new RectangleF(x, y, sz.Width, sz.Height);
                }
            }
            g.Restore(state);
            return headerRect;
        }

        static float Mod(float a, float b) { float r = a % b; return r < 0 ? r + b : r; }

        // Draws a picture into `target` the way the wallpaper setting shows it.
        public static void DrawPicture(Graphics g, Image img, Rectangle target, FitMode fit)
        {
            using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, target);
            if (img == null) return;
            float iw = img.Width, ih = img.Height;
            RectangleF dst;
            switch (fit)
            {
                case FitMode.Fit:
                {
                    float s = Math.Min(target.Width / iw, target.Height / ih);
                    dst = new RectangleF(target.Left + (target.Width - iw * s) / 2, target.Top + (target.Height - ih * s) / 2, iw * s, ih * s);
                    break;
                }
                case FitMode.Stretch:
                    dst = target;
                    break;
                case FitMode.Center:
                    dst = new RectangleF(target.Left + (target.Width - iw) / 2, target.Top + (target.Height - ih) / 2, iw, ih);
                    break;
                default:
                {
                    float s = Math.Max(target.Width / iw, target.Height / ih);
                    dst = new RectangleF(target.Left + (target.Width - iw * s) / 2, target.Top + (target.Height - ih * s) / 2, iw * s, ih * s);
                    break;
                }
            }
            var state = g.Save();
            g.SetClip(target);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(img, dst);
            g.Restore(state);
        }

        // ------------------------------------------------------------------ whole images

        // Renders a board (background + strokes) to a PNG file. Runs on the worker thread.
        public static bool RenderBoardFile(string style, List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers, int canvasW, int canvasH, int w, int h,
                                           string header, int seed, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool linear = Linear;
                Linear = InkGlow.AnyAnimated(layers);
                try
                {
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        Prepare(g);
                        var m = InkMapping.Fill(canvasW, canvasH, w, h);
                        DrawBackground(g, style, new Rectangle(0, 0, w, h), m, header, seed);
                        // The ink on its own layer(s), so the eraser clears ink and not the board. Glows as at the start of
                        // their animation: the picture is the video's first frame.
                        bool timed = InkGlow.Timed;
                        InkGlow.Timed = true;
                        InkGlow.Time = 0;
                        try { DrawLayers(g, layers, m, w, h); }
                        finally { InkGlow.Timed = timed; }
                    }
                    string tmp = path + ".part";
                    bmp.Save(tmp, ImageFormat.Png);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                }
                finally { Linear = linear; }
                return true;
            }
            catch (Exception ex) { Log.Error("Could not render board", ex); return false; }
        }

        // Layers bottom to top onto `g` (a w x h target): each drawn on a clear bitmap (erasers clear only their own layer),
        // then laid over with its opacity. One scratch bitmap, however many layers.
        // `clip` (empty = all): only that area is drawn.
        public static void DrawLayers(Graphics g, List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers, InkMapping m, int w, int h,
                                      Rectangle clip = default(Rectangle))
        {
            if (layers.Count == 0) return;
            Rectangle area = clip.IsEmpty ? new Rectangle(0, 0, w, h) : clip;
            // The scratch bitmap covers just the area; strokes are moved into it.
            using (var scratch = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb))
                foreach (var kv in layers)
                {
                    using (var gs = Graphics.FromImage(scratch))
                    {
                        gs.Clear(Color.Transparent);
                        Prepare(gs);
                        gs.TranslateTransform(-area.X, -area.Y);
                        DrawStrokes(gs, kv.Value, m);
                    }
                    if (area.X == 0 && area.Y == 0) DrawWithOpacity(g, scratch, new Rectangle(0, 0, area.Width, area.Height), kv.Key.Opacity);
                    else
                    {
                        var state = g.Save();
                        g.TranslateTransform(area.X, area.Y);
                        DrawWithOpacity(g, scratch, new Rectangle(0, 0, area.Width, area.Height), kv.Key.Opacity);
                        g.Restore(state);
                    }
                }
        }

        // `r` of `img` onto the same place of `g`, at `opacity` percent.
        public static void DrawWithOpacity(Graphics g, Image img, Rectangle r, int opacity)
        {
            if (opacity <= 0) return;
            if (opacity >= 100)
            {
                if (r.X == 0 && r.Y == 0 && r.Width == img.Width && r.Height == img.Height) g.DrawImageUnscaled(img, 0, 0);
                else g.DrawImage(img, r, r, GraphicsUnit.Pixel);
                return;
            }
            // Plain (not gamma-correct) blending, as the editor shows it.
            var quality = g.CompositingQuality;
            g.CompositingQuality = CompositingQuality.Default;
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = opacity / 100f });
                g.DrawImage(img, r, r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, ia);
            }
            g.CompositingQuality = quality;
        }

        // Loads an image without keeping the file locked. Returns null if GDI+ cannot read it.
        public static Bitmap LoadImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                using (var img = Image.FromStream(ms, false, false))
                    return new Bitmap(img);
            }
            catch { return null; }
        }
    }
}
