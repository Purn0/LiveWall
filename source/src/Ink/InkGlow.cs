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

        public const int DefaultDim = 85;   // percent: how dark an animated glow gets at its lowest

        // 0..1: the rhythm itself (0 = its darkest moment, 1 = its brightest). Every rhythm is a whole number of cycles
        // per loop; `seed` gives each element (or each speck of a spray) its own moment and pace.
        public static double Shape(string kind, int speed, uint seed, double t)
        {
            int c = speed <= 1 ? 1 : speed == 2 ? 2 : 4;                // cycles per loop
            double x = t / Loop;
            switch (kind)
            {
                case Pulse:
                    // Breathing, all in step (a sign, a heartbeat).
                    return 0.5 - 0.5 * Math.Cos(2 * Math.PI * c * x);
                case Twinkle:
                {
                    // A star: wandering brightness with short, sharp sparkles, each at its own moment and rate.
                    double wander = Noise(seed, x, 4 * c, true);
                    int cs = c * (1 + (int)(Rand(seed, 3) * 3));
                    double sparkle = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * (cs * x + Rand(seed, 1)))), 16);
                    return Math.Min(1, 0.8 * wander + sparkle);
                }
                case Flicker:
                    // A candle or a failing tube: jumpy, with sudden drop-outs.
                    return Noise(seed, x, 14 * c, false);
                default: return 1;
            }
        }

        // Seamless noise over one loop: `knots` random values (some of them dark drop-outs), smooth or straight between.
        static double Noise(uint seed, double x, int knots, bool smooth)
        {
            double k = (x - Math.Floor(x)) * knots, f = k - Math.Floor(k);
            int i0 = (int)Math.Floor(k) % knots, i1 = (i0 + 1) % knots;
            double v0 = Knot(seed, i0), v1 = Knot(seed, i1);
            if (smooth) f = f * f * (3 - 2 * f);
            return v0 + (v1 - v0) * f;
        }

        static double Knot(uint seed, int i)
        {
            double r = Rand(seed, 100 + i);
            return r < 0.15 ? 0 : r;
        }

        // How bright an element (or one spot of it) is now: 1 - dim x (1 - rhythm). Untimed (the editor without its preview,
        // drawings on wallpapers) and steady glows: 1.
        public static float Level(InkStroke s, uint seed)
        {
            if (!Timed || !Animated(s)) return 1;
            return (float)(1 - s.Dim / 100.0 * (1 - Shape(s.Anim, s.Speed, seed, Time)));
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
            public int[] HaloPx, CorePx;   // premultiplied
            public Bitmap Halo, Core;      // the same as pictures, made when first drawn at full strength
            public int[] BodyPx;           // animated: the element with its core over it (the halo is HaloPx)
            public Bitmap Dim;             // one dimmed moment of them, reused
            public double DimTime = -1;    // the moment in Dim (an element touching several redrawn areas is dimmed once)
            public Field Fine, Coarse;     // particle brushes: brightness at FieldTime
            public double FieldTime = -1;
            public Point At;               // target pixels
            public int W, H;
        }

        [ThreadStatic] static Dictionary<string, Made> cache;
        [ThreadStatic] static long cachedPixels;
        [ThreadStatic] static long room;   // what the cache may hold while an animation plays or is encoded (0: the usual 4 M pixels)

        // While an animation plays or is encoded, every moving element stays made: a cache that empties every frame makes
        // them all over again each time (10x slower on a board with a few big pulsing shapes). At most 16 M pixels
        // (~200 MB, only for as long as the animation runs); null = back to the usual size.
        public static void KeepMade(IEnumerable<InkStroke> moving, InkMapping m)
        {
            long need = 0;
            if (moving != null)
                foreach (var s in moving)
                {
                    var b = Rectangle.Round(m.ToTarget(s.Bounds));
                    need += (long)(b.Width + 6) * (b.Height + 6);
                }
            room = Math.Min(16000000L, need + need / 8);
        }

        public static void ClearCache()
        {
            if (cache == null) return;
            foreach (var mk in cache.Values)
            {
                if (mk.Halo != null) mk.Halo.Dispose();
                if (mk.Core != null) mk.Core.Dispose();
                if (mk.Dim != null) mk.Dim.Dispose();
            }
            cache = null;
            cachedPixels = 0;
        }

        // The element with its glow: halo, the element (`draw`), core; animated ones as one moment of their rhythm.
        public static void Draw(Graphics g, InkStroke s, InkMapping m, Action<Graphics, InkMapping> draw)
        {
            if (Timed && Animated(s)) { DrawAnimated(g, s, m, draw); return; }
            Made mk = Make(s, m, draw);
            if (mk != null) Blit(g, mk, mk.Halo ?? (mk.Halo = ToBitmap(mk.HaloPx, mk.W, mk.H)));
            draw(g, m);
            if (mk != null) Blit(g, mk, mk.Core ?? (mk.Core = ToBitmap(mk.CorePx, mk.W, mk.H)));
        }

        static void Blit(Graphics g, Made mk, Bitmap b)
        {
            g.DrawImage(b, new Rectangle(mk.At, b.Size), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel);
        }

        // One moment of an animated element: its whole look (halo, element, core) dimmed by the rhythm. Evenly; or, for
        // sprays, grain and dashes, spot by spot: cells of the canvas have their own phases, blended smoothly between them,
        // so every speck shines and twinkles on its own; the halo follows a coarser field (a cloud that breathes, not a
        // mottled one).
        static void DrawAnimated(Graphics g, InkStroke s, InkMapping m, Action<Graphics, InkMapping> draw)
        {
            Made mk = Make(s, m, draw);
            if (mk == null || mk.BodyPx == null) { draw(g, m); return; }
            if (mk.Dim != null && mk.DimTime == Time) { Blit(g, mk, mk.Dim); return; }
            int w = mk.W, h = mk.H;
            uint seed = InkBrush.Seed(s.Id);
            bool spots = s.Brush != null && InkBrush.Particles(s.Brush);
            int even = (int)(Level(s, seed) * 256);
            Field fine = null, coarse = null;
            if (spots)
            {
                float cell = InkBrush.Cell(s.Brush, s.Width);
                fine = new Field(s, seed, m, mk, cell, 40000);
                coarse = new Field(s, seed ^ 0x5bd1e995u, m, mk, cell * 5, 4000);
            }
            if (mk.Dim == null) mk.Dim = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            int[] body = mk.BodyPx, halo = mk.HaloPx;
            var row = new int[w];
            var d = mk.Dim.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int b = body[o + x], hl = halo[o + x];
                        if ((b | hl) == 0) { row[x] = 0; continue; }
                        int kb = spots ? fine.At(x, y) : even, kh = spots ? coarse.At(x, y) : even;
                        row[x] = Over(Scale(b, kb), Scale(hl, kh));
                    }
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, w);
                }
            }
            finally { mk.Dim.UnlockBits(d); }
            mk.DimTime = Time;
            Blit(g, mk, mk.Dim);
        }

        // One moment of an animated element blended straight into premultiplied pixels `px` (w x h, its top-left at target
        // (ox, oy)), as DrawAnimated would draw it: no GDI+ call per element, which counts on a sky full of stars. False
        // when the element is too big to have been made (the caller draws it the usual way).
        public static bool BlendInto(int[] px, int w, int h, int ox, int oy, InkStroke s, InkMapping m)
        {
            Made mk = Make(s, m, (g, mm) => InkRenderer.DrawElement(g, s, mm, false));
            if (mk == null || mk.BodyPx == null) return false;
            int x0 = Math.Max(mk.At.X, ox), y0 = Math.Max(mk.At.Y, oy), x1 = Math.Min(mk.At.X + mk.W, ox + w), y1 = Math.Min(mk.At.Y + mk.H, oy + h);
            if (x0 >= x1 || y0 >= y1) return true;
            uint seed = InkBrush.Seed(s.Id);
            bool spots = s.Brush != null && InkBrush.Particles(s.Brush);
            int even = (int)(Level(s, seed) * 256);
            if (spots && mk.FieldTime != Time)
            {
                float cell = InkBrush.Cell(s.Brush, s.Width);
                mk.Fine = new Field(s, seed, m, mk, cell, 40000);
                mk.Coarse = new Field(s, seed ^ 0x5bd1e995u, m, mk, cell * 5, 4000);
                mk.FieldTime = Time;
            }
            int[] body = mk.BodyPx, halo = mk.HaloPx;
            int n = x1 - x0;
            for (int y = y0; y < y1; y++)
            {
                int o = (y - mk.At.Y) * mk.W + (x0 - mk.At.X), ly = y - mk.At.Y, t = (y - oy) * w + (x0 - ox);
                for (int i = 0; i < n; i++)
                {
                    int b = body[o + i], hl = halo[o + i];
                    if ((b | hl) == 0) continue;
                    int lx = x0 - mk.At.X + i;
                    int kb = spots ? mk.Fine.At(lx, ly) : even, kh = spots ? mk.Coarse.At(lx, ly) : even;
                    int src = Blend(Scale(b, kb), Scale(hl, kh));
                    if (src != 0) px[t + i] = Blend(src, px[t + i]);
                }
            }
            return true;
        }

        // Premultiplied `p` at k/256 (every channel c * k >> 8, two at a time).
        internal static int Scale(int p, int k)
        {
            if (p == 0 || k >= 256) return p;
            if (k <= 0) return 0;
            uint u = (uint)p, f = (uint)k;
            return (int)(((u & 0x00FF00FF) * f >> 8 & 0x00FF00FF) | (((u >> 8) & 0x00FF00FF) * f & 0xFF00FF00));
        }

        // Brightness over an element's box at this moment: one phase per cell of the canvas (anchored to the canvas, so
        // every screen and the video agree), bilinear in between; at most `maxNodes` cells (bigger cells beyond that).
        sealed class Field
        {
            readonly float[] v;
            readonly int gw;
            readonly int[] ci, cj;
            readonly float[] fi, fj;

            public Field(InkStroke s, uint seed, InkMapping m, Made mk, float cell, int maxNodes)
            {
                int w = mk.W, h = mk.H;
                double u0 = (mk.At.X + 0.5 - m.OffsetX) / m.Scale, v0 = (mk.At.Y + 0.5 - m.OffsetY) / m.Scale;
                double spanU = w / m.Scale, spanV = h / m.Scale;
                double nodes = (spanU / cell + 2) * (spanV / cell + 2);
                if (nodes > maxNodes) cell *= (float)Math.Sqrt(nodes / maxNodes);
                int i0 = (int)Math.Floor(u0 / cell), j0 = (int)Math.Floor(v0 / cell);
                gw = (int)Math.Floor((u0 + spanU) / cell) - i0 + 2;
                int gh = (int)Math.Floor((v0 + spanV) / cell) - j0 + 2;
                v = new float[gw * gh];
                float dim = s.Dim / 100f;
                for (int j = 0; j < gh; j++)
                    for (int i = 0; i < gw; i++)
                    {
                        uint node = seed ^ (uint)((i0 + i) * 73856093) ^ (uint)((j0 + j) * 19349663);
                        v[j * gw + i] = (float)(1 - dim * (1 - Shape(s.Anim, s.Speed, node, Time))) * 256;
                    }
                ci = new int[w]; fi = new float[w];
                for (int x = 0; x < w; x++) { double u = (u0 + x / m.Scale) / cell - i0; ci[x] = (int)u; fi[x] = (float)(u - ci[x]); }
                cj = new int[h]; fj = new float[h];
                for (int y = 0; y < h; y++) { double t = (v0 + y / m.Scale) / cell - j0; cj[y] = (int)t; fj[y] = (float)(t - cj[y]); }
            }

            public int At(int x, int y)
            {
                int a = cj[y] * gw + ci[x];
                float tx = fi[x];
                float top = v[a] + (v[a + 1] - v[a]) * tx, bottom = v[a + gw] + (v[a + gw + 1] - v[a + gw]) * tx;
                return (int)(top + (bottom - top) * fj[y]);
            }
        }

        static Made Make(InkStroke s, InkMapping m, Action<Graphics, InkMapping> draw)
        {
            var inv = CultureInfo.InvariantCulture;
            string key = s.Id + "|" + m.Scale.ToString("R", inv) + "|" + m.OffsetX.ToString("R", inv) + "|" + m.OffsetY.ToString("R", inv);
            if (cache == null) cache = new Dictionary<string, Made>();
            Made mk;
            if (cache.TryGetValue(key, out mk)) return mk;

            float radius = Math.Max(2, Radius(s) * m.Scale);
            RectangleF b = m.ToTarget(s.Bounds);   // includes the halo already
            var box = Rectangle.FromLTRB((int)Math.Floor(b.Left) - 2, (int)Math.Floor(b.Top) - 2, (int)Math.Ceiling(b.Right) + 2, (int)Math.Ceiling(b.Bottom) + 2);
            if (box.Width <= 0 || box.Height <= 0 || (long)box.Width * box.Height > 16000000L) return null;
            int w = box.Width, h = box.Height;
            bool animated = Animated(s);
            var alpha = new int[w * h];
            int[] body = animated ? new int[w * h] : null;
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
                        if (body != null) Array.Copy(row, 0, body, y * w, w);
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
            mk = new Made { HaloPx = hp, CorePx = cp, At = box.Location, W = w, H = h };
            if (body != null)
            {
                // Animated: the element with the core over it, dimmed together; the halo goes under.
                for (int i = 0; i < body.Length; i++) body[i] = Over(cp[i], body[i]);
                mk.BodyPx = body;
            }
            if (cachedPixels + (long)w * h > Math.Max(4000000L, room)) ClearCache();
            if (cache == null) cache = new Dictionary<string, Made>();
            cache[key] = mk;
            cachedPixels += (long)w * h;
            return mk;
        }

        // Premultiplied `src` over `dst`, two channels at a time, rounded: the blends done every frame of an animation.
        internal static int Blend(int src, int dst)
        {
            uint s = (uint)src, d = (uint)dst, k = 255 - (s >> 24);
            if (k == 0 || d == 0) return src;
            if (s == 0) return dst;
            uint rb = (d & 0x00FF00FF) * k + 0x00800080, ag = ((d >> 8) & 0x00FF00FF) * k + 0x00800080;
            return (int)(s + ((rb + (rb >> 8 & 0x00FF00FF)) >> 8 & 0x00FF00FF) + ((ag + (ag >> 8 & 0x00FF00FF)) & 0xFF00FF00));
        }

        // Premultiplied `src` over `dst`.
        internal static int Over(int src, int dst)
        {
            if (src == 0) return dst;
            int sa = (src >> 24) & 0xFF;
            if (sa == 255 || dst == 0) return src;
            int k = 255 - sa;
            int a = sa + ((dst >> 24) & 0xFF) * k / 255, r = ((src >> 16) & 0xFF) + ((dst >> 16) & 0xFF) * k / 255;
            int gg = ((src >> 8) & 0xFF) + ((dst >> 8) & 0xFF) * k / 255, bb = (src & 0xFF) + (dst & 0xFF) * k / 255;
            return (Math.Min(255, a) << 24) | (Math.Min(255, r) << 16) | (Math.Min(255, gg) << 8) | Math.Min(255, bb);
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
            sb.Append("glow2|").Append(style).Append('|').Append(w).Append('x').Append(h).Append('|').Append(header).Append('|').Append(Frames);
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
            var parts = MovingParts(layers, m, w, h);
            if (parts.Count == 0) return false;
            var areas = parts.Select(p => new MovingArea(p.Key, p.Value)).ToList();
            KeepMade(layers.SelectMany(kv => kv.Value).Where(Animated), m);
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
                    long firstFrame = 0;
                    bool ok = MediaWorker.EncodeFrames(outPath, w, h, Fps, Frames, (f, canvas) =>
                    {
                        drawing.Start();
                        Time = f / (double)Fps;
                        if (f == 0)
                            using (var g = Graphics.FromImage(frame))
                            {
                                // The first frame whole (it is also the board picture); then its areas as every later
                                // frame draws them, so the loop joins without a seam.
                                InkRenderer.Prepare(g);
                                g.CompositingMode = CompositingMode.SourceCopy;
                                g.DrawImage(background, full, full, GraphicsUnit.Pixel);
                                g.CompositingMode = CompositingMode.SourceOver;
                                InkRenderer.DrawLayers(g, layers, m, w, h);
                            }
                        foreach (var area in areas) area.Draw(frame, background, m);
                        using (var gc = Graphics.FromImage(canvas))
                        {
                            gc.CompositingMode = CompositingMode.SourceCopy;
                            if (f == 0) gc.DrawImage(frame, full, full, GraphicsUnit.Pixel);
                            else
                                foreach (var area in areas) gc.DrawImage(frame, area.Area, area.Area, GraphicsUnit.Pixel);
                        }
                        drawing.Stop();
                        if (f == 0) firstFrame = drawing.ElapsedMilliseconds;
                    });
                    Log.Info("Board animation: " + parts.Count + " moving area(s), " + parts.Sum(p => p.Key.Width * p.Key.Height) / 1000 + " kpx, drawing " +
                             drawing.ElapsedMilliseconds + " ms of the encode (the first frame " + firstFrame + " ms)");
                    return ok;
                }
            }
            finally
            {
                foreach (var area in areas) area.Dispose();
                Timed = timed;
                Time = time;
                InkRenderer.Linear = linear;
                KeepMade(null, m);
                ClearCache();
            }
        }

        // The areas an animation changes (each animated element's, overlapping ones merged; target pixels), each with
        // the elements touching it: all that has to be drawn again for a new moment.
        public static List<KeyValuePair<Rectangle, List<KeyValuePair<InkLayerInfo, List<InkStroke>>>>> MovingParts(
            List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers, InkMapping m, int w, int h)
        {
            var full = new Rectangle(0, 0, w, h);
            var areas = new List<Rectangle>();
            foreach (var kv in layers)
                foreach (var s in kv.Value)
                    if (Animated(s))
                    {
                        var r = Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(m.ToTarget(s.Bounds)), 2, 2), full);
                        if (r.Width > 0 && r.Height > 0) areas.Add(r);
                    }
            // Each area is drawn whole, so overlapping ones are fine as they are (merging them snowballs into one big box
            // that redraws everything in between); only areas inside another are dropped.
            areas = areas.Where((r, i) => !areas.Where((o, j) => j != i && o.Contains(r) && (o != r || j < i)).Any()).ToList();
            return areas.Select(a => new KeyValuePair<Rectangle, List<KeyValuePair<InkLayerInfo, List<InkStroke>>>>(a,
                layers.Select(kv => new KeyValuePair<InkLayerInfo, List<InkStroke>>(kv.Key,
                    kv.Value.Where(s => Rectangle.Inflate(Rectangle.Round(m.ToTarget(s.Bounds)), 2, 2).IntersectsWith(a)).ToList())).ToList())).ToList();
        }
    }

    // One area an animation changes, ready to draw moment after moment. Per shown layer, the elements touching it in order;
    // the still runs between animated elements are baked once, and what never changes is laid together once: the
    // background with every layer under the first animated one (`under`), and every layer over the last one (`over`).
    // A frame is then `under`, the animated layers blended in plain pixels (animated elements straight from their made
    // pixels, no GDI+), `over`. Exact, as drawing in order would be (all of it is plain premultiplied "over"), except
    // when an eraser comes after an animated element: such a layer is drawn element by element.
    internal sealed class MovingArea : IDisposable
    {
        sealed class Piece
        {
            public List<InkStroke> Still;   // baked once into Px
            public int[] Px;
            public Rectangle Box;           // where Px has anything (area pixels)
            public InkStroke Moving;        // or one animated element
        }

        sealed class Part
        {
            public InkLayerInfo Layer;
            public List<InkStroke> All;     // drawn whole each frame (an eraser after something animated)
            public List<Piece> Pieces;
            public Rectangle Box;           // what the moving part can touch (area pixels)
        }

        public readonly Rectangle Area;
        readonly List<Part> parts = new List<Part>();
        int first, last;                    // parts[first..last] move
        int[] under, over, work, layer;
        Rectangle overBox;
        Bitmap scratch;

        public MovingArea(Rectangle area, List<KeyValuePair<InkLayerInfo, List<InkStroke>>> layers)
        {
            Area = area;
            first = -1;
            last = -1;
            foreach (var kv in layers)
            {
                if (kv.Value.Count == 0 || kv.Key.Opacity <= 0) continue;
                var part = new Part { Layer = kv.Key };
                bool movingSeen = false, direct = false;
                foreach (var s in kv.Value)
                {
                    if (InkGlow.Animated(s)) movingSeen = true;
                    else if (s.Tool == InkTool.Erase && movingSeen) direct = true;
                }
                if (direct) part.All = kv.Value;
                else
                {
                    part.Pieces = new List<Piece>();
                    Piece still = null;
                    foreach (var s in kv.Value)
                    {
                        if (InkGlow.Animated(s)) { still = null; part.Pieces.Add(new Piece { Moving = s }); continue; }
                        if (still == null) { still = new Piece { Still = new List<InkStroke>() }; part.Pieces.Add(still); }
                        still.Still.Add(s);
                    }
                }
                if (movingSeen)
                {
                    if (first < 0) first = parts.Count;
                    last = parts.Count;
                }
                parts.Add(part);
            }
        }

        // Writes the area of `target` (same pixels as `background`) at InkGlow's current moment.
        public void Draw(Bitmap target, Bitmap background, InkMapping m)
        {
            int w = Area.Width, h = Area.Height, n = w * h;
            if (under == null) Prepare(background, m);
            if (work == null) work = new int[n];
            Array.Copy(under, work, n);
            for (int k = first; k >= 0 && k <= last; k++)
            {
                var part = parts[k];
                bool straight = part.All == null && part.Layer.Opacity >= 100;
                int[] into = work;
                Rectangle box = part.Box;
                if (!straight)
                {
                    if (layer == null) layer = new int[n];
                    for (int y = box.Top; y < box.Bottom; y++) Array.Clear(layer, y * w + box.X, box.Width);
                    into = layer;
                }
                if (part.All != null) Bake(part.All, m, layer);
                else
                    foreach (var p in part.Pieces)
                    {
                        if (p.Moving == null) { Lay(into, p.Px, p.Box, 256); continue; }
                        if (!InkGlow.BlendInto(into, w, h, Area.X, Area.Y, p.Moving, m))
                        {
                            // Too big to have been made: drawn the usual way.
                            var one = new int[n];
                            Bake(new List<InkStroke> { p.Moving }, m, one);
                            Lay(into, one, new Rectangle(0, 0, w, h), 256);
                        }
                    }
                if (!straight) Lay(work, layer, box, part.Layer.Opacity * 256 / 100);
            }
            if (over != null) Lay(work, over, overBox, 256);
            var d = target.LockBits(Area, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(work, y * w, d.Scan0 + y * d.Stride, w);
            }
            finally { target.UnlockBits(d); }
        }

        // Once: the background with the layers under the moving ones, the layers over them, the still runs between.
        void Prepare(Bitmap background, InkMapping m)
        {
            int w = Area.Width, h = Area.Height, n = w * h;
            var whole = new Rectangle(0, 0, w, h);
            under = new int[n];
            var d = background.LockBits(Area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, under, y * w, w);
            }
            finally { background.UnlockBits(d); }
            if (first < 0) { first = parts.Count; last = parts.Count - 1; }
            var px = new int[n];
            for (int k = 0; k < first; k++)
            {
                Bake(Strokes(parts[k]), m, px);
                Lay(under, px, whole, parts[k].Layer.Opacity * 256 / 100);
            }
            // A plain first moving layer: its still start goes under too; a plain last one: its still end goes over.
            if (first <= last && parts[first].All == null && parts[first].Layer.Opacity >= 100 && parts[first].Pieces[0].Still != null)
            {
                Bake(parts[first].Pieces[0].Still, m, px);
                Lay(under, px, whole, 256);
                parts[first].Pieces.RemoveAt(0);
            }
            var top = new List<KeyValuePair<List<InkStroke>, int>>();
            if (first <= last && parts[last].All == null && parts[last].Layer.Opacity >= 100 && parts[last].Pieces.Count > 0 &&
                parts[last].Pieces[parts[last].Pieces.Count - 1].Still != null)
            {
                top.Add(new KeyValuePair<List<InkStroke>, int>(parts[last].Pieces[parts[last].Pieces.Count - 1].Still, 100));
                parts[last].Pieces.RemoveAt(parts[last].Pieces.Count - 1);
            }
            for (int k = last + 1; k < parts.Count; k++) top.Add(new KeyValuePair<List<InkStroke>, int>(Strokes(parts[k]), parts[k].Layer.Opacity));
            if (top.Count > 0)
            {
                over = new int[n];
                foreach (var t in top)
                {
                    Bake(t.Key, m, px);
                    Lay(over, px, whole, t.Value * 256 / 100);
                }
                overBox = Used(over, w, h);
                if (overBox.IsEmpty) over = null;
            }
            for (int k = first; k <= last; k++)
            {
                var part = parts[k];
                if (part.All != null) { part.Box = whole; continue; }
                Rectangle box = Rectangle.Empty;
                foreach (var p in part.Pieces)
                {
                    Rectangle b;
                    if (p.Moving != null)
                    {
                        b = Rectangle.Inflate(Rectangle.Round(m.ToTarget(p.Moving.Bounds)), 3, 3);
                        b.Offset(-Area.X, -Area.Y);
                        b.Intersect(whole);
                    }
                    else
                    {
                        p.Px = new int[n];
                        Bake(p.Still, m, p.Px);
                        b = p.Box = Used(p.Px, w, h);
                        if (b.IsEmpty) p.Px = null;
                    }
                    if (!b.IsEmpty) box = box.IsEmpty ? b : Rectangle.Union(box, b);
                }
                part.Pieces.RemoveAll(p => p.Moving == null && p.Px == null);
                part.Box = box;
            }
            if (scratch != null) { scratch.Dispose(); scratch = null; }
        }

        static List<InkStroke> Strokes(Part part)
        {
            if (part.All != null) return part.All;
            var list = new List<InkStroke>();
            foreach (var p in part.Pieces)
                if (p.Moving != null) list.Add(p.Moving);
                else list.AddRange(p.Still);
            return list;
        }

        // The elements drawn (GDI+) on nothing, into `px` (area pixels).
        void Bake(List<InkStroke> strokes, InkMapping m, int[] px)
        {
            int w = Area.Width, h = Area.Height;
            if (scratch == null) scratch = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(scratch))
            {
                g.Clear(Color.Transparent);
                InkRenderer.Prepare(g);
                g.TranslateTransform(-Area.X, -Area.Y);
                InkRenderer.DrawStrokes(g, strokes, m);
            }
            var d = scratch.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w, w);
            }
            finally { scratch.UnlockBits(d); }
        }

        // `src` over `dst` in `box`, at `k`/256 strength.
        void Lay(int[] dst, int[] src, Rectangle box, int k)
        {
            if (k <= 0) return;
            int w = Area.Width;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int i = y * w + box.Left, end = y * w + box.Right; i < end; i++)
                {
                    int p = src[i];
                    if (p == 0) continue;
                    dst[i] = InkGlow.Blend(k >= 256 ? p : InkGlow.Scale(p, k), dst[i]);
                }
        }

        static Rectangle Used(int[] px, int w, int h)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0, o = y * w; x < w; x++)
                    if (px[o + x] != 0)
                    {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; y1 = y;
                    }
            return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        public void Dispose()
        {
            if (scratch != null) { scratch.Dispose(); scratch = null; }
            under = over = work = layer = null;
            foreach (var part in parts)
                if (part.Pieces != null)
                    foreach (var p in part.Pieces) p.Px = null;
        }
    }
}
