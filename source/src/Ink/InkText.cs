using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    // Turns text (including color emoji, kaomoji and symbols) into a premultiplied bitmap cropped to the ink.
    //
    // DirectWrite through a software Direct2D DC render target draws color emoji; GDI+ (the fallback) can't. Nothing
    // is kept alive between calls except a small per-thread cache, which the editor clears when it closes.
    internal static class InkText
    {
        public const string Plain = "none", Outline = "outline", Box = "box";

        public sealed class Raster
        {
            public Bitmap Image;     // premultiplied ARGB, cropped
            public PointF Offset;    // image top-left relative to the text's anchor, in target pixels
        }

        [ThreadStatic] static Dictionary<string, Raster> cache;
        [ThreadStatic] static List<string> cacheOrder;
        const int CacheSize = 48;

        // A text element as drawn at `scale` target pixels per canvas unit (cached by element id).
        public static Raster For(InkStroke s, float scale)
        {
            string key = s.Id + "|" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (cache == null) { cache = new Dictionary<string, Raster>(); cacheOrder = new List<string>(); }
            Raster r;
            if (cache.TryGetValue(key, out r)) return r;
            r = Render(s.Text, s.Font, s.Width * scale, s.Bold, s.Italic, s.Argb, s.Effect);
            if (cacheOrder.Count >= CacheSize)
            {
                Raster old;
                if (cache.TryGetValue(cacheOrder[0], out old) && old != null && old.Image != null) old.Image.Dispose();
                cache.Remove(cacheOrder[0]);
                cacheOrder.RemoveAt(0);
            }
            cache[key] = r;
            cacheOrder.Add(key);
            return r;
        }

        public static void ClearCache()
        {
            ReleaseD2D();
            if (cache == null) return;
            foreach (var r in cache.Values) if (r != null && r.Image != null) r.Image.Dispose();
            cache = null;
            cacheOrder = null;
        }

        // Canvas-unit bounds of a text element (its ink, including the effect).
        public static RectangleF Measure(InkStroke s)
        {
            Raster r = For(s, 1f);
            if (r == null || r.Image == null) return new RectangleF(s.Points[0].X, s.Points[0].Y, 1, 1);
            return new RectangleF(s.Points[0].X + r.Offset.X, s.Points[0].Y + r.Offset.Y, r.Image.Width, r.Image.Height);
        }

        public static void Draw(Graphics g, InkStroke s, InkMapping m)
        {
            Raster r = For(s, m.Scale);
            if (r == null || r.Image == null) return;
            PointF p = m.ToTarget(s.Points[0].X, s.Points[0].Y);
            g.DrawImageUnscaled(r.Image, (int)Math.Round(p.X + r.Offset.X), (int)Math.Round(p.Y + r.Offset.Y));
        }

        // Contrasting color for outlines and boxes.
        public static int EffectColor(int argb)
        {
            Color c = Color.FromArgb(argb);
            return c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 150 ? unchecked((int)0xFF202124) : unchecked((int)0xFFFFFFFF);
        }

        // ------------------------------------------------------------------ rendering

        public static Raster Render(string text, string font, float sizePx, bool bold, bool italic, int argb, string effect)
        {
            if (string.IsNullOrEmpty(text)) return null;
            sizePx = Math.Max(2f, Math.Min(2000f, sizePx));
            text = text.Replace("\r\n", "\n");
            Raster plain = RenderPlain(text, string.IsNullOrEmpty(font) ? "Segoe UI" : font, sizePx, bold, italic, argb);
            if (plain == null || effect == null || effect == Plain) return plain;
            try { return ApplyEffect(plain, effect, sizePx, argb); }
            finally { plain.Image.Dispose(); }
        }

        static Raster RenderPlain(string text, string font, float size, bool bold, bool italic, int argb)
        {
            string[] lines = text.Split('\n');
            int longest = 1;
            foreach (string l in lines) longest = Math.Max(longest, l.Length);
            int pad = (int)(size * 0.6f) + 4;
            int w = (int)Math.Min(8192, size * (longest * 1.25f + 1) + pad * 2);
            int h = (int)Math.Min(8192, size * 1.6f * lines.Length + pad * 2);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int[] px = null;
                try { px = RenderD2D(text, font, size, bold, italic, argb, w, h, pad); }
                catch (Exception ex) { if (!d2dFailedLogged) { d2dFailedLogged = true; Log.Warn("DirectWrite text failed, using GDI+: " + ex.Message); } }
                if (px == null) px = RenderGdiPlus(text, font, size, bold, italic, argb, w, h, pad);
                Rectangle ink = InkBounds(px, w, h);
                if (ink.IsEmpty) return null;
                bool clipped = ink.Right >= w - 1 || ink.Bottom >= h - 1;
                if (clipped && attempt == 0 && w < 8192 && h < 8192) { w = Math.Min(8192, w * 2); h = Math.Min(8192, h * 2); continue; }
                return new Raster { Image = Crop(px, w, ink), Offset = new PointF(ink.X - pad, ink.Y - pad) };
            }
            return null;
        }

        static bool d2dFailedLogged;

        static Rectangle InkBounds(int[] px, int w, int h)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (((px[row + x] >> 24) & 0xFF) < 4) continue;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
            return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        static Bitmap Crop(int[] px, int w, Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < r.Height; y++) Marshal.Copy(px, (r.Y + y) * w + r.X, data.Scan0 + y * data.Stride, r.Width);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        static Raster ApplyEffect(Raster plain, string effect, float size, int argb)
        {
            Color fx = Color.FromArgb(EffectColor(argb));
            if (effect == Outline)
            {
                float r = Math.Max(1.5f, size * 0.07f);
                int m = (int)Math.Ceiling(r) + 2;
                var bmp = new Bitmap(plain.Image.Width + m * 2, plain.Image.Height + m * 2, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                using (var ia = new ImageAttributes())
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    // The text's shape in the outline color, stamped around a circle: a round outline, emoji included.
                    var cm = new ColorMatrix(new[]
                    {
                        new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 }, new float[] { fx.R / 255f, fx.G / 255f, fx.B / 255f, 0, 1 }
                    });
                    ia.SetColorMatrix(cm);
                    var dst = new Rectangle(0, 0, plain.Image.Width, plain.Image.Height);
                    foreach (float rr in new[] { r, r * 0.5f })
                        for (int i = 0; i < 16; i++)
                        {
                            double a = i * Math.PI / 8;
                            dst.X = (int)Math.Round(m + Math.Cos(a) * rr);
                            dst.Y = (int)Math.Round(m + Math.Sin(a) * rr);
                            g.DrawImage(plain.Image, dst, 0, 0, plain.Image.Width, plain.Image.Height, GraphicsUnit.Pixel, ia);
                        }
                    g.DrawImageUnscaled(plain.Image, m, m);
                }
                return new Raster { Image = bmp, Offset = new PointF(plain.Offset.X - m, plain.Offset.Y - m) };
            }
            if (effect == Box)
            {
                int p = (int)Math.Ceiling(size * 0.3f) + 1;
                var bmp = new Bitmap(plain.Image.Width + p * 2, plain.Image.Height + p * 2, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    InkRenderer.Prepare(g);
                    float rad = Math.Min(size * 0.35f, Math.Min(bmp.Width, bmp.Height) / 2f);
                    using (var path = RoundRect(new RectangleF(0.5f, 0.5f, bmp.Width - 1, bmp.Height - 1), rad))
                    using (var b = new SolidBrush(Color.FromArgb(235, fx)))
                        g.FillPath(b, path);
                    g.DrawImageUnscaled(plain.Image, p, p);
                }
                return new Raster { Image = bmp, Offset = new PointF(plain.Offset.X - p, plain.Offset.Y - p) };
            }
            return new Raster { Image = (Bitmap)plain.Image.Clone(), Offset = plain.Offset };
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.1f, rad * 2);
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static int[] RenderGdiPlus(string text, string font, float size, bool bold, bool italic, int argb, int w, int h, int pad)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var f = new Font(font, size, (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0), GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(argb)))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.DrawString(text, f, b, pad, pad, StringFormat.GenericTypographic);
                }
                var px = new int[w * h];
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try { for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w, w); }
                finally { bmp.UnlockBits(data); }
                return px;
            }
        }

        // ------------------------------------------------------------------ Direct2D / DirectWrite

        // Kept per thread while text is being drawn (the editor, one ink layer or board render); ClearCache releases them.
        [ThreadStatic] static ID2D1Factory d2dFactory;
        [ThreadStatic] static IDWriteFactory dwriteFactory;
        [ThreadStatic] static ID2D1DCRenderTarget dcTarget;

        static void ReleaseD2D()
        {
            if (dcTarget != null) { Marshal.ReleaseComObject(dcTarget); dcTarget = null; }
            if (dwriteFactory != null) { Marshal.ReleaseComObject(dwriteFactory); dwriteFactory = null; }
            if (d2dFactory != null) { Marshal.ReleaseComObject(d2dFactory); d2dFactory = null; }
        }

        static int[] RenderD2D(string text, string font, float size, bool bold, bool italic, int argb, int w, int h, int pad)
        {
            IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero, format = IntPtr.Zero, brush = IntPtr.Zero;
            IDWriteTextFormat fmt = null;
            bool ok = false;
            try
            {
                dc = InkNative.CreateCompatibleDC(IntPtr.Zero);
                var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr bits;
                dib = InkNative.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return null;
                old = InkNative.SelectObject(dc, dib);

                IntPtr p;
                if (d2dFactory == null)
                {
                    Guid iid = typeof(ID2D1Factory).GUID;
                    Check(D2D1CreateFactory(0 /*single threaded*/, ref iid, IntPtr.Zero, out p));
                    d2dFactory = (ID2D1Factory)Take(p);
                    iid = typeof(IDWriteFactory).GUID;
                    Check(DWriteCreateFactory(0 /*shared*/, ref iid, out p));
                    dwriteFactory = (IDWriteFactory)Take(p);
                    var props = new D2D1_RENDER_TARGET_PROPERTIES
                    {
                        type = 1 /*software: no GPU device for this*/, format = 87 /*B8G8R8A8_UNORM*/, alphaMode = 1 /*premultiplied*/,
                        dpiX = 96, dpiY = 96
                    };
                    Check(d2dFactory.CreateDCRenderTarget(ref props, out p));
                    dcTarget = (ID2D1DCRenderTarget)Take(p);
                }
                var target = dcTarget;

                Check(dwriteFactory.CreateTextFormat(font, IntPtr.Zero, bold ? 700 : 400, italic ? 2 : 0, 5, size, "en-us", out format));
                fmt = (IDWriteTextFormat)Marshal.GetObjectForIUnknown(format);
                fmt.SetWordWrapping(1 /*no wrap*/);

                var rect = new RECT(0, 0, w, h);
                Check(target.BindDC(dc, ref rect));

                Color c = Color.FromArgb(argb);
                var color = new D2D1_COLOR_F { r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = 1 };
                var clear = new D2D1_COLOR_F();
                target.BeginDraw();
                target.SetTextAntialiasMode(2 /*grayscale: the target has alpha*/);
                target.Clear(ref clear);
                Check(target.CreateSolidColorBrush(ref color, IntPtr.Zero, out brush));
                var layout = new D2D1_RECT_F { left = pad, top = pad, right = w, bottom = h };
                target.DrawText(text, text.Length, format, ref layout, brush, 4 /*enable color font*/, 0 /*natural*/);
                Check(target.EndDraw(IntPtr.Zero, IntPtr.Zero));

                var px = new int[w * h];
                Marshal.Copy(bits, px, 0, px.Length);
                ok = true;
                return px;
            }
            finally
            {
                if (brush != IntPtr.Zero) Marshal.Release(brush);
                if (fmt != null) Marshal.ReleaseComObject(fmt);
                if (format != IntPtr.Zero) Marshal.Release(format);
                if (!ok) ReleaseD2D();   // start clean next time
                if (old != IntPtr.Zero) InkNative.SelectObject(dc, old);
                if (dib != IntPtr.Zero) InkNative.DeleteObject(dib);
                if (dc != IntPtr.Zero) InkNative.DeleteDC(dc);
            }
        }

        static object Take(IntPtr p)
        {
            try { return Marshal.GetObjectForIUnknown(p); }
            finally { Marshal.Release(p); }
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, out IntPtr factory);

        [StructLayout(LayoutKind.Sequential)]
        struct D2D1_RENDER_TARGET_PROPERTIES
        {
            public int type, format, alphaMode;
            public float dpiX, dpiY;
            public int usage, minLevel;
        }

        [StructLayout(LayoutKind.Sequential)] struct D2D1_COLOR_F { public float r, g, b, a; }
        [StructLayout(LayoutKind.Sequential)] struct D2D1_RECT_F { public float left, top, right, bottom; }

        // Only the methods used are real; the rest hold their vtable slots.
        [ComImport, Guid("06152247-6f50-465a-9245-118bfd3b6007"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1Factory
        {
            void _ReloadSystemMetrics(); void _GetDesktopDpi(); void _CreateRectangleGeometry(); void _CreateRoundedRectangleGeometry();
            void _CreateEllipseGeometry(); void _CreateGeometryGroup(); void _CreateTransformedGeometry(); void _CreatePathGeometry();
            void _CreateStrokeStyle(); void _CreateDrawingStateBlock(); void _CreateWicBitmapRenderTarget(); void _CreateHwndRenderTarget();
            void _CreateDxgiSurfaceRenderTarget();
            [PreserveSig] int CreateDCRenderTarget(ref D2D1_RENDER_TARGET_PROPERTIES props, out IntPtr target);
        }

        [ComImport, Guid("1c51bc64-de61-46fd-9899-63a5d8f03950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1DCRenderTarget
        {
            void _GetFactory();
            void _CreateBitmap(); void _CreateBitmapFromWicBitmap(); void _CreateSharedBitmap(); void _CreateBitmapBrush();
            [PreserveSig] int CreateSolidColorBrush(ref D2D1_COLOR_F color, IntPtr properties, out IntPtr brush);
            void _CreateGradientStopCollection(); void _CreateLinearGradientBrush(); void _CreateRadialGradientBrush();
            void _CreateCompatibleRenderTarget(); void _CreateLayer(); void _CreateMesh(); void _DrawLine(); void _DrawRectangle();
            void _FillRectangle(); void _DrawRoundedRectangle(); void _FillRoundedRectangle(); void _DrawEllipse(); void _FillEllipse();
            void _DrawGeometry(); void _FillGeometry(); void _FillMesh(); void _FillOpacityMask(); void _DrawBitmap();
            [PreserveSig] void DrawText([MarshalAs(UnmanagedType.LPWStr)] string text, int length, IntPtr format, ref D2D1_RECT_F layout,
                                        IntPtr brush, int options, int measuringMode);
            void _DrawTextLayout(); void _DrawGlyphRun(); void _SetTransform(); void _GetTransform(); void _SetAntialiasMode();
            void _GetAntialiasMode();
            [PreserveSig] void SetTextAntialiasMode(int mode);
            void _GetTextAntialiasMode(); void _SetTextRenderingParams(); void _GetTextRenderingParams(); void _SetTags(); void _GetTags();
            void _PushLayer(); void _PopLayer(); void _Flush(); void _SaveDrawingState(); void _RestoreDrawingState();
            void _PushAxisAlignedClip(); void _PopAxisAlignedClip();
            [PreserveSig] void Clear(ref D2D1_COLOR_F color);
            [PreserveSig] void BeginDraw();
            [PreserveSig] int EndDraw(IntPtr tag1, IntPtr tag2);
            void _GetPixelFormat(); void _SetDpi(); void _GetDpi(); void _GetSize(); void _GetPixelSize(); void _GetMaximumBitmapSize();
            void _IsSupported();
            [PreserveSig] int BindDC(IntPtr dc, ref RECT rect);
        }

        [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDWriteFactory
        {
            void _GetSystemFontCollection(); void _CreateCustomFontCollection(); void _RegisterFontCollectionLoader();
            void _UnregisterFontCollectionLoader(); void _CreateFontFileReference(); void _CreateCustomFontFileReference();
            void _CreateFontFace(); void _CreateRenderingParams(); void _CreateMonitorRenderingParams(); void _CreateCustomRenderingParams();
            void _RegisterFontFileLoader(); void _UnregisterFontFileLoader();
            [PreserveSig] int CreateTextFormat([MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style,
                                               int stretch, float size, [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);
        }

        [ComImport, Guid("9c906818-31d7-4fd3-a151-7c5e225db55a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDWriteTextFormat
        {
            [PreserveSig] int SetTextAlignment(int alignment);
            [PreserveSig] int SetParagraphAlignment(int alignment);
            [PreserveSig] int SetWordWrapping(int wrapping);
        }
    }
}
