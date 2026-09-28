using System;
using System.Collections.Generic;
using LiveWall.Interop;

namespace LiveWall
{
    // Decides, per monitor, whether any part of the desktop is still visible, by subtracting the rectangles of all
    // visible, opaque top-level windows from each monitor's area. Snapped/tiled layouts count as covered too.
    internal sealed class Occlusion
    {
        public struct Result
        {
            public bool[] Covered;          // per monitor
            public bool FullscreenApp;      // a fullscreen app/game (or presentation mode) is in the foreground
            public int FullscreenMonitor;   // index of the monitor it is on, or -1
        }

        // Anything left uncovered below this share of a monitor (rounded window corners, 1px gaps) counts as covered.
        const double UncoveredTolerance = 0.004;

        readonly EnumWindowsProc enumProc;
        readonly List<RECT> windowRects = new List<RECT>(64);
        readonly List<IntPtr> windowHandles = new List<IntPtr>(64);

        public Occlusion()
        {
            enumProc = OnWindow;
        }

        // Class names and bounds of the windows that counted as covering in the last Compute (diagnostics only).
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < windowHandles.Count; i++)
                sb.Append(Native.ClassName(windowHandles[i])).Append(' ').Append(windowRects[i].ToString()).Append("; ");
            return sb.ToString();
        }

        public Result Compute(IList<RECT> monitors, IntPtr ignoreWindow)
        {
            var res = new Result();
            res.Covered = new bool[monitors.Count];
            res.FullscreenMonitor = -1;

            windowRects.Clear();
            windowHandles.Clear();
            ignore = ignoreWindow;
            Native.EnumWindows(enumProc, IntPtr.Zero);

            for (int m = 0; m < monitors.Count; m++)
                res.Covered[m] = UncoveredArea(monitors[m]) <= (long)(monitors[m].Width * (long)monitors[m].Height * UncoveredTolerance);

            // Fullscreen: the foreground window exactly covers its whole monitor (games, videos, F11 browsers)...
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsShellWindow(fg))
            {
                RECT wr;
                if (Native.GetWindowRect(fg, out wr))
                {
                    for (int m = 0; m < monitors.Count; m++)
                    {
                        RECT mr = monitors[m];
                        if (wr.Left <= mr.Left && wr.Top <= mr.Top && wr.Right >= mr.Right && wr.Bottom >= mr.Bottom)
                        {
                            res.FullscreenApp = true;
                            res.FullscreenMonitor = m;
                            break;
                        }
                    }
                }
            }
            // ...or Windows itself reports exclusive fullscreen D3D / presentation mode.
            int quns;
            if (!res.FullscreenApp && Native.SHQueryUserNotificationState(out quns) >= 0 &&
                (quns == Native.QUNS_RUNNING_D3D_FULL_SCREEN || quns == Native.QUNS_PRESENTATION_MODE))
                res.FullscreenApp = true;
            return res;
        }

        // For the music (cheap: no window enumeration): the foreground window if it is an app shown fullscreen or
        // maximized (or filling its screen's work area), else IntPtr.Zero. File Explorer, Settings and similar Windows
        // tools, the shell (Start, Alt+Tab, taskbar, desktop) and LiveWall's own windows don't count.
        // transient: the taskbar, Start, Alt+Tab and the like are in front for a moment; the caller keeps its last answer.
        public static IntPtr AppInFront(out string what, out bool transient)
        {
            what = null;
            IntPtr fg = Native.GetForegroundWindow();
            transient = fg != IntPtr.Zero && ShellOverlays.Contains(Native.ClassName(fg));
            if (fg == IntPtr.Zero || transient) return IntPtr.Zero;
            IntPtr root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);   // a dialog or popup of an app: the app decides
            if (root != IntPtr.Zero && root != fg && Native.IsWindowVisible(root)) fg = root;
            if (Native.IsIconic(fg) || IsShellWindow(fg)) return IntPtr.Zero;
            bool maximized = Native.IsZoomed(fg), fills = false;
            RECT wr;
            if (!maximized && Native.GetWindowRect(fg, out wr))
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFOEX));
                if (Native.GetMonitorInfo(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST), ref mi))
                    fills = Covers(wr, mi.rcMonitor) || Covers(wr, mi.rcWork);
            }
            int quns;
            bool exclusive = !maximized && !fills && Native.SHQueryUserNotificationState(out quns) >= 0 &&
                             (quns == Native.QUNS_RUNNING_D3D_FULL_SCREEN || quns == Native.QUNS_PRESENTATION_MODE);
            if (!maximized && !fills && !exclusive) return IntPtr.Zero;
            string exe = AppProcessName(fg);
            if (exe != null && WindowsTools.Contains(exe)) return IntPtr.Zero;
            what = (maximized ? "maximized app" : "fullscreen app") + (exe != null ? " (" + exe + ")" : "");
            return fg;
        }

        static bool Covers(RECT a, RECT b) { return a.Left <= b.Left && a.Top <= b.Top && a.Right >= b.Right && a.Bottom >= b.Bottom; }

        static readonly HashSet<string> ShellOverlays = new HashSet<string>
            { "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "Windows.UI.Core.CoreWindow", "Shell_TrayWnd", "TaskListThumbnailWnd" };

        // Not "an app" for the music: File Explorer, Settings and other Windows tools, shell hosts, LiveWall itself.
        static readonly HashSet<string> WindowsTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "SystemSettings", "Taskmgr", "mmc", "control", "rundll32", "regedit", "msconfig", "resmon", "perfmon",
            "SecHealthUI", "dxdiag", "msinfo32", "SystemPropertiesAdvanced", "SystemPropertiesComputerName", "SystemPropertiesHardware",
            "SystemPropertiesPerformance", "SystemPropertiesProtection", "SystemPropertiesRemote", "StartMenuExperienceHost",
            "SearchHost", "ShellExperienceHost", "TextInputHost", "LockApp", "LiveWall"
        };

        static IntPtr nameCacheWindow;
        static string nameCache;

        // The process behind a window (for a Store app's frame window: the app inside it, e.g. SystemSettings).
        static string AppProcessName(IntPtr hwnd)
        {
            if (hwnd == nameCacheWindow) return nameCache;
            IntPtr target = hwnd;
            if (Native.ClassName(hwnd) == "ApplicationFrameWindow")
            {
                IntPtr core = Native.FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero) target = core;
            }
            uint pid;
            Native.GetWindowThreadProcessId(target, out pid);
            string path = AudioMonitor.ProcessPath(pid);
            nameCacheWindow = hwnd;
            nameCache = path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : null;
            return nameCache;
        }

        IntPtr ignore;

        bool OnWindow(IntPtr h, IntPtr l)
        {
            if (h == ignore || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) >= 0 && cloaked != 0) return true;

            uint ex = Native.ExStyle(h);
            if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;           // click-through overlays
            if ((ex & Native.WS_EX_LAYERED) != 0)
            {
                uint key; byte alpha; uint flags;
                // Per-pixel-alpha (UpdateLayeredWindow) windows fail this call: treat them as see-through.
                if (!Native.GetLayeredWindowAttributes(h, out key, out alpha, out flags)) return true;
                if ((flags & Native.LWA_COLORKEY) != 0 || ((flags & Native.LWA_ALPHA) != 0 && alpha < 250)) return true;
            }
            if (IsShellWindow(h)) return true;

            RECT r;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16) < 0 && !Native.GetWindowRect(h, out r)) return true;
            if (r.Width <= 0 || r.Height <= 0) return true;
            windowRects.Add(r);
            windowHandles.Add(h);
            return true;
        }

        static bool IsShellWindow(IntPtr h)
        {
            string cls = Native.ClassName(h);
            return cls == "Progman" || cls == "WorkerW";
        }

        // Area of `area` not covered by any collected window rectangle.
        long UncoveredArea(RECT area)
        {
            var remaining = new List<RECT>(8) { area };
            var next = new List<RECT>(16);
            foreach (RECT w in windowRects)
            {
                if (remaining.Count == 0) break;
                next.Clear();
                foreach (RECT r in remaining)
                {
                    if (w.Right <= r.Left || w.Left >= r.Right || w.Bottom <= r.Top || w.Top >= r.Bottom) { next.Add(r); continue; }
                    if (w.Top > r.Top) next.Add(new RECT(r.Left, r.Top, r.Right, w.Top));
                    if (w.Bottom < r.Bottom) next.Add(new RECT(r.Left, w.Bottom, r.Right, r.Bottom));
                    int top = Math.Max(r.Top, w.Top), bottom = Math.Min(r.Bottom, w.Bottom);
                    if (w.Left > r.Left) next.Add(new RECT(r.Left, top, w.Left, bottom));
                    if (w.Right < r.Right) next.Add(new RECT(w.Right, top, r.Right, bottom));
                }
                var t = remaining; remaining = next; next = t;
            }
            long sum = 0;
            foreach (RECT r in remaining) sum += (long)r.Width * r.Height;
            return sum;
        }
    }
}
