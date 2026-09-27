using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LiveWall.Ink
{
    // Pictures inside a drawing (image elements): PNG in base64 in the drawing's file, decoded on demand into a small
    // per-thread cache that InkText.ClearCache releases with the text cache.
    internal static class InkImage
    {
        [ThreadStatic] static Dictionary<string, Bitmap> cache;

        public static Bitmap Decode(InkStroke s)
        {
            if (string.IsNullOrEmpty(s.Data)) return null;
            if (cache == null) cache = new Dictionary<string, Bitmap>();
            Bitmap b;
            if (cache.TryGetValue(s.Id, out b)) return b;
            try
            {
                using (var ms = new MemoryStream(Convert.FromBase64String(s.Data)))
                using (var raw = new Bitmap(ms))
                    b = ToPArgb(raw);
            }
            catch (Exception ex) { Log.Warn("Picture in a drawing could not be read: " + ex.Message); b = null; }
            if (cache.Count >= 24) ClearCache();
            cache[s.Id] = b;
            return b;
        }

        public static void ClearCache()
        {
            if (cache == null) return;
            foreach (var b in cache.Values) if (b != null) b.Dispose();
            cache = null;
        }

        public static Bitmap ToPArgb(Image img)
        {
            var b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
            }
            return b;
        }

        public static string Encode(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        public static void Draw(Graphics g, InkStroke s, InkMapping m)
        {
            if (s.Points.Length < 3) return;
            Bitmap b = Decode(s);
            if (b == null) return;
            var dst = new[] { m.ToTarget(s.Points[0].X, s.Points[0].Y), m.ToTarget(s.Points[1].X, s.Points[1].Y), m.ToTarget(s.Points[2].X, s.Points[2].Y) };
            DrawOn(g, b, dst, true);
        }

        // `corners`: where the image's top-left, top-right and bottom-left go.
        public static void DrawOn(Graphics g, Bitmap b, PointF[] corners, bool best)
        {
            var state = g.Save();
            // Bilinear, not bicubic: bicubic overshoots at hard edges and leaves a light halo on transparent pixels.
            g.InterpolationMode = best ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceOver;
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);   // no faded border
                g.DrawImage(b, corners, new RectangleF(0, 0, b.Width, b.Height), GraphicsUnit.Pixel, ia);
            }
            g.Restore(state);
        }
    }
}
