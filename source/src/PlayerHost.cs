using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall
{
    // Media Engine callbacks arrive on MF worker threads; hop to the host's UI thread.
    public sealed class HostEngineNotify : IMFMediaEngineNotify
    {
        internal IntPtr Window;
        int IMFMediaEngineNotify.EventNotify(uint eventId, IntPtr param1, uint param2)
        {
            switch (eventId)
            {
                case MF.EVENT_LOADEDMETADATA:
                case MF.EVENT_FIRSTFRAMEREADY:
                case MF.EVENT_FORMATCHANGE:
                case MF.EVENT_ERROR:
                case MF.EVENT_RESOURCELOST:
                case MF.EVENT_STREAMRENDERINGERROR:
                    Native.PostMessage(Window, PlayerHost.WM_ENGINE_EVENT, new IntPtr(eventId),
                        new IntPtr(((long)(uint)param1.ToInt64() << 32) | param2));
                    break;
            }
            return 0;
        }
    }

    // `LiveWall.exe --host ...`: plays ONE video into ONE surface window, then exits when told to.
    //
    // Why a separate process: the Media Foundation Media Engine is the most power-efficient way to play video
    // (GPU decode + scaling, no per-frame work in LiveWall), but it keeps a (silent) audio stream open for as long as
    // the process lives, even after the engine is shut down. An open audio stream keeps the audio hardware awake
    // and can stop Windows from sleeping. Ending the host process whenever the wallpaper stops playing releases it.
    internal static class PlayerHost
    {
        // host -> controller: wParam = (event << 24) | playerId, lParam = data
        public const uint WM_HOST_EVENT = Native.WM_APP + 20;
        public const int EVT_READY = 1, EVT_FIRST_FRAME = 2, EVT_ERROR = 3, EVT_LOST = 4, EVT_REVEALED = 5;
        // controller -> host
        public const uint WM_HOST_PLAY = Native.WM_APP + 30, WM_HOST_PAUSE = Native.WM_APP + 31,
            WM_HOST_FIT = Native.WM_APP + 32, WM_HOST_EXIT = Native.WM_APP + 33, WM_HOST_REVEAL = Native.WM_APP + 34;
        internal const uint WM_ENGINE_EVENT = Native.WM_APP + 40;

        static IntPtr controller, commandWindow;
        static int playerId;
        static WallpaperWindow surface;
        static IMFMediaEngineEx engine;
        static HostEngineNotify notify;
        static WndProc commandProc;
        static FitMode fit;
        static int width, height;
        static bool wantPlaying, metadataLoaded, firstFrameSent, exiting;
        static string videoPath;

        // args: --host <controller> <playerId> <parent> <insertAfter> <x> <y> <w> <h> <visible> <fit> <play> <path>
        public static int Run(string[] args)
        {
            try
            {
                int i = 1;
                controller = ParseHandle(args[i++]);
                playerId = int.Parse(args[i++], CultureInfo.InvariantCulture);
                IntPtr parent = ParseHandle(args[i++]), insertAfter = ParseHandle(args[i++]);
                int x = int.Parse(args[i++]), y = int.Parse(args[i++]);
                width = int.Parse(args[i++]); height = int.Parse(args[i++]);
                bool visible = args[i++] == "1";
                fit = (FitMode)Enum.Parse(typeof(FitMode), args[i++], true);
                wantPlaying = args[i++] == "1";
                videoPath = args[i++];

                Native.SetPriorityClass(Native.GetCurrentProcess(), Native.BELOW_NORMAL_PRIORITY_CLASS);
                MF.Check(MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL), "MFStartup");
                CreateCommandWindow();
                WatchController();

                surface = new WallpaperWindow(parent, new RECT(x, y, x + width, y + height), insertAfter);
                surface.DestroyedExternally += (s, e) => { Send(EVT_LOST, 0); Exit(); };
                CreateEngine();
                Send(EVT_READY, ((long)(uint)surface.Handle.ToInt64() << 32) | (uint)commandWindow.ToInt64());
                SetEcoQos(!wantPlaying);

                MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Player host (" + (videoPath == null ? "?" : System.IO.Path.GetFileName(videoPath)) + ")", ex);
                Send(EVT_ERROR, ((long)0 << 32) | (uint)(ex is COMException ? ((COMException)ex).ErrorCode : unchecked((int)0x80004005)));
            }
            Cleanup();
            return 0;
        }

        static IntPtr ParseHandle(string s) { return new IntPtr(long.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture)); }

        static void Send(int evt, long data)
        {
            Native.PostMessage(controller, WM_HOST_EVENT, new IntPtr(((long)evt << 24) | (uint)playerId), new IntPtr(data));
        }

        static void CreateCommandWindow()
        {
            commandProc = CommandProc;
            var wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = commandProc;
            wc.hInstance = Native.GetModuleHandle(IntPtr.Zero);
            wc.lpszClassName = "LiveWall.Host";
            Native.RegisterClassEx(ref wc);
            commandWindow = Native.CreateWindowEx(0, "LiveWall.Host", "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (commandWindow == IntPtr.Zero) throw new InvalidOperationException("host window");
        }

        // Exit if the LiveWall process that started us goes away (crash, kill, sign-out).
        static void WatchController()
        {
            uint pid;
            Native.GetWindowThreadProcessId(controller, out pid);
            if (pid == 0) throw new InvalidOperationException("controller not found");
            var p = Process.GetProcessById((int)pid);
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => Native.PostMessage(commandWindow, WM_HOST_EXIT, IntPtr.Zero, IntPtr.Zero);
            if (p.HasExited) throw new InvalidOperationException("controller exited");
        }

        static void CreateEngine()
        {
            var factory = (IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(MF.CLSID_MFMediaEngineClassFactory));
            IMFAttributes attrs;
            MF.Check(MF.MFCreateAttributes(out attrs, 3), "MFCreateAttributes");
            notify = new HostEngineNotify { Window = commandWindow };
            attrs.SetUnk(MF.MF_MEDIA_ENGINE_CALLBACK, notify);
            attrs.SetU64(MF.MF_MEDIA_ENGINE_PLAYBACK_HWND, (ulong)surface.Handle.ToInt64());
            attrs.SetU32(MF.MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, 87 /* DXGI_FORMAT_B8G8R8A8_UNORM */);
            MF.Check(factory.CreateInstance(MF.MF_MEDIA_ENGINE_FORCEMUTE, attrs, out engine), "Media engine");
            MF.Release(attrs);
            MF.Release(factory);
            engine.SetMuted(1);
            engine.SetLoop(1);
            engine.SetAutoPlay(0);             // playback starts on LOADEDMETADATA, after the geometry is set
            engine.EnableTimeUpdateTimer(0);   // no periodic "timeupdate" callbacks: fewer wakeups
            IntPtr stream = MF.OpenFile(videoPath);   // byte stream: no URL parsing, so any file name works
            try { MF.Check(engine.SetSourceFromByteStream(stream, MF.TypeHintUrl(videoPath)), "Open video"); }
            finally { Marshal.Release(stream); }
        }

        static IntPtr CommandProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_ENGINE_EVENT:
                        OnEngineEvent((uint)wParam.ToInt64(), lParam.ToInt64());
                        return IntPtr.Zero;
                    case WM_HOST_PLAY:
                        wantPlaying = true;
                        if (metadataLoaded && engine.IsPaused() != 0) engine.Play();
                        SetEcoQos(false);
                        return IntPtr.Zero;
                    case WM_HOST_PAUSE:
                        wantPlaying = false;
                        if (metadataLoaded && engine.IsPaused() == 0) engine.Pause();
                        SetEcoQos(true);
                        return IntPtr.Zero;
                    case WM_HOST_FIT:
                        fit = (FitMode)wParam.ToInt64();
                        width = (int)(lParam.ToInt64() & 0xFFFF);
                        height = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                        ApplyFit();
                        return IntPtr.Zero;
                    case WM_HOST_REVEAL:
                        surface.Reveal();
                        Native.DwmFlush();   // wait until it is composited, so the old wallpaper can go without a gap
                        Send(EVT_REVEALED, 0);
                        return IntPtr.Zero;
                    case WM_HOST_EXIT:
                        Exit();
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex) { Log.Error("Player host message 0x" + msg.ToString("X"), ex); }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        static void OnEngineEvent(uint evt, long data)
        {
            if (exiting) return;
            switch (evt)
            {
                case MF.EVENT_LOADEDMETADATA:
                    metadataLoaded = true;
                    ApplyFit();
                    if (wantPlaying) engine.Play();
                    break;
                case MF.EVENT_FORMATCHANGE:
                    ApplyFit();
                    break;
                case MF.EVENT_FIRSTFRAMEREADY:
                    if (!firstFrameSent) { firstFrameSent = true; Send(EVT_FIRST_FRAME, 0); }
                    break;
                case MF.EVENT_ERROR:
                case MF.EVENT_RESOURCELOST:
                case MF.EVENT_STREAMRENDERINGERROR:
                    uint code = evt == MF.EVENT_ERROR ? (uint)(data >> 32) : 0;
                    int hr = (int)(data & 0xFFFFFFFF);
                    if (evt == MF.EVENT_RESOURCELOST && hr == 0) hr = unchecked((int)0x887A0005);   // treat as device removed
                    Send(EVT_ERROR, ((long)code << 32) | (uint)hr);
                    Exit();
                    break;
            }
        }

        // The engine keeps the aspect ratio of the source rectangle, so "fill" crops the source to the window's shape.
        static void ApplyFit()
        {
            if (engine == null || !metadataLoaded || width <= 0 || height <= 0) return;
            uint vw, vh;
            if (engine.GetNativeVideoSize(out vw, out vh) < 0 || vw == 0 || vh == 0) return;
            uint ax, ay;
            double dw = vw, dh = vh;
            if (engine.GetVideoAspectRatio(out ax, out ay) >= 0 && ax > 0 && ay > 0) dw = dh * ax / ay;   // non-square pixels

            var src = new MFVideoNormalizedRect { left = 0, top = 0, right = 1, bottom = 1 };
            var dst = new MFRect { left = 0, top = 0, right = width, bottom = height };
            switch (fit)
            {
                case FitMode.Fit:
                {
                    double scale = Math.Min(width / dw, height / dh);
                    dst = Centered(dw * scale, dh * scale);
                    break;
                }
                case FitMode.Center:
                {
                    float fx = (float)Math.Min(1.0, width / dw), fy = (float)Math.Min(1.0, height / dh);
                    src = new MFVideoNormalizedRect { left = (1 - fx) / 2, top = (1 - fy) / 2, right = (1 + fx) / 2, bottom = (1 + fy) / 2 };
                    dst = Centered(Math.Min(dw, width), Math.Min(dh, height));
                    break;
                }
                default:   // Fill (and Stretch, which the engine cannot do without distorting)
                {
                    double scale = Math.Max(width / dw, height / dh);
                    float fx = (float)Math.Min(1.0, width / (dw * scale)), fy = (float)Math.Min(1.0, height / (dh * scale));
                    src = new MFVideoNormalizedRect { left = (1 - fx) / 2, top = (1 - fy) / 2, right = (1 + fx) / 2, bottom = (1 + fy) / 2 };
                    break;
                }
            }
            var black = new MFARGB { A = 255 };
            int result = engine.UpdateVideoStream(ref src, ref dst, ref black);
            if (result < 0) Log.Warn("UpdateVideoStream failed 0x" + result.ToString("X8"));
        }

        static MFRect Centered(double cw, double ch)
        {
            int iw = (int)Math.Round(cw), ih = (int)Math.Round(ch);
            int x = (width - iw) / 2, y = (height - ih) / 2;
            return new MFRect { left = x, top = y, right = x + iw, bottom = y + ih };
        }

        // EcoQoS while paused: whatever little runs goes to efficient cores and cannot raise the timer resolution.
        static void SetEcoQos(bool on)
        {
            uint mask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED | Native.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = mask, StateMask = on ? mask : 0 };
            Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
        }

        static void Exit()
        {
            if (exiting) return;
            exiting = true;
            Native.PostQuitMessage(0);
        }

        static void Cleanup()
        {
            exiting = true;
            if (engine != null) { try { engine.Shutdown(); } catch { } try { Marshal.FinalReleaseComObject(engine); } catch { } engine = null; }
            if (surface != null) surface.Dispose();
            if (commandWindow != IntPtr.Zero) Native.DestroyWindow(commandWindow);
        }
    }
}
