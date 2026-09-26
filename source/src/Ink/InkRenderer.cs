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
            if (s.Tool == InkTool.Highlighter) return s.Width;
            if (!s.HasPressure) return s.Width * PressureFactor(s.Points.Length > 0 ? s.Points[0].P : (byte)128);
            return s.Width * 1.7f;
        }

        static Color StrokeColor(InkTool tool, int argb)
        {
            Color c = Color.FromArgb(argb);
            return tool == InkTool.Highlighter ? Color.FromArgb(HighlighterAlpha, c.R, c.G, c.B) : Color.FromArgb(255, c.R, c.G, c.B);
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
        }

        // ------------------------------------------------------------------ strokes

        public static void DrawStrokes(Graphics g, IEnumerable<InkStroke> strokes, InkMapping m)
        {
            foreach (var s in strokes) DrawStroke(g, s, m);
        }

        // Pens with constant pressure (mouse) and highlighters are drawn as one smooth polyline; pens with pressure as
        // round-capped segments whose width follows the pressure (the same way they are drawn live).
        // `opaque`: highlighters at full strength (the desktop ink layer applies their transparency to the whole window).
        public static void DrawStroke(Graphics g, InkStroke s, InkMapping m, bool opaque = false)
        {
            if (s.Points.Length == 0) return;
            DrawPoints(g, s.Tool, s.Argb, s.Width, s.Points, s.Points.Length, s.HasPressure, m, opaque);
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
        public static bool RenderBoardFile(string style, List<InkStroke> strokes, int canvasW, int canvasH, int w, int h,
                                           string header, int seed, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        Prepare(g);
                        var m = InkMapping.Fill(canvasW, canvasH, w, h);
                        DrawBackground(g, style, new Rectangle(0, 0, w, h), m, header, seed);
                        DrawStrokes(g, strokes, m);
                    }
                    string tmp = path + ".part";
                    bmp.Save(tmp, ImageFormat.Png);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex) { Log.Error("Could not render board", ex); return false; }
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
