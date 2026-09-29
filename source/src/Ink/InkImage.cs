using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

        // ------------------------------------------------------------------ pictures from the clipboard

        static readonly string[] GdiFiles = { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico" };

        // A picture on the clipboard: PNG (keeps transparency: browsers, Snipping Tool, Office, our own copies), a 32-bit
        // DIBV5 with alpha, an image FILE copied in File Explorer (the first one that reads), or a plain bitmap (Paint).
        // Bigger than maxW x maxH: scaled down. Null = nothing usable; `problem` then says why, if there was something.
        public static Bitmap FromClipboard(int maxW, int maxH, out string problem)
        {
            problem = null;
            IDataObject data = null;
            for (int i = 0; i < 5 && data == null; i++)
            {
                try { data = Clipboard.GetDataObject(); }
                catch (ExternalException) { System.Threading.Thread.Sleep(40); }   // another app has the clipboard open
            }
            return data == null ? null : FromClipboardData(data, maxW, maxH, out problem);
        }

        public static Bitmap FromClipboardData(IDataObject data, int maxW, int maxH, out string problem)
        {
            problem = null;
            string from = null;
            Bitmap img = null;
            try { img = FromPngFormat(data, "PNG") ?? FromPngFormat(data, "image/png"); from = "PNG"; }
            catch (Exception ex) { Log.Warn("Paste: PNG on the clipboard could not be read: " + ex.Message); }
            if (img == null)
            {
                try { img = FromDibV5(data); from = "DIBV5"; }
                catch (Exception ex) { Log.Warn("Paste: DIBV5 on the clipboard could not be read: " + ex.Message); }
            }
            if (img == null)
            {
                try { img = FromFiles(data, maxW, maxH, out problem); from = "file"; }
                catch (Exception ex) { Log.Warn("Paste: copied files could not be read: " + ex.Message); }
            }
            if (img == null)
            {
                try
                {
                    var raw = data.GetDataPresent(DataFormats.Bitmap) ? data.GetData(DataFormats.Bitmap, true) as Image : null;
                    if (raw != null) using (raw) img = ToPArgb(raw);
                    from = "bitmap";
                }
                catch (Exception ex) { Log.Warn("Paste: bitmap on the clipboard could not be read: " + ex.Message); }
            }
            if (img == null)
            {
                Log.Info("Paste: no picture on the clipboard (formats: " + string.Join(", ", data.GetFormats(false)) + ")");
                return null;
            }
            problem = null;
            if (img.Width > maxW || img.Height > maxH)
            {
                Bitmap big = img;
                img = FitCopy(big, maxW, maxH);
                big.Dispose();
            }
            Log.Info("Pasted a " + img.Width + "x" + img.Height + " picture (" + from + ")");
            return img;
        }

        static Bitmap FromPngFormat(IDataObject data, string format)
        {
            if (!data.GetDataPresent(format)) return null;
            var st = data.GetData(format) as Stream;
            if (st == null) return null;
            using (st)
            using (var raw = new Bitmap(st))
                return ToPArgb(raw);
        }

        // CF_DIBV5 with a real alpha channel. Windows makes one up from every bitmap; without alpha (or not 32-bit) the
        // plain bitmap path reads it just as well.
        static Bitmap FromDibV5(IDataObject data)
        {
            const string Format = "Format17";   // CF_DIBV5
            if (!data.GetDataPresent(Format)) return null;
            var st = data.GetData(Format) as MemoryStream;
            if (st == null) return null;
            byte[] b = st.ToArray();
            if (b.Length < 40) return null;
            int size = BitConverter.ToInt32(b, 0), w = BitConverter.ToInt32(b, 4), h = BitConverter.ToInt32(b, 8);
            int bits = BitConverter.ToUInt16(b, 14), comp = BitConverter.ToInt32(b, 16), used = BitConverter.ToInt32(b, 32);
            if (size < 40 || bits != 32 || (comp != 0 && comp != 3) || w <= 0 || h == 0 || used < 0 || used > 256) return null;
            if (comp == 3)
            {
                int masks = size >= 52 ? 40 : size;   // V4/V5 header: inside it; plain header: right after it
                if (b.Length < masks + 12 || BitConverter.ToUInt32(b, masks) != 0xFF0000 || BitConverter.ToUInt32(b, masks + 4) != 0xFF00
                    || BitConverter.ToUInt32(b, masks + 8) != 0xFF) return null;
            }
            bool bottomUp = h > 0;
            h = Math.Abs(h);
            int stride = w * 4, offset = size + (comp == 3 && size == 40 ? 12 : 0) + used * 4;
            if ((long)offset + (long)stride * h > b.Length) return null;
            bool alpha = false;
            for (int i = offset + 3, end = offset + stride * h; i < end && !alpha; i += 4) alpha = b[i] != 0;
            if (!alpha) return null;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(b, offset + stride * (bottomUp ? h - 1 - y : y), bd.Scan0 + y * bd.Stride, stride);
                }
                finally { bmp.UnlockBits(bd); }
                return ToPArgb(bmp);
            }
        }

        // Image files copied in File Explorer (CF_HDROP): the first one that reads.
        static Bitmap FromFiles(IDataObject data, int maxW, int maxH, out string problem)
        {
            problem = null;
            if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
            var files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return null;
            foreach (string f in files)
            {
                if (!File.Exists(f)) continue;   // a folder
                Bitmap b = LoadFile(f, maxW, maxH);
                if (b != null) return b;
                if (problem == null) problem = "Can't read " + Path.GetFileName(f) + " as a picture";
            }
            if (problem == null) problem = "Nothing to paste: the copied items aren't pictures";
            return null;
        }

        // A picture file, turned upright (camera photos) and scaled down to fit maxW x maxH. PNG/JPEG/BMP/GIF/TIFF/ICO
        // through GDI+; anything else (WebP, HEIC, AVIF...) through the codecs Windows has.
        public static Bitmap LoadFile(string path, int maxW, int maxH)
        {
            if (Array.IndexOf(GdiFiles, Path.GetExtension(path).ToLowerInvariant()) >= 0)
            {
                try
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                    using (var raw = new Bitmap(ms))
                    {
                        TurnUpright(raw);
                        return FitCopy(raw, maxW, maxH);
                    }
                }
                catch (Exception ex) { Log.Warn("Picture file could not be read by GDI+ (" + Path.GetFileName(path) + "): " + ex.Message); }
            }
            return LiveWall.Interop.Wic.Load(path, maxW, maxH);
        }

        // EXIF orientation (0x0112), as cameras and phones write it.
        static void TurnUpright(Image img)
        {
            if (Array.IndexOf(img.PropertyIdList, 0x0112) < 0) return;
            byte[] v = img.GetPropertyItem(0x0112).Value;
            if (v == null || v.Length < 2) return;
            RotateFlipType t;
            switch (BitConverter.ToUInt16(v, 0))
            {
                case 2: t = RotateFlipType.RotateNoneFlipX; break;
                case 3: t = RotateFlipType.Rotate180FlipNone; break;
                case 4: t = RotateFlipType.Rotate180FlipX; break;
                case 5: t = RotateFlipType.Rotate90FlipX; break;
                case 6: t = RotateFlipType.Rotate90FlipNone; break;
                case 7: t = RotateFlipType.Rotate270FlipX; break;
                case 8: t = RotateFlipType.Rotate270FlipNone; break;
                default: return;
            }
            img.RotateFlip(t);
        }

        // A premultiplied copy, scaled down to fit maxW x maxH if it is bigger.
        static Bitmap FitCopy(Image img, int maxW, int maxH)
        {
            float k = Math.Min(1f, Math.Min(maxW / (float)img.Width, maxH / (float)img.Height));
            if (k >= 1) return ToPArgb(img);
            int w = Math.Max(1, (int)Math.Round(img.Width * k)), h = Math.Max(1, (int)Math.Round(img.Height * k));
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            using (var ia = new ImageAttributes())
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
            return b;
        }
    }
}
