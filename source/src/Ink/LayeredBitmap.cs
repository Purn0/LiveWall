using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    // A 32-bit premultiplied DIB that GDI+ draws into directly and UpdateLayeredWindow presents without any copy:
    // the drawing editor's per-pixel-alpha top-level window. (Not usable for children of the desktop; see InkLayer.)
    internal sealed class LayeredBitmap : IDisposable
    {
        public readonly int Width, Height;
        public readonly Bitmap Bitmap;       // shares the DIB's memory
        IntPtr hdc, hbmp, oldObject, bits;
        IntPtr info;                         // unmanaged POINT/SIZE/POINT/BLENDFUNCTION/RECT block for UpdateLayeredWindowIndirect
        bool presentedOnce;

        public LayeredBitmap(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            IntPtr screen = InkNative.GetDC(IntPtr.Zero);
            hdc = InkNative.CreateCompatibleDC(screen);
            InkNative.ReleaseDC(IntPtr.Zero, screen);
            var bi = new BITMAPINFO();
            bi.biSize = 40;
            bi.biWidth = Width;
            bi.biHeight = -Height;   // top-down, same row order as GDI+
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            hbmp = InkNative.CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero)
            {
                InkNative.DeleteDC(hdc);
                throw new OutOfMemoryException("CreateDIBSection " + Width + "x" + Height);
            }
            oldObject = InkNative.SelectObject(hdc, hbmp);
            Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
            info = Marshal.AllocHGlobal(64);
        }

        public Graphics CreateGraphics()
        {
            var g = Graphics.FromImage(Bitmap);
            InkRenderer.Prepare(g);
            return g;
        }

        // Shows the bitmap in a layered window. `screenPos` moves a top-level window there (null keeps the position,
        // which is what child windows need). `dirty` limits the update to a rectangle after the first present.
        public bool Present(IntPtr hwnd, Point? screenPos, Rectangle? dirty)
        {
            // Offsets in `info`: 0 dst POINT, 8 SIZE, 16 src POINT, 24 BLENDFUNCTION, 32 dirty RECT.
            if (screenPos.HasValue) { Marshal.WriteInt32(info, 0, screenPos.Value.X); Marshal.WriteInt32(info, 4, screenPos.Value.Y); }
            Marshal.WriteInt32(info, 8, Width); Marshal.WriteInt32(info, 12, Height);
            Marshal.WriteInt32(info, 16, 0); Marshal.WriteInt32(info, 20, 0);
            Marshal.WriteInt32(info, 24, 0x01FF0000);   // AC_SRC_OVER, 0, alpha 255, AC_SRC_ALPHA (little-endian bytes 00 00 FF 01)

            bool useDirty = dirty.HasValue && presentedOnce;
            if (useDirty)
            {
                Rectangle d = Rectangle.Intersect(dirty.Value, new Rectangle(0, 0, Width, Height));
                if (d.Width <= 0 || d.Height <= 0) return true;
                Marshal.WriteInt32(info, 32, d.Left); Marshal.WriteInt32(info, 36, d.Top);
                Marshal.WriteInt32(info, 40, d.Right); Marshal.WriteInt32(info, 44, d.Bottom);
            }
            var u = new UPDATELAYEREDWINDOWINFO
            {
                cbSize = Marshal.SizeOf(typeof(UPDATELAYEREDWINDOWINFO)),
                hdcDst = IntPtr.Zero,
                pptDst = screenPos.HasValue ? info : IntPtr.Zero,
                psize = info + 8,
                hdcSrc = hdc,
                pptSrc = info + 16,
                crKey = 0,
                pblend = info + 24,
                dwFlags = InkNative.ULW_ALPHA,
                prcDirty = useDirty ? info + 32 : IntPtr.Zero
            };
            bool ok = InkNative.UpdateLayeredWindowIndirect(hwnd, ref u);
            if (!ok && useDirty)
            {
                u.prcDirty = IntPtr.Zero;
                ok = InkNative.UpdateLayeredWindowIndirect(hwnd, ref u);
            }
            if (ok) presentedOnce = true;
            else Log.Warn("UpdateLayeredWindowIndirect failed: " + Marshal.GetLastWin32Error());
            return ok;
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            if (hdc != IntPtr.Zero)
            {
                InkNative.SelectObject(hdc, oldObject);
                InkNative.DeleteObject(hbmp);
                InkNative.DeleteDC(hdc);
                hdc = IntPtr.Zero;
            }
            if (info != IntPtr.Zero) { Marshal.FreeHGlobal(info); info = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public uint bmiColor0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UPDATELAYEREDWINDOWINFO
    {
        public int cbSize;
        public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc;
        public uint crKey;
        public IntPtr pblend;
        public uint dwFlags;
        public IntPtr prcDirty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags, penMask, pressure, rotation;
        public int tiltX, tiltY;
    }

    internal static class InkNative
    {
        public const uint ULW_ALPHA = 2;
        public const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_HOTKEY = 0x0312;
        public const uint PT_TOUCH = 2, PT_PEN = 3;
        public const uint POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_PRIMARY = 0x2000, POINTER_FLAG_CANCELED = 0x8000;
        public const uint PEN_FLAG_BARREL = 0x1, PEN_FLAG_INVERTED = 0x2, PEN_FLAG_ERASER = 0x4, PEN_MASK_PRESSURE = 0x1;
        public const uint WS_EX_NOPARENTNOTIFY = 0x4;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        public const uint SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetPointerType(uint pointerId, out uint type);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO info);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    }
}
