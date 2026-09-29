using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LiveWall.Interop;

namespace LiveWall
{
    // Media Engine callbacks arrive on MF worker threads; hop to the music host's thread.
    public sealed class MusicEngineNotify : IMFMediaEngineNotify
    {
        internal IntPtr Window;
        int IMFMediaEngineNotify.EventNotify(uint eventId, IntPtr param1, uint param2)
        {
            switch (eventId)
            {
                case MF.EVENT_LOADEDMETADATA:
                case MF.EVENT_PLAYING:
                case MF.EVENT_ENDED:
                case MF.EVENT_ERROR:
                case MF.EVENT_RESOURCELOST:
                case MF.EVENT_STREAMRENDERINGERROR:
                    Native.PostMessage(Window, MusicHost.WM_ENGINE_EVENT, new IntPtr(eventId),
                        new IntPtr(((long)(uint)param1.ToInt64() << 32) | param2));
                    break;
            }
            return 0;
        }
    }

    // `LiveWall.exe --music <controller> <playerId>`: plays music (or a video's own soundtrack: the same file, audio
    // only) with an audio-only Media Engine, one track at a time. It does only what the controller says; the choice of
    // tracks, crossfades (fade out, switch, fade in) and when to go silent live in the controller (AppController.Music).
    //
    // Why a separate process: an open audio stream keeps the audio device awake and can hold off sleep, and the Media
    // Engine keeps its stream until the process ends. So the controller ends this process a few seconds after the
    // music has faded out and paused. Nothing here runs between fades: no timers, no polling.
    //
    // Controller -> host: one command per line on stdin (UTF-8, tab-separated). Unlike SendMessage, a pipe write never
    // waits for this process, keeps commands in order, carries any file name, and ends (EOF) when the controller dies.
    //   load <seq> <path> <startMs> <loop 0|1> <fadeIn 0|1> <play 0|1>
    //   play              fade in from the current volume (after a pause, or turning a fade-out around)
    //   pause             fade out, pause, then EVT_PAUSED with the position
    //   volume <0-100>
    //   fade <ms>         length of fades
    //   exit
    // Host -> controller: WM_MUSIC_EVENT posted to the controller window,
    //   wParam = (event << 24) | playerId, lParam = (seq of the load it is about << 32) | data.
    internal static class MusicHost
    {
        public const uint WM_MUSIC_EVENT = Native.WM_APP + 22;
        public const int EVT_READY = 1, EVT_PLAYING = 2, EVT_PAUSED = 3, EVT_ENDED = 4, EVT_ERROR = 5, EVT_DEVICE = 6;
        public const int ErrNoAudio = 1;   // EVT_ERROR data: the file has no audio stream
        internal const uint WM_ENGINE_EVENT = Native.WM_APP + 50, WM_COMMAND_LINE = Native.WM_APP + 51, WM_EXIT = Native.WM_APP + 52;
        static int fadeMs = 1500;              // set by the "fade" command
        const uint FadeTickMs = 30;
        static readonly IntPtr TimerFade = new IntPtr(1);

        static IntPtr controller, window;
        static int playerId;
        static IMFMediaEngineEx engine;
        static MusicEngineNotify notify;
        static WndProc proc;
        static readonly Queue<string> commands = new Queue<string>();

        static int seq;                       // the load being played
        static long startMs;
        static bool loaded, wantPlay, fadeInOnStart, fadeInPending, exiting;
        static double target = 0.5;           // the user's volume (0-1)
        static double level;                  // fade position (0-1); engine volume = target * level^2 (sounds even)
        static double fadeFrom, fadeTo;
        static int fadeStart, fadeDuration;
        static bool fading;
        static Action afterFade;

        public static int Run(string[] args)
        {
            try
            {
                controller = new IntPtr(long.Parse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                playerId = int.Parse(args[2], CultureInfo.InvariantCulture);
                Native.SetPriorityClass(Native.GetCurrentProcess(), Native.BELOW_NORMAL_PRIORITY_CLASS);
                SetEcoQos(true);
                MF.Check(MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL), "MFStartup");
                CreateWindow();
                WatchController();
                CreateEngine();
                new Thread(ReadCommands) { IsBackground = true, Name = "music commands" }.Start();
                Send(EVT_READY, 0, 0);

                MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Music host", ex);
                Send(EVT_DEVICE, seq, ex is COMException ? ((COMException)ex).ErrorCode : unchecked((int)0x80004005));
            }
            Cleanup();
            return 0;
        }

        static void Send(int evt, int forSeq, int data)
        {
            Native.PostMessage(controller, WM_MUSIC_EVENT, new IntPtr(((long)evt << 24) | (uint)playerId),
                new IntPtr(((long)forSeq << 32) | (uint)data));
        }

        static void CreateWindow()
        {
            proc = WindowProc;
            var wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = proc;
            wc.hInstance = Native.GetModuleHandle(IntPtr.Zero);
            wc.lpszClassName = "LiveWall.Music";
            Native.RegisterClassEx(ref wc);
            window = Native.CreateWindowEx(0, "LiveWall.Music", "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new InvalidOperationException("music window");
        }

        // Exit if the LiveWall process that started us goes away (the stdin pipe also ends then).
        static void WatchController()
        {
            uint pid;
            Native.GetWindowThreadProcessId(controller, out pid);
            if (pid == 0) throw new InvalidOperationException("controller not found");
            var p = Process.GetProcessById((int)pid);
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => Native.PostMessage(window, WM_EXIT, IntPtr.Zero, IntPtr.Zero);
            if (p.HasExited) throw new InvalidOperationException("controller exited");
        }

        static void CreateEngine()
        {
            var factory = (IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(MF.CLSID_MFMediaEngineClassFactory));
            IMFAttributes attrs;
            MF.Check(MF.MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
            notify = new MusicEngineNotify { Window = window };
            attrs.SetUnk(MF.MF_MEDIA_ENGINE_CALLBACK, notify);
            MF.Check(factory.CreateInstance(MF.MF_MEDIA_ENGINE_AUDIOONLY, attrs, out engine), "Media engine (audio)");
            MF.Release(attrs);
            MF.Release(factory);
            engine.SetAudioStreamCategory(MF.AudioCategory_Media);
            engine.SetAudioEndpointRole(MF.eMultimedia);
            engine.SetAutoPlay(0);
            engine.EnableTimeUpdateTimer(0);   // no periodic "timeupdate" callbacks
            engine.SetVolume(0);
        }

        // Blocks on stdin (no CPU); each line is handed to the window thread.
        static void ReadCommands()
        {
            try
            {
                using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lock (commands) commands.Enqueue(line);
                        Native.PostMessage(window, WM_COMMAND_LINE, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Music host input: " + ex.Message); }
            Native.PostMessage(window, WM_EXIT, IntPtr.Zero, IntPtr.Zero);   // the controller closed the pipe or is gone
        }

        static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_COMMAND_LINE:
                        while (true)
                        {
                            string line;
                            lock (commands) { if (commands.Count == 0) break; line = commands.Dequeue(); }
                            if (!exiting) OnCommand(line.Split('\t'));
                        }
                        return IntPtr.Zero;
                    case WM_ENGINE_EVENT:
                        OnEngineEvent((uint)wParam.ToInt64(), lParam.ToInt64());
                        return IntPtr.Zero;
                    case Native.WM_TIMER:
                        if (wParam == TimerFade) OnFadeTick();
                        return IntPtr.Zero;
                    case WM_EXIT:
                        Exit();
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex) { Log.Error("Music host message 0x" + msg.ToString("X"), ex); }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        static void OnCommand(string[] c)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (c[0])
            {
                case "load":
                    if (c.Length >= 7) Load(int.Parse(c[1], ci), c[2], long.Parse(c[3], ci), c[4] == "1", c[5] == "1", c[6] == "1");
                    break;
                case "play": Play(); break;
                case "pause": Pause(); break;
                case "volume":
                    if (c.Length >= 2) { target = Math.Max(0, Math.Min(100, int.Parse(c[1], ci))) / 100.0; ApplyVolume(); }
                    break;
                case "fade":
                    if (c.Length >= 2) fadeMs = Math.Max(100, Math.Min(10000, int.Parse(c[1], ci)));
                    break;
                case "exit": Exit(); break;
            }
        }

        static void Load(int newSeq, string path, long start, bool loop, bool fadeIn, bool play)
        {
            StopFade();
            afterFade = null;
            seq = newSeq;
            startMs = start;
            wantPlay = play;
            fadeInOnStart = fadeIn;
            fadeInPending = false;
            loaded = false;
            level = fadeIn ? 0 : 1;
            ApplyVolume();
            try
            {
                engine.SetLoop(loop ? 1 : 0);
                IntPtr stream = MF.OpenFile(path);   // byte stream: no URL parsing, so any file name works
                try { MF.Check(engine.SetSourceFromByteStream(stream, MF.TypeHintUrl(path)), "Open track"); }
                finally { Marshal.Release(stream); }
            }
            catch (COMException ex)
            {
                Log.Warn("Music host: " + Path.GetFileName(path) + ": " + ex.Message);
                Send(EVT_ERROR, seq, ex.ErrorCode);
            }
        }

        static void Play()
        {
            wantPlay = true;
            if (!loaded) return;   // starts when the track is open
            if (engine.IsPaused() != 0)
            {
                fadeInPending = true;   // the fade starts when sound actually starts (EVENT_PLAYING)
                SetEcoQos(false);
                engine.Play();
            }
            else FadeTo(1, (int)(fadeMs * (1 - level)), null);   // was fading out: turn around
        }

        static void Pause()
        {
            wantPlay = false;
            fadeInPending = false;
            if (!loaded) { Send(EVT_PAUSED, seq, (int)startMs); return; }   // never started: same position
            if (engine.IsPaused() != 0) { Send(EVT_PAUSED, seq, Position()); return; }
            FadeTo(0, (int)(fadeMs * level), () =>
            {
                engine.Pause();
                SetEcoQos(true);
                Send(EVT_PAUSED, seq, Position());
            });
        }

        static int Position()
        {
            double t = engine.GetCurrentTime();
            return double.IsNaN(t) || t < 0 ? 0 : (int)Math.Min(int.MaxValue, t * 1000);
        }

        static void OnEngineEvent(uint evt, long data)
        {
            if (exiting) return;
            switch (evt)
            {
                case MF.EVENT_LOADEDMETADATA:
                {
                    loaded = true;
                    if (engine.HasAudio() == 0) { Send(EVT_ERROR, seq, ErrNoAudio); return; }
                    double dur = engine.GetDuration();
                    if (startMs > 0 && (double.IsNaN(dur) || double.IsInfinity(dur) || startMs / 1000.0 < dur - 1))
                        engine.SetCurrentTime(startMs / 1000.0);
                    ApplyVolume();
                    if (wantPlay)
                    {
                        fadeInPending = fadeInOnStart;
                        SetEcoQos(false);
                        engine.Play();
                    }
                    break;
                }
                case MF.EVENT_PLAYING:
                {
                    if (fadeInPending) { fadeInPending = false; FadeTo(1, fadeMs, null); }
                    double dur = engine.GetDuration();
                    Send(EVT_PLAYING, seq, double.IsNaN(dur) || double.IsInfinity(dur) ? 0 : (int)Math.Min(int.MaxValue, dur * 1000));
                    break;
                }
                case MF.EVENT_ENDED:
                    SetEcoQos(true);
                    Send(EVT_ENDED, seq, 0);
                    break;
                case MF.EVENT_ERROR:
                    Send(EVT_ERROR, seq, (int)(data & 0xFFFFFFFF));
                    break;
                case MF.EVENT_RESOURCELOST:
                case MF.EVENT_STREAMRENDERINGERROR:
                    // Audio device gone or broken: the controller starts a fresh host if music should still play.
                    Send(EVT_DEVICE, seq, (int)(data & 0xFFFFFFFF));
                    Exit();
                    break;
            }
        }

        // ------------------------------------------------------------------ fades (the only timer, only while fading)

        static void FadeTo(double to, int ms, Action done)
        {
            afterFade = done;
            if (Math.Abs(to - level) < 0.001 || ms <= 0)
            {
                StopFade();
                level = to;
                ApplyVolume();
                afterFade = null;
                if (done != null) done();
                return;
            }
            fadeFrom = level;
            fadeTo = to;
            fadeStart = Environment.TickCount;
            fadeDuration = ms;
            if (!fading)
            {
                fading = true;
                Native.SetTimer(window, TimerFade, FadeTickMs, IntPtr.Zero);
            }
        }

        static void OnFadeTick()
        {
            if (!fading) { Native.KillTimer(window, TimerFade); return; }
            double t = Math.Min(1.0, (Environment.TickCount - fadeStart) / (double)fadeDuration);
            level = fadeFrom + (fadeTo - fadeFrom) * t;
            ApplyVolume();
            if (t < 1) return;
            StopFade();
            Action done = afterFade;
            afterFade = null;
            if (done != null) done();
        }

        static void StopFade()
        {
            if (!fading) return;
            fading = false;
            Native.KillTimer(window, TimerFade);
        }

        static void ApplyVolume()
        {
            if (engine != null) engine.SetVolume(target * level * level);
        }

        // EcoQoS while paused; off while playing so audio delivery is never throttled.
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
            StopFade();
            Native.PostQuitMessage(0);
        }

        static void Cleanup()
        {
            exiting = true;
            if (engine != null) { try { engine.Shutdown(); } catch { } try { Marshal.FinalReleaseComObject(engine); } catch { } engine = null; }
            if (window != IntPtr.Zero) Native.DestroyWindow(window);
        }
    }
}
