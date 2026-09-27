using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using LiveWall.Interop;

namespace LiveWall
{
    // Core Audio callbacks arrive on audio worker threads: each one only posts a message to the controller window.
    public sealed class AudioNotifySink : IMMNotificationClient, IAudioSessionNotification, IAudioSessionEvents
    {
        internal IntPtr Window;
        internal uint Message;
        void Post(int kind) { Native.PostMessage(Window, Message, new IntPtr(kind), IntPtr.Zero); }

        int IMMNotificationClient.OnDeviceStateChanged(IntPtr id, uint state) { return 0; }
        int IMMNotificationClient.OnDeviceAdded(IntPtr id) { return 0; }
        int IMMNotificationClient.OnDeviceRemoved(IntPtr id) { return 0; }
        int IMMNotificationClient.OnDefaultDeviceChanged(int flow, int role, IntPtr id)
        {
            if (flow == CoreAudio.eRender && role == CoreAudio.eMultimedia) Post(AudioMonitor.KindDevice);
            return 0;
        }
        int IMMNotificationClient.OnPropertyValueChanged(IntPtr id, PROPERTYKEY key) { return 0; }

        int IAudioSessionNotification.OnSessionCreated(IntPtr session) { Post(AudioMonitor.KindSessions); return 0; }

        int IAudioSessionEvents.OnDisplayNameChanged(IntPtr name, IntPtr context) { return 0; }
        int IAudioSessionEvents.OnIconPathChanged(IntPtr path, IntPtr context) { return 0; }
        int IAudioSessionEvents.OnSimpleVolumeChanged(float volume, int mute, IntPtr context) { return 0; }
        int IAudioSessionEvents.OnChannelVolumeChanged(uint channels, IntPtr volumes, uint changed, IntPtr context) { return 0; }
        int IAudioSessionEvents.OnGroupingParamChanged(IntPtr grouping, IntPtr context) { return 0; }
        int IAudioSessionEvents.OnStateChanged(int state)
        {
            Post(state == CoreAudio.AudioSessionStateExpired ? AudioMonitor.KindSessions : AudioMonitor.KindState);
            return 0;
        }
        int IAudioSessionEvents.OnSessionDisconnected(int reason) { Post(AudioMonitor.KindSessions); return 0; }
    }

    // "Is another app making sound?" for the music. Watches the default render endpoint's audio sessions with
    // notifications only (new sessions, per-session active/inactive, default device changes); peak meters are read
    // only when the controller asks (its coalescable timer runs only while music plays or waits to resume, and only
    // while another session is active). LiveWall's own processes (controller, music host, video hosts) are ignored.
    // Created, used and disposed on the controller's UI thread (COM STA).
    internal sealed class AudioMonitor : IDisposable
    {
        public const int KindState = 1, KindSessions = 2, KindDevice = 3;

        sealed class Session
        {
            public IAudioSessionControl2 Control;
            public IAudioMeterInformation Meter;
            public uint Pid;
            public string Name;
            public bool Registered;
        }

        readonly AudioNotifySink sink;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        IMMDeviceEnumerator enumerator;
        IAudioSessionManager2 manager;
        bool deviceRegistered, sessionsRegistered;

        public AudioMonitor(IntPtr window, uint message)
        {
            sink = new AudioNotifySink { Window = window, Message = message };
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CoreAudio.CLSID_MMDeviceEnumerator));
            deviceRegistered = enumerator.RegisterEndpointNotificationCallback(sink) >= 0;
            AttachDefaultDevice();
        }

        // (Re)binds to the current default render endpoint (also after a default-device change).
        public void AttachDefaultDevice()
        {
            DetachDevice();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out device) < 0 || device == null)
            {
                Log.Info("Music: no audio output device");
                return;
            }
            try
            {
                Guid iid = CoreAudio.IID_IAudioSessionManager2;
                object o;
                if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out o) < 0) return;
                manager = (IAudioSessionManager2)o;
                Refresh();   // the session enumerator must be obtained before session notifications are delivered
                sessionsRegistered = manager.RegisterSessionNotification(sink) >= 0;
            }
            finally { Marshal.ReleaseComObject(device); }
        }

        // Re-reads the session list (new sessions, expired ones). Cheap: a handful of sessions.
        public void Refresh()
        {
            if (manager == null) return;
            IAudioSessionEnumerator list;
            if (manager.GetSessionEnumerator(out list) < 0 || list == null) return;
            var seen = new HashSet<string>();
            try
            {
                int count;
                list.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 control;
                    if (list.GetSession(i, out control) < 0 || control == null) continue;
                    IntPtr idPtr;
                    string id = control.GetSessionInstanceIdentifier(out idPtr) >= 0 ? CoreAudio.TakeString(idPtr) : null;
                    bool duplicate = id == null || !seen.Add(id);
                    if (duplicate || sessions.ContainsKey(id)) { Marshal.ReleaseComObject(control); continue; }
                    uint pid;
                    control.GetProcessId(out pid);
                    // Not ours, and not the system sounds (a notification ding is not "another app playing").
                    if (control.IsSystemSoundsSession() == 0 || IsOwnProcess(pid))
                    {
                        sessions[id] = new Session { Control = control, Pid = pid, Name = "ignored pid " + pid };
                        continue;
                    }
                    var s = new Session { Control = control, Meter = control as IAudioMeterInformation, Pid = pid, Name = ProcessName(pid) };
                    s.Registered = control.RegisterAudioSessionNotification(sink) >= 0;
                    sessions[id] = s;
                }
            }
            finally { Marshal.ReleaseComObject(list); }
            foreach (string gone in new List<string>(sessions.Keys))
                if (!seen.Contains(gone)) { Release(sessions[gone]); sessions.Remove(gone); }
        }

        // Another app has an active audio stream (browsers keep one open while silent, so this is not "audible").
        public bool AnyOtherActive
        {
            get
            {
                foreach (var s in sessions.Values)
                {
                    int state;
                    if (s.Meter != null && s.Control.GetState(out state) >= 0 && state == CoreAudio.AudioSessionStateActive) return true;
                }
                return false;
            }
        }

        // Loudest current peak (0-1) of other apps' active sessions, and which app it is.
        public float LoudestOther(out string who)
        {
            float max = 0;
            who = null;
            foreach (var s in sessions.Values)
            {
                int state;
                float peak;
                if (s.Meter == null || s.Control.GetState(out state) < 0 || state != CoreAudio.AudioSessionStateActive) continue;
                if (s.Meter.GetPeakValue(out peak) >= 0 && peak > max) { max = peak; who = s.Name; }
            }
            return max;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (var s in sessions.Values)
            {
                if (s.Meter == null) continue;
                int state;
                float peak = 0;
                s.Control.GetState(out state);
                s.Meter.GetPeakValue(out peak);
                sb.Append(s.Name).Append(state == CoreAudio.AudioSessionStateActive ? " active " : " idle ").Append(peak.ToString("0.000")).Append("; ");
            }
            return sb.Length == 0 ? "no other audio sessions" : sb.ToString();
        }

        // LiveWall's own processes (controller, music host, video hosts) all run this same exe. Checked once per new
        // session, never cached by pid (pids get reused).
        static bool IsOwnProcess(uint pid)
        {
            if (pid == 0) return false;
            return pid == Native.GetCurrentProcessId() || string.Equals(ProcessPath(pid), AppPaths.ExePath, StringComparison.OrdinalIgnoreCase);
        }

        static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        static string ProcessName(uint pid)
        {
            string path = ProcessPath(pid);
            return (path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : "pid") + " (" + pid + ")";
        }

        void Release(Session s)
        {
            if (s.Registered) { try { s.Control.UnregisterAudioSessionNotification(sink); } catch { } }
            try { Marshal.ReleaseComObject(s.Control); } catch { }
        }

        void DetachDevice()
        {
            foreach (var s in sessions.Values) Release(s);
            sessions.Clear();
            if (manager != null)
            {
                if (sessionsRegistered) { try { manager.UnregisterSessionNotification(sink); } catch { } }
                sessionsRegistered = false;
                try { Marshal.ReleaseComObject(manager); } catch { }
                manager = null;
            }
        }

        public void Dispose()
        {
            DetachDevice();
            if (enumerator != null)
            {
                if (deviceRegistered) { try { enumerator.UnregisterEndpointNotificationCallback(sink); } catch { } }
                try { Marshal.ReleaseComObject(enumerator); } catch { }
                enumerator = null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
