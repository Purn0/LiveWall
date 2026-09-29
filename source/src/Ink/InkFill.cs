using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveWall.Ink
{
    // Paint-bucket fills: the area around a point that is enclosed by solid ink (pens, shapes, text, pictures; not
    // highlighters, earlier fills, backgrounds or wallpapers), kept as a region so it looks the same at any screen size.
    internal static class InkFill
    {
        // A region in canvas pixels: per row, runs of [start, end) relative to Left.
        internal sealed class Mask
        {
            public int Left, Top, Width, Height;
            public int[][] Rows;

            public bool Contains(float x, float y)
            {
                int ix = (int)Math.Floor(x) - Left, iy = (int)Math.Floor(y) - Top;
                if (iy < 0 || iy >= Height || ix < 0 || ix >= Width) return false;
                int[] runs = Rows[iy];
                for (int i = 0; i + 1 < runs.Length; i += 2) if (ix >= runs[i] && ix < runs[i + 1]) return true;
                return false;
            }

            // "left,top,width,height:" then rows separated by '/', runs "a-b" separated by '.', "*n" repeats a row n times.
            public string Serialize()
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(64 + Height * 8);
                sb.Append(Left.ToString(ci)).Append(',').Append(Top.ToString(ci)).Append(',').Append(Width.ToString(ci)).Append(',')
                  .Append(Height.ToString(ci)).Append(':');
                string prev = null;
                int repeat = 0;
                bool first = true;
                for (int y = 0; y <= Height; y++)
                {
                    string row = y < Height ? RowText(Rows[y]) : null;
                    if (row != null && row == prev) { repeat++; continue; }
                    if (prev != null)
                    {
                        if (!first) sb.Append('/');
                        sb.Append(prev);
                        if (repeat > 1) sb.Append('*').Append(repeat.ToString(ci));
                        first = false;
                    }
                    prev = row;
                    repeat = 1;
                }
                return sb.ToString();
            }

            static string RowText(int[] runs)
            {
                var sb = new StringBuilder();
                for (int i = 0; i + 1 < runs.Length; i += 2)
                {
                    if (i > 0) sb.Append('.');
                    sb.Append(runs[i].ToString(CultureInfo.InvariantCulture)).Append('-').Append(runs[i + 1].ToString(CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }

            public static Mask Parse(string s)
            {
                try
                {
                    int colon = s.IndexOf(':');
                    string[] head = s.Substring(0, colon).Split(',');
                    var ci = CultureInfo.InvariantCulture;
                    var m = new Mask
                    {
                        Left = int.Parse(head[0], ci), Top = int.Parse(head[1], ci), Width = int.Parse(head[2], ci), Height = int.Parse(head[3], ci)
                    };
                    if (m.Width <= 0 || m.Height <= 0 || m.Width > 32768 || m.Height > 32768) return null;
                    var rows = new List<int[]>(m.Height);
                    foreach (string part in s.Substring(colon + 1).Split('/'))
                    {
                        int star = part.IndexOf('*');
                        string row = star >= 0 ? part.Substring(0, star) : part;
                        int n = star >= 0 ? int.Parse(part.Substring(star + 1), ci) : 1;
                        var runs = new List<int>();
                        if (row.Length > 0)
                            foreach (string r in row.Split('.'))
                            {
                                int dash = r.IndexOf('-');
                                runs.Add(int.Parse(r.Substring(0, dash), ci));
                                runs.Add(int.Parse(r.Substring(dash + 1), ci));
                            }
                        int[] arr = runs.ToArray();
                        for (int i = 0; i < n && rows.Count < m.Height; i++) rows.Add(arr);
                    }
                    while (rows.Count < m.Height) rows.Add(new int[0]);
                    m.Rows = rows.ToArray();
                    return m;
                }
                catch { return null; }
            }
        }

        // `ink`: per canvas pixel, true where solid drawing blocks the fill. `gap` (canvas pixels, 0 = none): the flood runs
        // on the ink grown by that much, so gaps in an outline up to about twice as wide are closed and thin anti-aliased
        // lines can't leak; the result then grows back by `gap` (never across a line) to meet the ink. Returns null when
        // (sx, sy) is on ink.
        public static Mask Flood(bool[] ink, int w, int h, int sx, int sy, int gap)
        {
            if (sx < 0 || sy < 0 || sx >= w || sy >= h || ink[sy * w + sx]) return null;
            int start = sy * w + sx;
            bool[] blocked = ink;
            if (gap > 0)
            {
                blocked = Dilate(ink, w, h, gap, new Rectangle(0, 0, w, h));
                if (blocked[start])
                {
                    // Clicked close to a line: start from the nearest open pixel on this side of it, or (a sliver narrower
                    // than the gap) fill without closing gaps.
                    int open = NearestOpen(ink, blocked, w, h, start, 2 * gap + 2);
                    if (open >= 0) start = open;
                    else { blocked = ink; gap = 0; }
                }
            }
            Rectangle box;
            bool[] filled = FloodFrom(blocked, w, h, start, out box);
            if (gap > 0)
            {
                box = Rectangle.Intersect(Rectangle.Inflate(box, gap, gap), new Rectangle(0, 0, w, h));
                filled = Dilate(filled, w, h, gap, box);
            }
            // One pixel wider into the surrounding ink, so no gap shows along anti-aliased edges.
            int minX = Math.Max(0, box.Left - 1), minY = Math.Max(0, box.Top - 1), maxX = Math.Min(w - 1, box.Right), maxY = Math.Min(h - 1, box.Bottom);
            var m = new Mask { Left = minX, Top = minY, Width = maxX - minX + 1, Height = maxY - minY + 1 };
            m.Rows = new int[m.Height][];
            var runs = new List<int>();
            for (int y = minY; y <= maxY; y++)
            {
                runs.Clear();
                int from = -1;
                for (int x = minX; x <= maxX + 1; x++)
                {
                    bool on = x <= maxX && Grown(filled, w, h, x, y);
                    if (on && from < 0) from = x;
                    else if (!on && from >= 0) { runs.Add(from - minX); runs.Add(x - minX); from = -1; }
                }
                m.Rows[y - minY] = runs.ToArray();
            }
            return m;
        }

        // Scanline flood over the pixels that aren't blocked; `box` = the filled area's bounds.
        static bool[] FloodFrom(bool[] blocked, int w, int h, int start, out Rectangle box)
        {
            var filled = new bool[w * h];
            var stack = new Stack<int>();
            stack.Push(start);
            int minX = start % w, maxX = minX, minY = start / w, maxY = minY;
            var rows = new int[2];
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (filled[i] || blocked[i]) continue;
                int y = i / w, x0 = i % w, x1 = x0;
                while (x0 > 0 && !blocked[y * w + x0 - 1] && !filled[y * w + x0 - 1]) x0--;
                while (x1 < w - 1 && !blocked[y * w + x1 + 1] && !filled[y * w + x1 + 1]) x1++;
                for (int x = x0; x <= x1; x++) filled[y * w + x] = true;
                if (x0 < minX) minX = x0; if (x1 > maxX) maxX = x1;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                rows[0] = y - 1;
                rows[1] = y + 1;
                foreach (int ny in rows)
                {
                    if (ny < 0 || ny >= h) continue;
                    bool open = false;
                    for (int x = x0; x <= x1; x++)
                    {
                        int j = ny * w + x;
                        bool free = !blocked[j] && !filled[j];
                        if (free && !open) stack.Push(j);
                        open = free;
                    }
                }
            }
            box = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            return filled;
        }

        // A copy of `src` grown by r pixels in every direction (a square), computed only inside `area`.
        static bool[] Dilate(bool[] src, int w, int h, int r, Rectangle area)
        {
            var dst = new bool[w * h];
            var tmp = new bool[w * h];
            int x0 = area.Left, x1 = area.Right;
            var prefix = new int[w + 1];
            // Across: tmp = any src within r to the left or right (rows the second pass reads).
            for (int y = Math.Max(0, area.Top - r); y < Math.Min(h, area.Bottom + r); y++)
            {
                int row = y * w, from = Math.Max(0, x0 - r), to = Math.Min(w, x1 + r);
                for (int x = from; x < to; x++) prefix[x + 1] = prefix[x] + (src[row + x] ? 1 : 0);
                for (int x = x0; x < x1; x++) tmp[row + x] = prefix[Math.Min(to, x + r + 1)] - prefix[Math.Max(from, x - r)] > 0;
            }
            // Down: a sliding window of 2r+1 rows, counted per column.
            var count = new int[w];
            for (int y = Math.Max(0, area.Top - r); y < Math.Min(h, area.Top + r); y++) AddRow(tmp, count, y * w, x0, x1, 1);
            for (int y = area.Top; y < area.Bottom; y++)
            {
                if (y + r < h) AddRow(tmp, count, (y + r) * w, x0, x1, 1);
                int row = y * w;
                for (int x = x0; x < x1; x++) dst[row + x] = count[x] > 0;
                if (y - r >= 0) AddRow(tmp, count, (y - r) * w, x0, x1, -1);
            }
            return dst;
        }

        static void AddRow(bool[] b, int[] count, int row, int x0, int x1, int d)
        {
            for (int x = x0; x < x1; x++) if (b[row + x]) count[x] += d;
        }

        // The nearest pixel that isn't blocked, reached without crossing ink (4-neighbour steps), or -1.
        static int NearestOpen(bool[] ink, bool[] blocked, int w, int h, int start, int maxSteps)
        {
            var seen = new HashSet<int> { start };
            var ring = new List<int> { start };
            for (int step = 0; step < maxSteps && ring.Count > 0; step++)
            {
                var next = new List<int>();
                foreach (int i in ring)
                {
                    int x = i % w, y = i / w;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (ink[j] || !seen.Add(j)) continue;
                        if (!blocked[j]) return j;
                        next.Add(j);
                    }
                }
                ring = next;
            }
            return -1;
        }

        static bool Grown(bool[] f, int w, int h, int x, int y)
        {
            int i = y * w + x;
            return f[i] || (x > 0 && f[i - 1]) || (x < w - 1 && f[i + 1]) || (y > 0 && f[i - w]) || (y < h - 1 && f[i + w]);
        }

        public static void Draw(Graphics g, InkStroke s, InkMapping m)
        {
            Mask mask = s.Mask;
            if (mask == null) return;
            Color c = Color.FromArgb(255, Color.FromArgb(s.Argb));
            using (var bmp = new Bitmap(mask.Width, mask.Height, PixelFormat.Format32bppPArgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, mask.Width, mask.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new int[mask.Width];
                    int px = c.ToArgb();
                    for (int y = 0; y < mask.Height; y++)
                    {
                        Array.Clear(row, 0, row.Length);
                        int[] runs = mask.Rows[y];
                        for (int i = 0; i + 1 < runs.Length; i += 2)
                            for (int x = Math.Max(0, runs[i]); x < Math.Min(mask.Width, runs[i + 1]); x++) row[x] = px;
                        Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, mask.Width);
                    }
                }
                finally { bmp.UnlockBits(data); }

                RectangleF dst = m.ToTarget(new RectangleF(mask.Left, mask.Top, mask.Width, mask.Height));
                var state = g.Save();
                bool exact = Math.Abs(m.Scale - 1) < 0.001f;
                g.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(bmp, new[] { dst.Location, new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) },
                                new RectangleF(0, 0, mask.Width, mask.Height), GraphicsUnit.Pixel, ia);
                }
                g.Restore(state);
            }
        }

    }
}
