using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LiveWall
{
    // Controller-side handle to one music host process (see MusicHost). Creating it starts the process; disposing it
    // ends the process, which closes its audio stream so the audio device and the system can idle.
    internal sealed class MusicPlayer : IDisposable
    {
        static int lastId, lastSeq;

        public readonly int Id;
        public int ProcessId { get { return processId; } }
        public bool HasExited { get; private set; }
        public bool Disposed { get; private set; }

        readonly Process process;
        readonly int processId;
        readonly StreamWriter input;

        public MusicPlayer(IntPtr controller, int volume, int fadeMs, Action<MusicPlayer> exited)
        {
            Id = Interlocked.Increment(ref lastId);
            var ci = CultureInfo.InvariantCulture;
            string args = "--music " + controller.ToInt64().ToString("X", ci) + " " + Id.ToString(ci);
            if (AppPaths.InstanceSuffix.Length > 0) args += " --data \"" + AppPaths.DataDir + "\"";
            process = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.ExePath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true },
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
            // Our own UTF-8 writer on the pipe (the default one uses the console code page).
            input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Send("volume\t" + volume.ToString(ci));
            SetFade(fadeMs);
        }

        // Returns the load's sequence number (events from the host name the load they are about).
        public int Load(string path, long startMs, bool loop, bool fadeIn, bool play)
        {
            int seq = Interlocked.Increment(ref lastSeq);
            var ci = CultureInfo.InvariantCulture;
            Send("load\t" + seq.ToString(ci) + "\t" + path + "\t" + Math.Max(0, startMs).ToString(ci) + "\t" + (loop ? "1" : "0") + "\t" +
                 (fadeIn ? "1" : "0") + "\t" + (play ? "1" : "0"));
            return seq;
        }

        public void Play() { Send("play"); }
        public void Pause() { Send("pause"); }
        public void SetVolume(int volume) { Send("volume\t" + volume.ToString(CultureInfo.InvariantCulture)); }
        public void SetFade(int fadeMs) { Send("fade\t" + fadeMs.ToString(CultureInfo.InvariantCulture)); }

        // A pipe write returns at once (the host reads on its own thread); a dead host just drops the command.
        void Send(string line)
        {
            if (Disposed || HasExited) return;
            try { input.WriteLine(line); }
            catch (Exception ex) { Log.Debug("Music host input closed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (Disposed) return;
            Send("exit");
            Disposed = true;
            try { input.Dispose(); } catch { }   // EOF: the host exits even if it missed "exit"
            if (HasExited) return;
            // Normally gone within milliseconds; make sure a stuck host cannot linger (it would hold the audio device).
            ThreadPool.QueueUserWorkItem(_ => { try { if (!process.WaitForExit(3000)) process.Kill(); } catch { } });
        }
    }
}
