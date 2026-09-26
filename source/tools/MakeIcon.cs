// Draws the LiveWall icon (a rounded tile with a sun over waves) and writes a multi-size .ico.
// Small sizes are stored as 32-bit DIBs (readable by System.Drawing.Icon), 256 px as PNG.
// Usage: MakeIcon.exe <out.ico> [preview.png]
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    static int Main(string[] args)
    {
        var images = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
        {
            using (var bmp = Draw(Sizes[i]))
                images[i] = Sizes[i] >= 256 ? Png(bmp) : Dib(bmp);
        }
        using (var fs = File.Create(args[0]))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
        if (args.Length > 1)
        {
            using (var sheet = new Bitmap(520, 280))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(40, 40, 46));
                int x = 10;
                foreach (int s in Sizes)
                {
                    using (var b = Draw(s)) g.DrawImage(b, x, 10 + (s >= 256 ? 0 : 0), s, s);
                    x += Math.Min(s, 256) + 8;
                    if (s == 64) x += 0;
                }
                sheet.Save(args[1], ImageFormat.Png);
            }
        }
        return 0;
    }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float pad = s <= 20 ? 0.5f : s * 0.04f;
            var tile = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
            using (var path = Rounded(tile, s * (s <= 20 ? 0.2f : 0.23f)))
            {
                using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(255, 79, 70, 229), Color.FromArgb(255, 6, 182, 212)))
                    g.FillPath(bg, path);
                g.SetClip(path);

                // Sun with a soft glow
                float sr = s * 0.15f, sx = s * 0.66f, sy = s * 0.33f;
                if (s >= 32)
                    using (var glow = new SolidBrush(Color.FromArgb(70, 255, 236, 170)))
                        g.FillEllipse(glow, sx - sr * 1.7f, sy - sr * 1.7f, sr * 3.4f, sr * 3.4f);
                using (var sun = new SolidBrush(Color.FromArgb(255, 254, 240, 138)))
                    g.FillEllipse(sun, sx - sr, sy - sr, sr * 2, sr * 2);

                // Back wave
                if (s >= 24)
                    using (var back = Wave(s, 0.60f, 0.07f, 0.35f))
                    using (var b = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                        g.FillPath(b, back);
                // Front wave
                using (var front = Wave(s, 0.70f, 0.08f, 0.0f))
                using (var b = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
                    g.FillPath(b, front);
                g.ResetClip();

                if (s >= 32)
                    using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, s / 64f)))
                        g.DrawPath(edge, path);
            }
        }
        return bmp;
    }

    static GraphicsPath Wave(int s, float baseline, float amplitude, float phase)
    {
        var p = new GraphicsPath();
        float y = s * baseline, a = s * amplitude;
        var pts = new PointF[33];
        for (int i = 0; i <= 32; i++)
        {
            float x = s * i / 32f;
            pts[i] = new PointF(x, y + (float)Math.Sin((i / 32.0 + phase) * Math.PI * 2 * 1.25) * a);
        }
        p.AddCurve(pts);
        p.AddLine(s, y, s, s + 1);
        p.AddLine(s, s + 1, 0, s + 1);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // BITMAPINFOHEADER + bottom-up BGRA pixels + empty AND mask.
    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskStride = ((s + 31) / 32) * 4;
            w.Write(new byte[maskStride * s]);
            return ms.ToArray();
        }
    }
}
