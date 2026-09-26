using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using LiveWall.Interop;

namespace LiveWall
{
    // Controller-side handle to one player host process (see PlayerHost). Creating it starts the process; disposing
    // it ends the process, which frees the decoder, its memory and its audio stream at once.
    internal sealed class VideoPlayer : IDisposable
    {
        static int lastId;

        public readonly int Id;
        public readonly string Path;
        public IntPtr Window { get; private set; }     // surface window (owned by the host process)
        public bool Ready { get; private set; }
        public bool FirstFrameReady { get; private set; }
        public bool Failed { get; private set; }
        public bool Disposed { get; private set; }
        public int ErrorHResult { get; private set; }
        public string ErrorText { get; private set; }

        readonly Process process;
        IntPtr command;
        bool wantPlaying;
        FitMode fit;
        int width, height;

        public VideoPlayer(IntPtr controller, IntPtr parent, RECT boundsInParent, IntPtr insertAfter, bool visible,
                           string path, FitMode fit, bool play, Action<VideoPlayer> exited)
        {
            Id = Interlocked.Increment(ref lastId);
            Path = path;
            this.fit = fit;
            width = boundsInParent.Width;
            height = boundsInParent.Height;
            wantPlaying = play;

            var ci = CultureInfo.InvariantCulture;
            string args = string.Join(" ", new[]
            {
                "--host", controller.ToInt64().ToString("X", ci), Id.ToString(ci), parent.ToInt64().ToString("X", ci),
                insertAfter.ToInt64().ToString("X", ci), boundsInParent.Left.ToString(ci), boundsInParent.Top.ToString(ci),
                width.ToString(ci), height.ToString(ci), visible ? "1" : "0", fit.ToString(), play ? "1" : "0",
                "\"" + path + "\""
            });
            if (AppPaths.InstanceSuffix.Length > 0) args += " --data \"" + AppPaths.DataDir + "\"";
            process = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.ExePath, args) { UseShellExecute = false, CreateNoWindow = true },
                EnableRaisingEvents = true
            };
            var ui = SynchronizationContext.Current;
            process.Exited += (s, e) => ui.Post(_ =>
            {
                HasExited = true;
                try { exited(this); } finally { process.Dispose(); }
            }, null);
            process.Start();
            processId = process.Id;
        }

        readonly int processId;
        public int ProcessId { get { return processId; } }
        public bool HasExited { get; private set; }

        // Events from the host (UI thread).
        public void OnHostEvent(int evt, long data)
        {
            switch (evt)
            {
                case PlayerHost.EVT_READY:
                    Window = new IntPtr((int)(data >> 32));
                    command = new IntPtr((int)(data & 0xFFFFFFFF));
                    Ready = true;
                    // Re-send the wanted state in case it changed while the host was starting.
                    Post(wantPlaying ? PlayerHost.WM_HOST_PLAY : PlayerHost.WM_HOST_PAUSE, IntPtr.Zero, IntPtr.Zero);
                    PostFit();
                    break;
                case PlayerHost.EVT_FIRST_FRAME:
                    FirstFrameReady = true;
                    break;
                case PlayerHost.EVT_ERROR:
                    Failed = true;
                    ErrorHResult = (int)(data & 0xFFFFFFFF);
                    ErrorText = DescribeError((uint)(data >> 32), ErrorHResult);
                    break;
                case PlayerHost.EVT_LOST:
                    Failed = true;
                    ErrorText = "the desktop window went away";
                    break;
            }
        }

        public bool IsPlaying { get { return !Disposed && !Failed && wantPlaying; } }

        public void Play()
        {
            if (wantPlaying) return;
            wantPlaying = true;
            Post(PlayerHost.WM_HOST_PLAY, IntPtr.Zero, IntPtr.Zero);
        }

        public void Pause()
        {
            if (!wantPlaying) return;
            wantPlaying = false;
            Post(PlayerHost.WM_HOST_PAUSE, IntPtr.Zero, IntPtr.Zero);
        }

        public void ApplyFit(FitMode mode, int w, int h)
        {
            fit = mode; width = w; height = h;
            PostFit();
        }

        void PostFit()
        {
            Post(PlayerHost.WM_HOST_FIT, new IntPtr((int)fit), new IntPtr(((long)height << 16) | (uint)(width & 0xFFFF)));
        }

        // Z-order / visibility of the host's window is managed from here (asynchronously: never wait on the host).
        public void Show(IntPtr insertAfter)
        {
            if (Window == IntPtr.Zero || !Native.IsWindow(Window)) return;
            Native.SetWindowPos(Window, insertAfter, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_ASYNCWINDOWPOS);
        }

        public bool IsWindowVisible { get { return Window != IntPtr.Zero && Native.IsWindowVisible(Window); } }

        // Make the (already rendering, fully transparent) window opaque; the host answers with EVT_REVEALED.
        public void Reveal() { Post(PlayerHost.WM_HOST_REVEAL, IntPtr.Zero, IntPtr.Zero); }

        void Post(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (Ready && !Disposed) Native.PostMessage(command, msg, wParam, lParam);
        }

        public string Statistics()
        {
            return "pid " + ProcessId + (wantPlaying ? ", playing" : ", paused");
        }

        static string DescribeError(uint code, int hr)
        {
            if (hr == unchecked((int)0x80070002) || hr == unchecked((int)0x80070003))
                return "the file could not be found (0x" + hr.ToString("X8") + ")";
            if (hr == unchecked((int)0x80070005) || hr == unchecked((int)0x80070020))
                return "the file could not be opened: access denied or in use (0x" + hr.ToString("X8") + ")";
            string what;
            switch (code)
            {
                case 1: what = "playback aborted"; break;
                case 2: what = "the file could not be read"; break;
                case 3: what = "the video could not be decoded"; break;
                case 4: what = "the format or codec is not supported"; break;
                case 5: what = "the video is encrypted"; break;
                default: what = "playback failed"; break;
            }
            return what + " (0x" + hr.ToString("X8") + ")";
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (HasExited) return;
            try
            {
                if (process.HasExited) return;
                if (command != IntPtr.Zero && Native.PostMessage(command, PlayerHost.WM_HOST_EXIT, IntPtr.Zero, IntPtr.Zero))
                {
                    // Normally gone within a few milliseconds; make sure a stuck host cannot linger.
                    ThreadPool.QueueUserWorkItem(_ => { try { if (!process.WaitForExit(3000)) process.Kill(); } catch { } });
                }
                else process.Kill();
            }
            catch { }
        }
    }
}
