using System;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall
{
    // Windows' own (static) wallpaper. Static images are handed to Windows entirely, so they cost nothing to display.
    // Call from the worker thread: these are cross-process calls into Explorer and can take a moment.
    internal static class NativeWallpaper
    {
        public static int PositionFor(FitMode fit)
        {
            switch (fit)
            {
                case FitMode.Fit: return ShellApi.DWPOS_FIT;
                case FitMode.Stretch: return ShellApi.DWPOS_STRETCH;
                case FitMode.Center: return ShellApi.DWPOS_CENTER;
                default: return ShellApi.DWPOS_FILL;
            }
        }

        static IDesktopWallpaper Create()
        {
            return (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellApi.CLSID_DesktopWallpaper));
        }

        public static bool Set(string path, int position)
        {
            IDesktopWallpaper dw = null;
            try
            {
                dw = Create();
                int cur;
                if (dw.GetPosition(out cur) < 0 || cur != position) dw.SetPosition(position);
                int hr = dw.SetWallpaper(null, path);
                if (hr < 0) { Log.Warn("SetWallpaper failed 0x" + hr.ToString("X8") + " for " + path); return false; }
                return true;
            }
            catch (Exception ex) { Log.Error("SetWallpaper", ex); return false; }
            finally { if (dw != null) Marshal.ReleaseComObject(dw); }
        }

        public static void GetCurrent(out string path, out int position)
        {
            path = ""; position = ShellApi.DWPOS_FILL;
            IDesktopWallpaper dw = null;
            try
            {
                dw = Create();
                IntPtr p;
                if (dw.GetWallpaper(null, out p) >= 0) path = ShellApi.TakeCoTaskString(p) ?? "";
                int pos;
                if (dw.GetPosition(out pos) >= 0) position = pos;
            }
            catch (Exception ex) { Log.Error("GetWallpaper", ex); }
            finally { if (dw != null) Marshal.ReleaseComObject(dw); }
        }

        // Restores a wallpaper captured by GetCurrent. An empty path means "no picture" (solid color background).
        public static void Restore(string path, int position)
        {
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) { Set(path, position); return; }
            ShellApi.SystemParametersInfo(ShellApi.SPI_SETDESKWALLPAPER, 0, "", ShellApi.SPIF_UPDATEINIFILE | ShellApi.SPIF_SENDCHANGE);
        }
    }
}
