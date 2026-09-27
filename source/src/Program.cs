using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall
{
    internal static class Program
    {
        // Usage: LiveWall.exe [--autostart] [--settings|--next|--prev|--pause|--resume|--toggle-pause|--add|--exit|--status]
        //                     [--board|--daily-board|--permanent-board|--wallpaper|--draw]
        //                     [--music-settings|--music-toggle|--music-pause|--music-play|--music-next|--music-volume=N]
        //                     [--restore-wallpaper] [--data <dir>] [--verbose]
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--host")
            {
                // Player host process (started by the running LiveWall; see PlayerHost).
                int d = Array.IndexOf(args, "--data");
                AppPaths.Init(d > 0 && d + 1 < args.Length ? args[d + 1] : null);
                Log.Init(AppPaths.LocalDir);
                return PlayerHost.Run(args);
            }
            if (args.Length >= 3 && args[0] == "--music")
            {
                // Music host process (started by the running LiveWall; see MusicHost).
                int d = Array.IndexOf(args, "--data");
                AppPaths.Init(d > 0 && d + 1 < args.Length ? args[d + 1] : null);
                Log.Init(AppPaths.LocalDir);
                return MusicHost.Run(args);
            }

            string command = null, dataDir = null;
            bool autostart = false, ecoAlways = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "--autostart") autostart = true;
                else if (a == "--verbose") Log.Verbose = true;
                else if (a == "--eco-always") ecoAlways = true;
                else if (a == "--data" && i + 1 < args.Length) dataDir = args[++i];
                else if (a.StartsWith("--")) command = a.Substring(2);
            }

            AppPaths.Init(dataDir);
            Log.Init(AppPaths.LocalDir);

            bool first;
            using (var mutex = new Mutex(true, "Local\\LiveWall.Instance" + AppPaths.InstanceSuffix, out first))
            {
                if (!first)
                {
                    // Already running: hand the request to that instance (launching again = open Settings).
                    if (!autostart) Ipc.Send(command ?? "settings");
                    return 0;
                }
                if (command == "exit") return 0;
                if (command == "restore-wallpaper")
                {
                    var s = Settings.Load();
                    if (s.OriginalCaptured) NativeWallpaper.Restore(s.OriginalWallpaper, s.OriginalPosition);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Error("Unhandled UI exception", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
                try
                {
                    Application.Run(new AppController(autostart, command, ecoAlways));
                }
                catch (Exception ex)
                {
                    Log.Error("Fatal error", ex);
                    MessageBox.Show("LiveWall could not start:\r\n\r\n" + ex.Message, "LiveWall", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
            return 0;
        }
    }

    internal static class Ipc
    {
        public const long Magic = 0x4C57;   // "LW"

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT data, uint flags, uint timeout, out IntPtr result);

        public static bool Send(string command)
        {
            IntPtr hwnd = Native.FindWindow(MessageWindow.ClassName, "LiveWall" + AppPaths.InstanceSuffix);
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            Native.AllowSetForegroundWindow((int)pid);   // let it bring Settings to the front
            byte[] data = Encoding.Unicode.GetBytes(command + "\0");
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var cds = new COPYDATASTRUCT { dwData = new IntPtr(Magic), cbData = data.Length, lpData = pin.AddrOfPinnedObject() };
                IntPtr result;
                SendMessageTimeout(hwnd, Native.WM_COPYDATA, IntPtr.Zero, ref cds, Native.SMTO_ABORTIFHUNG, 5000, out result);
                return result != IntPtr.Zero;
            }
            finally { pin.Free(); }
        }
    }
}
