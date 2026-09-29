using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiveWall.Ink
{
    // Pen brushes: the `brush=` attribute of a pen stroke (none = the round pen; older versions draw every brush as the
    // round pen). Anything random comes from a hash of the stroke's id and a fixed paper grain, never from Random, so
    // every replay on every PC draws the same pixels.
    //
    // A stroke can be drawn in part: only what touches `clip` (target pixels), with exactly the pixels the whole stroke
    // gives there. The editor uses that to draw a live stroke: each move redraws the area of the new segment over the
    // finished drawing, so the live stroke already looks final and the cost doesn't grow with the stroke's length.
    //
    // Kinds: stamps along the path at a fixed spacing (soft, spray, the neon glow); runs of a pen with a paper-grain
    // texture (pencil, chalk, crayon: the grain is on/off, so overlapping parts don't darken); a flat nib swept along the
    // path as one filled outline (marker, calligraphy); dashes measured along the path.
    internal static class InkBrush
    {
        public const string Pen = "", Soft = "soft", Spray = "spray", Pencil = "pencil", Marker = "marker", Calligraphy = "calligraphy",
            Chalk = "chalk", Crayon = "crayon", Neon = "neon", Dashed = "dashed";
        public static readonly string[] All = { Pen, Soft, Spray, Pencil, Marker, Calligraphy, Chalk, Crayon, Neon, Dashed };

        // A brush this version draws (other names, e.g. from a newer version, are drawn as the round pen).
        public static bool IsBrush(string b) { return !string.IsNullOrEmpty(b) && Array.IndexOf(All, b) > 0; }

        public static string Title(string b)
        {
            switch (b)
            {
                case Soft: return "Soft airbrush";
                case Spray: return "Spray";
                case Pencil: return "Pencil";
                case Marker: return "Marker";
                case Calligraphy: return "Calligraphy";
                case Chalk: return "Chalk";
                case Crayon: return "Crayon";
                case Neon: return "Neon";
                case Dashed: return "Dashed line";
                default: return "Pen";
            }
        }

        public static string Tip(string b)
        {
            switch (b)
            {
                case Soft: return "Soft airbrush: feathered edge, builds up where you go over it";
                case Spray: return "Spray: speckles, like a spray can";
                case Pencil: return "Pencil: thin and grainy; press lighter for lighter lines";
                case Marker: return "Marker: flat, opaque chisel tip";
                case Calligraphy: return "Calligraphy: angled flat nib, thick and thin with the direction";
                case Chalk: return "Chalk: broken, grainy edge (great on the blackboard)";
                case Crayon: return "Crayon: waxy, streaky";
                case Neon: return "Neon: glowing line (best on dark backgrounds)";
                case Dashed: return "Dashed line";
                default: return "Pen: round and smooth";
            }
        }

        // The paint's full width as a multiple of the stroke's width at that pressure (bounds, hit tests, redraw areas).
        // `spread`: 1 = normal (spray, soft and neon only).
        public static float Extent(string b, float spread = 1)
        {
            switch (b)
            {
                case Soft: return Math.Max(1.1f, 1.3f * spread);
                case Spray: return Math.Max(1.2f, 1.1f * spread + 0.1f);
                case Marker: return 1.6f;
                case Calligraphy: return 1.9f;
                case Neon: return Math.Max(1.1f, 2.6f * spread);
                default: return 1.1f;
            }
        }

        // Brushes whose spread can be set: how far spray scatters, how soft the airbrush is, how wide neon glows.
        public static bool UsesSpread(string b) { return b == Spray || b == Soft || b == Neon; }

        // Made of separate specks or pieces: glowing, each one shines and twinkles on its own (see InkGlow).
        public static bool Particles(string b) { return b == Spray || b == Pencil || b == Chalk || b == Crayon || b == Dashed; }

        // About one speck (or dash) per cell of this size, canvas units: the grain of their independent twinkling.
        public static float Cell(string b, float width)
        {
            switch (b)
            {
                case Spray: return Math.Max(3, width * 0.2f);
                case Pencil: return 3;
                case Dashed: return Math.Max(4, width * 2.4f + 2);
                default: return 4;
            }
        }

        // Scattered or see-through paint doesn't hold a fill in (like the highlighter).
        public static bool Blocks(string b) { return b != Soft && b != Spray; }

        // Seed for everything random in a stroke: FNV-1a of its id.
        public static uint Seed(string id)
        {
            uint h = 2166136261;
            if (id != null) foreach (char c in id) { h ^= c; h *= 16777619; }
            return h;
        }

        // `solid`: plain coverage, for the fill tool's boundaries (no grain, no gaps between dashes, no glow).
        public static void Draw(Graphics g, string brush, int argb, float width, InkPoint[] pts, int count, uint seed, InkMapping m,
                                RectangleF clip, bool solid = false, float spread = 1)
        {
            if (count <= 0) return;
            Color c = Color.FromArgb(255, Color.FromArgb(argb));
            spread = Math.Max(0.25f, Math.Min(4f, spread));
            switch (brush)
            {
                // Softer = bigger dabs, each fainter, so the middle stays about as strong.
                case Soft: Stamps(g, c, width, pts, count, m, clip, 1.3f * spread, 0.12f, 0.13f / (float)Math.Sqrt(spread)); break;
                case Spray: Speckles(g, c, width, pts, count, seed, m, clip, spread); break;
                case Pencil: Runs(g, c, width, 0.55f, pts, count, m, clip, solid ? null : Pencil); break;
                case Chalk: Runs(g, c, width, 1.0f, pts, count, m, clip, solid ? null : Chalk); break;
                case Crayon: Runs(g, c, width, 1.05f, pts, count, m, clip, solid ? null : Crayon); break;
                case Marker: Nib(g, c, width, pts, count, m, clip, 30, 1.2f, 0.6f); break;
                case Calligraphy: Nib(g, c, width, pts, count, m, clip, 45, 1.8f, 0.09f); break;
                case Neon:
                    // A glow, the colored tube, and its hot white middle (reads on dark and light boards).
                    if (!solid) Stamps(g, c, width, pts, count, m, clip, 2.6f * spread, 0.3f, 0.1f / (float)Math.Sqrt(spread));
                    Runs(g, c, width, solid ? 0.8f : 0.5f, pts, count, m, clip, null);
                    if (!solid) Runs(g, Mix(c, Color.White, 0.8f), width, 0.2f, pts, count, m, clip, null);
                    break;
                case Dashed: Dashes(g, c, width, pts, count, m, clip, solid); break;
            }
        }

        // A small sample for the toolbar (white on the dark bar).
        public static void DrawIcon(Graphics g, RectangleF r, string brush, Color fg)
        {
            var pts = new InkPoint[24];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (float)(pts.Length - 1);
                pts[i] = new InkPoint(r.Left + r.Width * t, r.Top + r.Height * (0.5f - 0.32f * (float)Math.Sin(t * Math.PI * 2)), 128);
            }
            float w = r.Height * (brush == Calligraphy || brush == Marker ? 0.2f : brush == Neon ? 0.26f : brush == Spray || brush == Soft ? 0.42f : 0.26f);
            var m = new InkMapping { Scale = 1 };
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Draw(g, brush, fg.ToArgb(), w, pts, pts.Length, 0x5eed, m, RectangleF.Empty);
            g.Restore(state);
        }

        // ------------------------------------------------------------------ stamps

        struct Stamp
        {
            public float X, Y, P;
            public int Index;
        }

        // Every `spacing` canvas units along the path (the first on the first point), pressure interpolated.
        static List<Stamp> Along(InkPoint[] pts, int count, float spacing)
        {
            var list = new List<Stamp>(64);
            list.Add(new Stamp { X = pts[0].X, Y = pts[0].Y, P = pts[0].P, Index = 0 });
            float carry = 0;   // distance from the last stamp to the start of this segment
            for (int i = 1; i < count; i++)
            {
                float ax = pts[i - 1].X, ay = pts[i - 1].Y, dx = pts[i].X - ax, dy = pts[i].Y - ay;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0) continue;
                float t = spacing - carry;
                while (t <= len)
                {
                    float f = t / len;
                    list.Add(new Stamp { X = ax + dx * f, Y = ay + dy * f, P = pts[i - 1].P + (pts[i].P - pts[i - 1].P) * f, Index = list.Count });
                    t += spacing;
                }
                carry = len - (t - spacing);
            }
            return list;
        }

        // Soft round dabs (airbrush, neon glow): they build up where they overlap, with a feathered edge.
        static void Stamps(Graphics g, Color c, float width, InkPoint[] pts, int count, InkMapping m, RectangleF clip,
                           float sizeK, float spacingK, float flow)
        {
            // Whole pixels, no scaling, linear blending: a dab is a plain copy (fast), and soft dabs don't need more.
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            foreach (var s in Along(pts, count, Math.Max(0.5f, width * spacingK)))
            {
                int d = (int)Math.Ceiling(Math.Max(2f, width * InkRenderer.PressureFactor((byte)s.P) * sizeK * m.Scale));
                PointF p = m.ToTarget(s.X, s.Y);
                var r = new Rectangle((int)Math.Round(p.X - d / 2f), (int)Math.Round(p.Y - d / 2f), d, d);
                if (!clip.IsEmpty && !clip.IntersectsWith(r)) continue;
                int level = Math.Max(1, Math.Min(32, (int)Math.Round(flow * (0.55f + s.P / 285f) * 128)));   // flow, in 1/128
                g.DrawImage(Dab(c, d, level), r, 0, 0, d, d, GraphicsUnit.Pixel);
            }
            g.Restore(state);
        }

        // `spread`: how far the specks scatter (more of them, the same size, so the spray keeps its density).
        static void Speckles(Graphics g, Color c, float width, InkPoint[] pts, int count, uint seed, InkMapping m, RectangleF clip, float spread)
        {
            int perStamp = Math.Max(4, Math.Min(240, (int)(width * 0.8f * spread)));
            float dot = Math.Max(0.8f, width * 0.07f);
            using (var b = new SolidBrush(c))
                foreach (var s in Along(pts, count, Math.Max(0.8f, width * 0.25f)))
                {
                    float radius = width * InkRenderer.PressureFactor((byte)s.P) * 0.55f * spread;
                    PointF p = m.ToTarget(s.X, s.Y);
                    float reach = (radius + dot) * m.Scale;
                    if (!clip.IsEmpty && !new RectangleF(p.X - reach, p.Y - reach, reach * 2, reach * 2).IntersectsWith(clip)) continue;
                    uint h = Hash(seed ^ Hash((uint)s.Index + 1));
                    for (int k = 0; k < perStamp; k++)
                    {
                        h = Hash(h + 0x9E3779B9);
                        float angle = (h & 0xFFFF) / 65536f * 6.2831853f, far = (float)Math.Pow((h >> 16) / 65536f, 0.75);
                        h = Hash(h + 0x9E3779B9);
                        float ds = dot * (0.6f + (h & 0xFFFF) / 65536f * 0.9f) * m.Scale;
                        float x = p.X + (float)Math.Cos(angle) * radius * far * m.Scale, y = p.Y + (float)Math.Sin(angle) * radius * far * m.Scale;
                        g.FillEllipse(b, x - ds / 2, y - ds / 2, ds, ds);
                    }
                }
        }

        // ------------------------------------------------------------------ pen runs (pencil, chalk, crayon, neon core)

        // Consecutive segments with the same width (quarter pixels) and grain density go as one polyline: no seams,
        // few calls. `grain` null = solid color.
        static void Runs(Graphics g, Color c, float width, float widthK, InkPoint[] pts, int count, InkMapping m, RectangleF clip, string grain)
        {
            if (count == 1)
            {
                float d = Math.Max(1f, width * widthK * InkRenderer.PressureFactor(pts[0].P) * m.Scale);
                PointF p = m.ToTarget(pts[0].X, pts[0].Y);
                var r = new RectangleF(p.X - d / 2, p.Y - d / 2, d, d);
                if (!clip.IsEmpty && !r.IntersectsWith(clip)) return;
                Brush b = grain == null ? (Brush)new SolidBrush(c) : Grain(grain, c, Density(grain, pts[0].P), m);
                try { g.FillEllipse(b, r); }
                finally { if (grain == null) b.Dispose(); }
                return;
            }
            int i = 0;
            while (i < count - 1)
            {
                int wq = WidthQ(width, widthK, pts[i], pts[i + 1], m), dq = grain == null ? 0 : DensityQ(grain, pts[i], pts[i + 1]);
                int j = i + 1;
                while (j < count - 1 && WidthQ(width, widthK, pts[j], pts[j + 1], m) == wq && (grain == null || DensityQ(grain, pts[j], pts[j + 1]) == dq)) j++;
                // points i..j
                float w = Math.Max(0.8f, wq / 4f);
                var arr = new PointF[j - i + 1];
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int k = i; k <= j; k++)
                {
                    PointF p = m.ToTarget(pts[k].X, pts[k].Y);
                    arr[k - i] = p;
                    if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                }
                var bounds = RectangleF.FromLTRB(minX - w, minY - w, maxX + w, maxY + w);
                if (clip.IsEmpty || bounds.IntersectsWith(clip))
                {
                    Brush b = grain == null ? (Brush)new SolidBrush(c) : Grain(grain, c, dq / 16f, m);
                    try
                    {
                        using (var pen = new Pen(b, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                            g.DrawLines(pen, arr);
                    }
                    finally { if (grain == null) b.Dispose(); }
                }
                i = j;
            }
        }

        static int WidthQ(float width, float widthK, InkPoint a, InkPoint b, InkMapping m)
        {
            return (int)Math.Round(width * widthK * InkRenderer.PressureFactor((byte)((a.P + b.P) / 2)) * m.Scale * 4);
        }

        // How much of the paper the grain covers: pressing harder fills more of it (pencil most of all).
        static float Density(string grain, int p)
        {
            switch (grain)
            {
                case Pencil: return 0.3f + 0.6f * p / 255f;
                case Chalk: return 0.62f + 0.3f * p / 255f;
                default: return 0.72f + 0.25f * p / 255f;
            }
        }

        static int DensityQ(string grain, InkPoint a, InkPoint b) { return (int)Math.Round(Density(grain, (a.P + b.P) / 2) * 16); }

        // ------------------------------------------------------------------ flat nib (marker, calligraphy)

        // The nib (a line `lengthK` x width long at `angle` degrees, `thickK` x width thick) swept along the path: one
        // filled outline (no seams), plus a round pen of the nib's thickness along the middle.
        static void Nib(Graphics g, Color c, float width, InkPoint[] pts, int count, InkMapping m, RectangleF clip, float angle, float lengthK, float thickK)
        {
            float ca = (float)Math.Cos(angle * Math.PI / 180), sa = (float)Math.Sin(angle * Math.PI / 180);
            float thick = Math.Max(0.8f, width * thickK * m.Scale);
            using (var brush = new SolidBrush(c))
            using (var path = new GraphicsPath(FillMode.Winding))
            {
                PointF prev1 = PointF.Empty, prev2 = PointF.Empty;
                for (int i = 0; i < count; i++)
                {
                    float half = width * lengthK * InkRenderer.PressureFactor(pts[i].P) / 2;
                    PointF p1 = m.ToTarget(pts[i].X + ca * half, pts[i].Y + sa * half), p2 = m.ToTarget(pts[i].X - ca * half, pts[i].Y - sa * half);
                    if (i > 0)
                    {
                        var quad = new[] { prev1, p1, p2, prev2 };
                        if (!clip.IsEmpty && !Bounds(quad, thick).IntersectsWith(clip)) { prev1 = p1; prev2 = p2; continue; }
                        // Same orientation for every piece: with the winding rule, overlaps then add up instead of cancelling.
                        float area = 0;
                        for (int k = 0; k < 4; k++) { PointF u = quad[k], v = quad[(k + 1) % 4]; area += u.X * v.Y - v.X * u.Y; }
                        if (area < 0) Array.Reverse(quad);
                        path.AddPolygon(quad);
                    }
                    else if (count == 1) path.AddPolygon(new[] { p1, new PointF(p1.X + 0.5f, p1.Y + 0.5f), p2, new PointF(p2.X - 0.5f, p2.Y - 0.5f) });
                    prev1 = p1;
                    prev2 = p2;
                }
                if (path.PointCount > 0) g.FillPath(brush, path);
            }
            Runs(g, c, width, thickK / InkRenderer.PressureFactor(128), pts, count, m, clip, null);
        }

        static RectangleF Bounds(PointF[] q, float pad)
        {
            float minX = q[0].X, minY = q[0].Y, maxX = minX, maxY = minY;
            foreach (var p in q)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            return RectangleF.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
        }

        // ------------------------------------------------------------------ dashes

        // Dashes and gaps measured along the path from its start (constant width: the first point's pressure).
        static void Dashes(Graphics g, Color c, float width, InkPoint[] pts, int count, InkMapping m, RectangleF clip, bool solid)
        {
            float w = Math.Max(0.8f, width * InkRenderer.PressureFactor(pts[0].P) * m.Scale);
            using (var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (count == 1 || solid)
                {
                    var all = new PointF[count];
                    for (int i = 0; i < count; i++) all[i] = m.ToTarget(pts[i].X, pts[i].Y);
                    if (count == 1) using (var b = new SolidBrush(c)) g.FillEllipse(b, all[0].X - w / 2, all[0].Y - w / 2, w, w);
                    else g.DrawLines(pen, all);
                    return;
                }
                float dash = width * 2.4f + 2, gap = width * 1.9f + 2;   // canvas units
                bool on = true;
                float left = dash;   // of the current dash or gap
                var piece = new List<PointF> { m.ToTarget(pts[0].X, pts[0].Y) };
                for (int i = 1; i < count; i++)
                {
                    float ax = pts[i - 1].X, ay = pts[i - 1].Y, dx = pts[i].X - ax, dy = pts[i].Y - ay;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy), at = 0;
                    while (len - at >= left)
                    {
                        at += left;
                        PointF p = m.ToTarget(ax + dx * at / len, ay + dy * at / len);
                        if (on) { piece.Add(p); DrawPiece(g, pen, piece, w, clip); }
                        piece.Clear();
                        if (!on) piece.Add(p);
                        on = !on;
                        left = on ? dash : gap;
                    }
                    left -= len - at;
                    if (on) piece.Add(m.ToTarget(pts[i].X, pts[i].Y));
                }
                if (on && piece.Count > 0) DrawPiece(g, pen, piece, w, clip);
            }
        }

        static void DrawPiece(Graphics g, Pen pen, List<PointF> piece, float w, RectangleF clip)
        {
            if (piece.Count == 0) return;
            var arr = piece.ToArray();
            if (!clip.IsEmpty && !Bounds(arr, w).IntersectsWith(clip)) return;
            if (arr.Length == 1) arr = new[] { arr[0], new PointF(arr[0].X + 0.01f, arr[0].Y) };
            g.DrawLines(pen, arr);
        }

        // ------------------------------------------------------------------ caches (per thread: GDI+ objects aren't shared)

        [ThreadStatic] static Dictionary<long, Bitmap> dabs;
        [ThreadStatic] static Dictionary<long, TextureBrush> grains;
        [ThreadStatic] static Dictionary<string, float[]> noise;

        public static void ClearCache()
        {
            if (dabs != null) { foreach (var b in dabs.Values) b.Dispose(); dabs = null; }
            if (grains != null) { foreach (var b in grains.Values) b.Dispose(); grains = null; }
            noise = null;
        }

        // A round dab `size` pixels wide: the color at `level`/128 in the middle, fading smoothly to nothing at the edge.
        static Bitmap Dab(Color c, int size, int level)
        {
            if (dabs == null) dabs = new Dictionary<long, Bitmap>();
            long key = ((long)(uint)(c.ToArgb() & 0xFFFFFF) << 24) | ((long)(uint)size << 8) | (uint)level;
            Bitmap b;
            if (dabs.TryGetValue(key, out b)) return b;
            if (dabs.Count >= 96) { foreach (var old in dabs.Values) old.Dispose(); dabs.Clear(); }
            b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            var px = new int[size * size];
            float rad = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - rad) / rad, dy = (y + 0.5f - rad) / rad, r2 = dx * dx + dy * dy;
                    if (r2 >= 1) continue;
                    float f = (1 - r2) * (1 - r2);
                    int a = (int)Math.Round(f * level * 255 / 128f);
                    if (a <= 0) continue;
                    px[y * size + x] = (a << 24) | ((c.R * a / 255) << 16) | ((c.G * a / 255) << 8) | (c.B * a / 255);
                }
            var data = b.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < size; y++) Marshal.Copy(px, y * size, data.Scan0 + y * data.Stride, size); }
            finally { b.UnlockBits(data); }
            dabs[key] = b;
            return b;
        }

        const int GrainSize = 128;

        // The paper grain in the stroke's color, fixed to the canvas (it scales and moves with the drawing): on where the
        // grain's noise is in the top `density` share, off elsewhere, with a thin soft edge.
        static TextureBrush Grain(string kind, Color c, float density, InkMapping m)
        {
            if (grains == null) grains = new Dictionary<long, TextureBrush>();
            int dq = (int)Math.Round(density * 16);
            long key = ((long)(uint)(c.ToArgb() & 0xFFFFFF) << 16) | ((long)(uint)dq << 4) | (uint)(kind == Pencil ? 1 : kind == Chalk ? 2 : 3);
            TextureBrush brush;
            if (!grains.TryGetValue(key, out brush))
            {
                if (grains.Count >= 24) { foreach (var old in grains.Values) old.Dispose(); grains.Clear(); }
                float[] v = Noise(kind);
                var sorted = (float[])v.Clone();
                Array.Sort(sorted);
                float thr = sorted[Math.Max(0, Math.Min(sorted.Length - 1, (int)((1 - dq / 16f) * sorted.Length)))];
                const float band = 0.015f;
                var px = new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    float f = Math.Max(0, Math.Min(1, (v[i] - thr) / band + 0.5f));
                    int a = (int)(f * 255);
                    px[i] = (a << 24) | (c.R << 16) | (c.G << 8) | c.B;
                }
                using (var bmp = new Bitmap(GrainSize, GrainSize, PixelFormat.Format32bppArgb))
                {
                    var data = bmp.LockBits(new Rectangle(0, 0, GrainSize, GrainSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try { for (int y = 0; y < GrainSize; y++) Marshal.Copy(px, y * GrainSize, data.Scan0 + y * data.Stride, GrainSize); }
                    finally { bmp.UnlockBits(data); }
                    brush = new TextureBrush(bmp, WrapMode.Tile);
                }
                grains[key] = brush;
            }
            // Canvas units per texel: pencil fine, chalk coarse, crayon in slanted streaks.
            float sx = kind == Pencil ? 0.7f : 1.0f, sy = sx;
            var t = new Matrix(sx * m.Scale, 0, 0, sy * m.Scale, m.OffsetX, m.OffsetY);
            if (kind == Crayon) t.Rotate(-18);
            brush.Transform = t;
            t.Dispose();
            return brush;
        }

        // Tileable noise per grain kind, the same everywhere (fixed seeds).
        static float[] Noise(string kind)
        {
            if (noise == null) noise = new Dictionary<string, float[]>();
            float[] v;
            if (noise.TryGetValue(kind, out v)) return v;
            int n = GrainSize;
            v = new float[n * n];
            uint seed = kind == Pencil ? 11u : kind == Chalk ? 23u : 37u;
            for (int i = 0; i < v.Length; i++) v[i] = (Hash(seed * 0x9E3779B9 + (uint)i) & 0xFFFFFF) / 16777216f;
            if (kind == Chalk) v = Blur(v, n, 1, 1);                                                          // small clumps
            else if (kind == Crayon) { v = Blur(v, n, 6, 0); v = Blur(v, n, 0, 1); }                           // streaks
            noise[kind] = v;
            return v;
        }

        // Box blur that wraps around (the texture tiles).
        static float[] Blur(float[] v, int n, int rx, int ry)
        {
            var o = new float[v.Length];
            float k = 1f / ((2 * rx + 1) * (2 * ry + 1));
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float s = 0;
                    for (int dy = -ry; dy <= ry; dy++)
                        for (int dx = -rx; dx <= rx; dx++) s += v[((y + dy + n) % n) * n + (x + dx + n) % n];
                    o[y * n + x] = s * k;
                }
            return o;
        }

        static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352d;
            x ^= x >> 15; x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }
}
