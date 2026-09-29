using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LiveWall.Ink
{
    // Emissive elements (pens with any brush, shapes, text): `glow=<1-100>` on the element, optionally animated
    // (`anim=pulse|twinkle|flicker`, `speed=1-3`). Drawn as a soft halo in the element's color under it and a hot, lighter
    // core over it, both made once per element and scale from its own pixels (blurred) and cached; the animation only
    // changes how strongly they are laid over (`Level`).
    //
    // Everything moves on one seamless loop of `Loop` seconds: every element's rhythm is a whole number of cycles per
    // loop, with its phase and variation seeded from its id, so the same drawing always animates the same way and stars
    // twinkle out of step. A board with animated glow becomes a looping video (RenderAnimation) played like any video
    // wallpaper; frame 0 is exactly the board picture Windows shows while it is paused.
    internal static class InkGlow
    {
        public const string Steady = "", Pulse = "pulse", Twinkle = "twinkle", Flicker = "flicker";
        public static readonly string[] Kinds = { Steady, Pulse, Twinkle, Flicker };
        public const double Loop = 6.0;
        public const int Fps = 24, Frames = 144;   // Loop * Fps

        // Drawing one moment of the animation (the board picture: t = 0; video frames); otherwise glows are at full
        // strength (the editor, drawings on wallpapers).
        [ThreadStatic] public static bool Timed;
        [ThreadStatic] public static double Time;

        public static bool Applies(InkStroke s) { return s.Tool == InkTool.Pen || s.IsShape || s.Tool == InkTool.Text; }
        public static bool Animated(InkStroke s) { return s.Glow > 0 && !string.IsNullOrEmpty(s.Anim) && Array.IndexOf(Kinds, s.Anim) > 0; }

        public static string Title(string kind)
        {
            switch (kind)
            {
                case Pulse: return "Pulse";
                case Twinkle: return "Twinkle";
                case Flicker: return "Flicker";
                default: return "Steady";
            }
        }

        // How far the halo reaches beyond the element (canvas units).
        public static float Radius(InkStroke s)
        {
            if (s.Glow <= 0) return 0;
            float w = s.Tool == InkTool.Text ? s.Width * 0.35f : s.Width * (s.Tool == InkTool.Pen && s.HasPressure ? 1.2f : 1f);
            return (6 + w * 1.6f) * (0.45f + s.Glow / 90f);
        }

        // 0..1: how strongly the glow shows now.
        public static float Level(InkStroke s)
        {
            if (!Timed || !Animated(s)) return 1;
            return Level(s.Anim, s.Speed, InkBrush.Seed(s.Id), Time);
        }

        public static float Level(string kind, int speed, uint seed, double t)
        {
            int c = speed <= 1 ? 1 : speed == 2 ? 2 : 4;                // cycles per loop
            double x = t / Loop;
            double p1 = Rand(seed, 1), p2 = Rand(seed, 2), p3 = Rand(seed, 3);
            switch (kind)
            {
                case Pulse:
                    // Slow breathing, all in step (a sign, a heartbeat).
                    return (float)(0.3 + 0.7 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * c * x)));
                case Twinkle:
                {
                    // Mostly calm with short bright sparkles; each star at its own rate and moment.
                    int c1 = c * (1 + (int)(p3 * 2)), c2 = c1 * 2 + 1;
                    double a = Math.Max(0, Math.Sin(2 * Math.PI * (c1 * x + p1)));
                    double b = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (c2 * x + p2));
                    return (float)(0.22 + 0.78 * Math.Pow(a, 3) * (0.55 + 0.45 * b));
                }
                case Flicker:
                {
                    // Irregular, fast (a candle or an old neon tube): smooth noise with 10 knots per cycle.
                    int knots = 10 * c;
                    double k = x * knots, f = k - Math.Floor(k);
                    int i0 = (int)Math.Floor(k) % knots, i1 = (i0 + 1) % knots;
                    double v0 = Rand(seed, 100 + i0), v1 = Rand(seed, 100 + i1), sm = f * f * (3 - 2 * f);
                    return (float)(0.45 + 0.55 * (v0 + (v1 - v0) * sm));
                }
                default: return 1;
            }
        }

        static double Rand(uint seed, int i)
        {
            uint h = seed ^ (uint)(i * 0x9E3779B9);
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216.0;
        }

        // ------------------------------------------------------------------ drawing

        sealed class Made
        {
            public Bitmap Halo, Core;
            public int[] HaloPx, CorePx;   // the same, premultiplied, for dimmed copies
            public Bitmap Dim;             // a dimmed copy, reused
            public Point At;               // target pixels
        }

        [ThreadStatic] static Dictionary<string, Made> cache;
        [ThreadStatic] static long cachedPixels;

        public static void ClearCache()
        {
            if (cache == null) return;
            foreach (var mk in cache.Values) { mk.Halo.Dispose(); mk.Core.Dispose(); if (mk.Dim != null) mk.Dim.Dispose(); }
            cache = null;
            cachedPixels = 0;
        }

        // The element with its glow: halo, the element (`draw`), core.
        public static void Draw(Graphics g, InkStroke s, InkMapping m, Action<Graphics, InkMapping> draw)
        {
            float level = Level(s);
            Made mk = level > 0.004f ? Make(s, m, draw) : null;
            if (mk != null) Lay(g, mk, false, level);
            draw(g, m);
            if (mk != null) Lay(g, mk, true, level);
        }

        // Dimmed by `level` with a plain loop into a reused bitmap (GDI+'s color matrix is many times slower), then
        // copied like any picture.
        static void Lay(Graphics g, Made mk, bool core, float level)
        {
            Bitmap src = core ? mk.Core : mk.Halo;
            var r = new Rectangle(mk.At, src.Size);
            if (level >= 0.996f) { g.DrawImage(src, r, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel); return; }
            int w = src.Width, h = src.Height, k = (int)(level * 256);
            int[] px = core ? mk.CorePx : mk.HaloPx;
            if (mk.Dim == null) mk.Dim = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var row = new int[w];
            var d = mk.Dim.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int p = px[o + x];
                        row[x] = p == 0 ? 0 : ((((p >> 24) & 0xFF) * k >> 8) << 24) | ((((p >> 16) & 0xFF) * k >> 8) << 16) | ((((p >> 8) & 0xFF) * k >> 8) << 8) | ((p & 0xFF) * k >> 8);
                    }
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, w);
                }
            }
            finally { mk.Dim.UnlockBits(d); }
            g.DrawImage(mk.Dim, r, 0, 0, w, h, GraphicsUnit.Pixel);
        }

        static Made Make(InkStroke s, InkMapping m, Action<Graphics, InkMapping> draw)
        {
            var ci = CultureInfo.InvariantCulture;
            string key = s.Id + "|" + m.Scale.ToString("R", ci) + "|" + m.OffsetX.ToString("R", ci) + "|" + m.OffsetY.ToString("R", ci);
            if (cache == null) cache = new Dictionary<string, Made>();
            Made mk;
            if (cache.TryGetValue(key, out mk)) return mk;

            float radius = Math.Max(2, Radius(s) * m.Scale);
            RectangleF b = m.ToTarget(s.Bounds);   // includes the halo already
            var box = Rectangle.FromLTRB((int)Math.Floor(b.Left) - 2, (int)Math.Floor(b.Top) - 2, (int)Math.Ceiling(b.Right) + 2, (int)Math.Ceiling(b.Bottom) + 2);
            if (box.Width <= 0 || box.Height <= 0 || (long)box.Width * box.Height > 16000000L) return null;
            int w = box.Width, h = box.Height;
            var alpha = new int[w * h];
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var gb = Graphics.FromImage(bmp))
                {
                    InkRenderer.Prepare(gb);
                    gb.TranslateTransform(-box.X, -box.Y);
                    draw(gb, m);
                }
                var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new int[w];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, w);
                        for (int x = 0; x < w; x++) alpha[y * w + x] = (row[x] >> 24) & 0xFF;
                    }
                }
                finally { bmp.UnlockBits(d); }
            }
            // Bloom: a tight bright ring and a wide soft one.
            int[] inner = Blur(alpha, w, h, Math.Max(1, (int)(radius / 4))), outer = Blur(alpha, w, h, Math.Max(1, (int)(radius / 2)));
            float strength = 0.35f + 0.65f * s.Glow / 100f;
            Color c = Color.FromArgb(255, Color.FromArgb(s.Argb));
            Color halo = Mix(c, Color.White, 0.15f), hot = Mix(c, Color.White, 0.7f);
            var hp = new int[w * h];
            var cp = new int[w * h];
            for (int i = 0; i < hp.Length; i++)
            {
                float a = Math.Min(1f, (inner[i] * 1.7f + outer[i] * 1.4f) / 255f) * strength;
                int ai = (int)(a * 255);
                if (ai > 0) hp[i] = (ai << 24) | ((halo.R * ai / 255) << 16) | ((halo.G * ai / 255) << 8) | (halo.B * ai / 255);
                int ac = (int)(alpha[i] * 0.55f * strength);
                if (ac > 0) cp[i] = (ac << 24) | ((hot.R * ac / 255) << 16) | ((hot.G * ac / 255) << 8) | (hot.B * ac / 255);
            }
            mk = new Made { Halo = ToBitmap(hp, w, h), Core = ToBitmap(cp, w, h), HaloPx = hp, CorePx = cp, At = box.Location };
            if (cachedPixels + (long)w * h > 6000000L) ClearCache();
            if (cache == null) cache = new Dictionary<string, Made>();
            cache[key] = mk;
            cachedPixels += (long)w * h;
            return mk;
        }

        static Bitmap ToBitmap(int[] px, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, d.Scan0 + y * d.Stride, w); }
            finally { b.UnlockBits(d); }
            return b;
        }

        // Three box blurs across and down: close to a Gaussian, linear time whatever the radius.
        static int[] Blur(int[] src, int w, int h, int r)
        {
            var a = (int[])src.Clone();
            var tmp = new int[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxRows(a, tmp, w, h, r);
                BoxCols(tmp, a, w, h, r);
            }
            return a;
        }

        static void BoxRows(int[] src, int[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, sum = 0;
                for (int x = -r; x <= r; x++) if (x >= 0 && x < w) sum += src[row + x];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = sum / div;
                    int add = x + r + 1, sub = x - r;
                    if (add < w) sum += src[row + add];
                    if (sub >= 0) sum -= src[row + sub];
                }
            }
        }

        static void BoxCols(int[] src, int[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1;
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int y = -r; y <= r; y++) if (y >= 0 && y < h) sum += src[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum / div;
                    int add = y + r + 1, sub = y - r;
                    if (add < h) sum += src[add * w + x];
                    if (sub >= 0) sum -= src[sub * w + x];
                }
            }
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ------------------------------------------------------------------ the board's loop as a video

        public static bool AnyAnimated(List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers)
        {
            return layers.Any(kv => kv.Value.Any(Animated));
        }

        // Names the video by what is in it (elements never change: their ids say it all) and how it is drawn.
        public static string ContentKey(List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers, string style, int w, int h, string header)
        {
            var sb = new StringBuilder();
            sb.Append(style).Append('|').Append(w).Append('x').Append(h).Append('|').Append(header).Append('|').Append(Frames);
            foreach (var kv in layers)
            {
                sb.Append('#').Append(kv.Key.Id).Append(':').Append(kv.Key.Opacity);
                foreach (var s in kv.Value) sb.Append(',').Append(s.Id);
            }
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(20);
                for (int i = 0; i < 10; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString();
            }
        }

        // Renders and encodes the loop (runs on the worker thread). Frame 0 is drawn whole; after that only the areas of
        // animated glows are drawn again, with the elements under and over them, so a few twinkling stars cost little.
        public static bool RenderAnimation(string style, List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers, int canvasW, int canvasH,
                                           int w, int h, string header, int seed, string outPath)
        {
            w &= ~1; h &= ~1;   // H.264 wants even sizes
            var m = InkMapping.Fill(canvasW, canvasH, w, h);
            var full = new Rectangle(0, 0, w, h);
            // The areas that change: each animated element's, overlapping ones merged.
            var areas = new List<Rectangle>();
            foreach (var kv in layers)
                foreach (var s in kv.Value)
                    if (Animated(s))
                    {
                        var r = Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(m.ToTarget(s.Bounds)), 2, 2), full);
                        if (r.Width > 0 && r.Height > 0) areas.Add(r);
                    }
            if (areas.Count == 0) return false;
            for (bool merged = true; merged; )
            {
                merged = false;
                for (int i = 0; i < areas.Count && !merged; i++)
                    for (int j = i + 1; j < areas.Count && !merged; j++)
                        if (areas[i].IntersectsWith(areas[j])) { areas[i] = Rectangle.Union(areas[i], areas[j]); areas.RemoveAt(j); merged = true; }
            }
            // Per area, what the frames after the first redraw there: the elements touching it.
            var parts = areas.Select(a => new KeyValuePair<Rectangle, List<KeyValuePair<InkLayerInfo, List<InkStroke>>>>(a,
                layers.Select(kv => new KeyValuePair<InkLayerInfo, List<InkStroke>>(kv.Key,
                    kv.Value.Where(s => s.Tool == InkTool.Erase || Rectangle.Round(m.ToTarget(s.Bounds)).IntersectsWith(a)).ToList())).ToList())).ToList();
            bool timed = Timed, linear = InkRenderer.Linear;
            double time = Time;
            InkRenderer.Linear = true;
            try
            {
                using (var background = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (var frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(background))
                    {
                        InkRenderer.Prepare(g);
                        InkRenderer.DrawBackground(g, style, full, m, header, seed);
                    }
                    Timed = true;
                    var drawing = new System.Diagnostics.Stopwatch();
                    bool ok = MediaWorker.EncodeFrames(outPath, w, h, Fps, Frames, (f, canvas) =>
                    {
                        drawing.Start();
                        Time = f / (double)Fps;
                        var todo = f == 0 ? new[] { new KeyValuePair<Rectangle, List<KeyValuePair<InkLayerInfo, List<InkStroke>>>>(full, layers) }.ToList() : parts;
                        using (var g = Graphics.FromImage(frame))
                        using (var gc = Graphics.FromImage(canvas))
                        {
                            InkRenderer.Prepare(g);
                            gc.CompositingMode = CompositingMode.SourceCopy;
                            foreach (var part in todo)
                            {
                                Rectangle r = part.Key;
                                g.SetClip(r);
                                g.CompositingMode = CompositingMode.SourceCopy;
                                g.DrawImage(background, r, r, GraphicsUnit.Pixel);
                                g.CompositingMode = CompositingMode.SourceOver;
                                InkRenderer.DrawLayers(g, part.Value, m, w, h, f == 0 ? Rectangle.Empty : r);
                                gc.DrawImage(frame, r, r, GraphicsUnit.Pixel);
                            }
                        }
                        drawing.Stop();
                    });
                    Log.Info("Board animation: " + parts.Count + " moving area(s), " + parts.Sum(p => p.Key.Width * p.Key.Height) / 1000 + " kpx, drawing " +
                             drawing.ElapsedMilliseconds + " ms of the encode");
                    return ok;
                }
            }
            finally
            {
                Timed = timed;
                Time = time;
                InkRenderer.Linear = linear;
                ClearCache();
            }
        }
    }
}
