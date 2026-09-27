using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall
{
    // Music per wallpaper, played by a `--music` child process (MusicHost) that exists only while music plays.
    //
    //  * Source: the wallpaper's own choice ("music.<hash>=") or musicDefault: none, custom files/folders, the video's own
    //    sound (same file, audio only), by theme (mood of the picture -> "<music folder>\<mood>") or random.
    //  * Going silent (other app audible, locked, screen off, sleep, fullscreen, battery/Energy Saver, user pause, a
    //    wallpaper without music): fade out (~1.5 s, the host's only timer), pause, remember track + position, and after
    //    the grace period END the process so the audio stream closes and the device and system can idle.
    //  * Coming back: start the process again, seek to the saved position, fade in.
    //  * Other apps' audio: Core Audio notifications (AudioMonitor). Browsers keep silent streams "active", so while
    //    music plays or waits to resume AND another session is active, peak meters are read on a coalescable ~1.5 s
    //    timer. Nothing runs otherwise.
    internal sealed partial class AppController
    {
        static readonly IntPtr TimerMusicGrace = new IntPtr(30), TimerMusicMeter = new IntPtr(31), TimerMusicRetry = new IntPtr(32),
            TimerMusicCheck = new IntPtr(33);
        static readonly TimeSpan FullscreenSettle = TimeSpan.FromMilliseconds(1500);
        const uint WM_AUDIO_NOTIFY = Native.WM_APP + 23;
        const float AudibleThreshold = 0.005f;                                   // peak (0-1); silent streams read exactly 0
        static readonly TimeSpan AudibleConfirm = TimeSpan.FromMilliseconds(600);   // audible this long before fading out
        static readonly TimeSpan AudibleGap = TimeSpan.FromMilliseconds(1200);      // a pause in speech is still "audible"

        enum HostState { None, Playing, Pausing, Paused }

        // What should play, as worked out for the current wallpaper.
        sealed class MusicWant
        {
            public string Key;                   // identity of the source: same key on the next wallpaper = keep playing
            public string Describe;
            public bool Pending;                 // the mood is still being worked out
            public bool Shuffle, Loop;
            public List<string> Immediate;       // tracks known without touching the disk
            public Func<List<string>> Build;     // otherwise: found on the worker thread
        }

        // The host process and what it has loaded.
        MusicPlayer music;
        HostState hostState;
        int hostSeq;
        string hostTrack;
        bool musicGraceRunning;
        int musicRetries;

        // The queue for the current source.
        string musicKey;
        List<string> musicQueue = new List<string>();
        int musicIndex;
        long musicPositionMs;                    // where musicQueue[musicIndex] continues
        bool musicShuffle, musicLoopTrack;
        string musicReason = "";                 // why it is silent ("" = playing, or nothing to play)
        string musicBaseReason;                  // silent for a reason other than other apps' audio (null = none)
        bool musicReady;                         // this wallpaper has music and its tracks are known
        string musicPendingKey;
        readonly Dictionary<string, List<string>> musicTracks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> musicFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> moodJobs = new HashSet<string>(), moodAiTried = new HashSet<string>();
        int musicErrors;
        bool musicStarted;                       // start-up done: the wallpaper (and so its music) is known

        // Other apps' audio.
        AudioMonitor audio;
        bool audioMonitorFailed, otherAudible, meterRunning, meterSoon;
        DateTime audibleSince = DateTime.MinValue, lastAudibleAt = DateTime.MinValue, meterDue;
        string otherWho;
        DateTime fullscreenSince = DateTime.MinValue;
        string fullscreenClass;

        string CurrentTrack { get { return musicQueue.Count > 0 && musicIndex < musicQueue.Count ? musicQueue[musicIndex] : null; } }

        // ================================================================== public surface for tray / settings

        public bool MusicMuted { get { return settings.Music.Muted; } }
        public int MusicVolume { get { return settings.Music.Volume; } }
        public bool MusicSilencesForOtherAudio { get { return settings.Music.SilenceForOtherAudio; } }
        public bool CanSkipTrack { get { return musicReady && musicQueue.Count > 1; } }
        public bool MusicPlaying { get { return music != null && hostState == HostState.Playing && musicReason.Length == 0; } }
        public MusicSettings MusicSettingsCopy { get { return settings.Music.Clone(); } }

        public string MusicTrackTitle
        {
            get
            {
                string t = CurrentTrack;
                if (t == null) return null;
                return musicKey != null && musicKey.StartsWith("video|") ? "sound of " + Path.GetFileNameWithoutExtension(t) : Path.GetFileNameWithoutExtension(t);
            }
        }

        public string MusicStatus
        {
            get
            {
                if (MusicPlaying) return "Playing: " + MusicTrackTitle;
                if (musicReason.Length > 0) return "Music: " + musicReason;
                return "No music for this wallpaper";
            }
        }

        // Key under which the shown wallpaper's music choice is stored (boards share one).
        public string MusicWallpaperKey { get { return board != null ? "board" : current != null ? current.Path : null; } }
        public bool HasMusicTarget { get { return MusicWallpaperKey != null; } }
        public string MusicWallpaperName { get { return board != null ? "boards" : current != null ? current.Name : null; } }
        public string CurrentWallpaperMusic { get { return settings.Music.OverrideFor(MusicWallpaperKey); } }   // null = default
        public string MusicDefault { get { return settings.Music.Default; } }

        public void ToggleMusicMute()
        {
            settings.Music.Muted = !settings.Music.Muted;
            settings.Save();
            Log.Info(settings.Music.Muted ? "Music paused by user" : "Music resumed by user");
            UpdateMusic();
            UpdateStatus();
        }

        public void NextTrack()
        {
            if (!CanSkipTrack) return;
            AdvanceQueue();
            musicPositionMs = 0;
            musicErrors = 0;
            Log.Info("Music: next track");
            UpdateMusic();   // a different track than the host has: fade out, switch, fade in
            UpdateStatus();
        }

        public void SetMusicVolume(int volume)
        {
            settings.Music.Volume = Math.Max(0, Math.Min(100, volume));
            settings.Save();
            if (music != null) music.SetVolume(settings.Music.Volume);
        }

        public void ToggleMusicSilenceForOtherAudio()
        {
            settings.Music.SilenceForOtherAudio = !settings.Music.SilenceForOtherAudio;
            settings.Save();
            UpdateMusic();
            UpdateStatus();
        }

        // spec null = use the default.
        public void SetCurrentWallpaperMusic(string spec)
        {
            string key = MusicWallpaperKey;
            if (key == null) return;
            string hash = MusicSettings.KeyFor(key);
            if (spec == null || MusicSpec.Kind(spec) == MusicSpec.Default) settings.Music.Overrides.Remove(hash);
            else settings.Music.Overrides[hash] = spec;
            settings.Save();
            Log.Info("Music for " + (board != null ? "boards" : Path.GetFileName(key)) + ": " + (spec == null ? "default" : MusicSpec.Describe(spec)));
            musicFailed.Clear();
            UpdateMusic();
            UpdateStatus();
        }

        public void ChooseCurrentWallpaperMusicFiles(IWin32Window owner)
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MusicLibrary.DialogFilter, Title = "Music for this wallpaper" })
            {
                if (dlg.ShowDialog(owner) == DialogResult.OK && dlg.FileNames.Length > 0) SetCurrentWallpaperMusic(MusicSpec.MakeCustom(dlg.FileNames));
            }
        }

        public void ChooseCurrentWallpaperMusicFolder(IntPtr owner)
        {
            var folders = FolderPicker.Pick(owner, "Music folder for this wallpaper");
            if (folders.Count > 0) SetCurrentWallpaperMusic(MusicSpec.MakeCustom(folders));
        }

        // From the Music window: its settings, plus (if changed) the music of the wallpaper it was opened for.
        public void ApplyMusicSettings(MusicSettings m, string wallpaperKey, bool wallpaperChanged, string wallpaperSpec)
        {
            MusicSettings old = settings.Music;
            m.Muted = old.Muted;                 // tray state
            m.Moods = old.Moods;                 // found in the background meanwhile
            m.Overrides = old.Overrides;
            if (wallpaperChanged && wallpaperKey != null)
            {
                string hash = MusicSettings.KeyFor(wallpaperKey);
                if (wallpaperSpec == null || MusicSpec.Kind(wallpaperSpec) == MusicSpec.Default) m.Overrides.Remove(hash);
                else m.Overrides[hash] = wallpaperSpec;
            }
            settings.Music = m;
            settings.Save();
            musicTracks.Clear();     // look at the folders again when a source is chosen next
            musicFailed.Clear();
            if (m.AskAi != old.AskAi || m.AiKey != old.AiKey || m.AiModel != old.AiModel) moodAiTried.Clear();
            if (music != null && m.Volume != old.Volume) music.SetVolume(m.Volume);
            if (m.GraceSeconds != old.GraceSeconds && musicGraceRunning) { StopGrace(); }
            Log.Info("Music settings applied (default " + MusicSpec.Describe(m.Default) + ", volume " + m.Volume + ")");
            UpdateMusic();
            UpdateStatus();
        }

        MusicForm musicForm;

        public void ShowMusicSettings()
        {
            if (musicForm != null && !musicForm.IsDisposed) { musicForm.Activate(); return; }
            musicForm = new MusicForm(this, settings.Music.Clone());
            musicForm.FormClosed += (s, e) => { musicForm = null; TrimSoon(); };
            musicForm.Show();
            musicForm.Activate();
        }

        public string CurrentWallpaperMood
        {
            get
            {
                if (current == null || board != null) return null;
                string m;
                return settings.Music.Moods.TryGetValue(MusicSettings.KeyFor(current.Path), out m) ? m : null;
            }
        }

        // ================================================================== what should play

        MusicWant WantedMusic()
        {
            var m = settings.Music;
            MediaItem item = board == null ? current : null;
            string wallKey = MusicWallpaperKey;
            string spec = m.OverrideFor(wallKey);
            if (spec == null || MusicSpec.Kind(spec) == MusicSpec.Default) spec = m.Default;
            string folder = m.EffectiveFolder;
            switch (MusicSpec.Kind(spec))
            {
                case MusicSpec.Video:
                    // Pictures, GIFs (converted without sound) and boards have no soundtrack.
                    if (item == null || item.Kind != MediaKind.Video) return null;
                    return new MusicWant { Key = "video|" + item.Path, Describe = "the video's own sound", Loop = true, Immediate = new List<string> { item.Path } };
                case MusicSpec.Random:
                    return new MusicWant { Key = "random|" + folder, Describe = "random music from " + folder, Shuffle = true, Build = () => MusicLibrary.Scan(folder) };
                case MusicSpec.Theme:
                {
                    string mood = MoodFor(item);
                    if (mood == null) return new MusicWant { Pending = true, Describe = "finding the mood" };
                    return new MusicWant { Key = "theme|" + mood + "|" + folder, Describe = mood + " music", Shuffle = true, Build = () => MusicLibrary.ForMood(folder, mood) };
                }
                case MusicSpec.Custom:
                {
                    var paths = MusicSpec.CustomPaths(spec);
                    if (paths.Count == 0) return null;
                    return new MusicWant { Key = "custom|" + string.Join("|", paths), Describe = MusicSpec.Describe(spec), Build = () => MusicLibrary.Expand(paths) };
                }
                default:
                    return null;
            }
        }

        // The wallpaper's mood tag, or null while it is being worked out (UpdateMusic runs again when it is known).
        string MoodFor(MediaItem item)
        {
            if (item == null) return "calm";   // boards: quiet music to write by
            var m = settings.Music;
            string hash = MusicSettings.KeyFor(item.Path), cached;
            bool haveCached = m.Moods.TryGetValue(hash, out cached);
            bool useAi = m.AskAi && m.AiKey.Length > 0;
            if (haveCached && (!useAi || cached.EndsWith(" ai") || moodAiTried.Contains(hash))) return cached.Split(' ')[0];
            StartMoodAnalysis(item, hash, useAi);
            return haveCached ? cached.Split(' ')[0] : null;   // the offline tag meanwhile
        }

        void StartMoodAnalysis(MediaItem item, string hash, bool useAi)
        {
            if (!moodJobs.Add(hash)) return;
            string key = useAi ? Dpapi.Unprotect(settings.Music.AiKey) : "", model = settings.Music.AiModel;
            string video = item.Kind == MediaKind.Video ? item.Path : null;
            string image = item.Kind == MediaKind.Video ? item.SnapshotPath : item.Path;   // a GIF's first frame, or the picture
            string name = item.Name, mood = null, how = "";
            worker.Enqueue("mood:" + hash, true, () =>
            {
                if (video != null && !File.Exists(image)) MediaWorker.ExtractSnapshot(video, image);
                if (!File.Exists(image)) return false;
                IMoodAnalyzer online = key.Length > 0 ? new ClaudeMoodAnalyzer(key, model) : null;
                if (online != null) { mood = online.Analyze(image); how = " ai"; }
                if (mood == null) { mood = new LocalMoodAnalyzer().Analyze(image); how = ""; }
                return true;
            },
            ok =>
            {
                moodJobs.Remove(hash);
                if (useAi) moodAiTried.Add(hash);
                settings.Music.Moods[hash] = (mood ?? "unknown") + how;   // "unknown": the theme falls back to random
                settings.Save();
                Log.Info("Music: mood of " + name + " is " + (mood ?? "unknown") + (how.Length > 0 ? " (asked Claude)" : " (from its colors)"));
                UpdateMusic();
                UpdateStatus();
            });
        }

        // Tracks for a source, or null while the worker looks for them (UpdateMusic runs again when found).
        List<string> TracksFor(MusicWant want)
        {
            List<string> t;
            if (musicTracks.TryGetValue(want.Key, out t)) return t;
            if (want.Immediate != null) { musicTracks[want.Key] = want.Immediate; return want.Immediate; }
            if (musicPendingKey == want.Key) return null;
            musicPendingKey = want.Key;
            string key = want.Key;
            var build = want.Build;
            List<string> found = null;
            worker.Enqueue("music:" + key, true, () => { found = build(); return true; }, ok =>
            {
                if (musicPendingKey == key) musicPendingKey = null;
                musicTracks[key] = found ?? new List<string>();
                Log.Info("Music: " + musicTracks[key].Count + " track(s) for " + key);
                UpdateMusic();
                UpdateStatus();
            });
            return null;
        }

        void SelectSource(MusicWant want, List<string> tracks)
        {
            if (want.Key == musicKey && musicQueue.Count > 0) return;   // same source as before: keep playing
            musicKey = want.Key;
            musicShuffle = want.Shuffle;
            musicQueue = new List<string>(tracks);
            if (musicShuffle) Shuffle(musicQueue, null);
            musicIndex = 0;
            musicPositionMs = 0;
            musicErrors = 0;
            musicLoopTrack = want.Loop || musicQueue.Count == 1;
            foreach (string k in musicTracks.Keys.Where(k => !string.Equals(k, want.Key, StringComparison.OrdinalIgnoreCase)).ToList())
                musicTracks.Remove(k);   // look at other folders again when they are chosen again
            Log.Info("Music source: " + want.Describe + " (" + musicQueue.Count + " track" + (musicQueue.Count == 1 ? "" : "s") + ")");
        }

        void AdvanceQueue()
        {
            if (musicQueue.Count == 0) return;
            musicIndex++;
            if (musicIndex < musicQueue.Count) return;
            musicIndex = 0;
            if (musicShuffle && musicQueue.Count > 1) Shuffle(musicQueue, musicQueue[musicQueue.Count - 1]);
        }

        void Shuffle(List<string> list, string notFirst)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = random.Next(i + 1); string t = list[i]; list[i] = list[j]; list[j] = t; }
            if (notFirst != null && list.Count > 1 && list[0] == notFirst) { list.RemoveAt(0); list.Add(notFirst); }   // no immediate repeat
        }

        // ================================================================== the decision

        // Idempotent and cheap: called on every state change (Evaluate, wallpaper changes, host and audio events).
        void UpdateMusic()
        {
            if (exiting || !musicStarted) return;   // not before the first wallpaper is up (OnStart)
            MusicWant want = WantedMusic();
            List<string> tracks = null;
            string reason = null;
            bool preparing = false;
            if (want == null) reason = "";
            else if (want.Pending) { reason = "finding the mood"; preparing = true; }
            else if (musicFailed.Contains(want.Key)) reason = "could not be played";
            else if (want.Key == musicKey && musicQueue.Count > 0) tracks = musicQueue;
            else if ((tracks = TracksFor(want)) == null) { reason = "looking for music"; preparing = true; }
            else if (tracks.Count == 0) reason = "no music found in " + settings.Music.EffectiveFolder;
            musicReady = reason == null;

            musicBaseReason = want != null ? BaseSilence() : null;
            if (musicBaseReason != null) { reason = musicBaseReason; preparing = false; }
            UpdateAudioMonitor(want != null && settings.Music.SilenceForOtherAudio);
            if (!settings.Music.SilenceForOtherAudio || audio == null) otherAudible = false;

            if (musicReady) SelectSource(want, tracks);
            if (reason == null && audio != null && !otherAudible && hostState != HostState.Playing) PrecheckOtherAudio();
            if (reason == null && otherAudible) reason = "another app is playing sound";

            musicReason = reason ?? "";
            if (reason == null) PlayMusic();
            else if (preparing && hostState == HostState.Playing) { }   // keep the current music until the next source is known
            else SilenceMusic(reason);
            ScheduleMeter();
        }

        // Silence that does not depend on other apps' audio.
        string BaseSilence()
        {
            var m = settings.Music;
            if (m.Muted) return "paused";
            if (power.SessionLocked) return "locked";
            if (power.DisplayOff) return "screen off";
            if (power.Suspending) return "sleeping";
            if (m.PauseOnBattery && power.OnBattery) return "on battery";
            if (m.PauseOnEnergySaver && power.SaverOn) return "Energy Saver";
            // (The drawing editor is LiveWall's own full-screen window: not a reason.)
            if (m.PauseOnFullscreen && editor == null && FullscreenSettled()) return "fullscreen app";
            return null;
        }

        // A fullscreen window must stay for a moment before playing music fades out (splash screens and other brief
        // full-screen windows must not cause a dip); a one-shot timer looks again then.
        bool FullscreenSettled()
        {
            string cls;
            if (!Occlusion.FullscreenAppRunning(EnumerateMonitors(), out cls)) { fullscreenSince = DateTime.MinValue; return false; }
            DateTime now = DateTime.UtcNow;
            if (fullscreenSince == DateTime.MinValue) fullscreenSince = now;
            TimeSpan left = FullscreenSettle - (now - fullscreenSince);
            if (left <= TimeSpan.Zero || hostState != HostState.Playing)
            {
                if (fullscreenClass != cls) { fullscreenClass = cls; Log.Info("Music: fullscreen window " + cls); }
                return true;
            }
            Native.SetTimer(window.Handle, TimerMusicCheck, (uint)left.TotalMilliseconds + 20, IntPtr.Zero);
            return false;
        }

        void PlayMusic()
        {
            StopGrace();
            string track = CurrentTrack;
            if (track == null) return;
            if (music == null)
            {
                if (!StartMusicHost()) return;
                LoadTrack(track, musicPositionMs, true);
                return;
            }
            if (string.Equals(hostTrack, track, StringComparison.OrdinalIgnoreCase))
            {
                if (hostState == HostState.Pausing || hostState == HostState.Paused)
                {
                    music.Play();   // fade in (or turn a fade-out around)
                    hostState = HostState.Playing;
                    Log.Info("Music: fading in " + Path.GetFileName(track));
                }
                return;
            }
            // The host has another track: fade it out first; OnMusicEvent(EVT_PAUSED) comes back here to switch.
            if (hostState == HostState.Playing) { music.Pause(); hostState = HostState.Pausing; Log.Debug("Music: crossfade, fading out"); return; }
            if (hostState == HostState.Pausing) return;
            LoadTrack(track, musicPositionMs, true);
        }

        void LoadTrack(string track, long positionMs, bool fadeIn)
        {
            hostTrack = track;
            hostSeq = music.Load(track, positionMs, musicLoopTrack, fadeIn, true);
            hostState = HostState.Playing;
            Log.Info("Music: " + (positionMs > 0 ? "resuming " : "playing ") + Path.GetFileName(track) +
                     (positionMs > 0 ? " at " + TimeSpan.FromMilliseconds(positionMs).ToString(@"m\:ss") : "") + (fadeIn ? " (fade in)" : ""));
        }

        void SilenceMusic(string reason)
        {
            if (music == null) return;
            if (hostState == HostState.Playing)
            {
                music.Pause();   // fade out, pause, EVT_PAUSED with the position
                hostState = HostState.Pausing;
                Log.Info("Music: fading out (" + (reason.Length > 0 ? reason : "no music for this wallpaper") + ")");
                return;
            }
            if (hostState == HostState.Paused || hostState == HostState.None) StartGrace();
        }

        bool StartMusicHost()
        {
            try
            {
                music = new MusicPlayer(window.Handle, settings.Music.Volume, OnMusicExited);
                hostState = HostState.None;
                hostTrack = null;
                Log.Info("Music: started the music process (pid " + music.ProcessId + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Music: could not start the music process", ex);
                music = null;
                return false;
            }
        }

        // Paused: after the grace period the process ends (its audio stream closes with it).
        void StartGrace()
        {
            if (musicGraceRunning) return;
            musicGraceRunning = true;
            Native.SetCoalescableTimer(window.Handle, TimerMusicGrace, (uint)(settings.Music.GraceSeconds * 1000), IntPtr.Zero, 500);
        }

        void StopGrace()
        {
            if (!musicGraceRunning) return;
            musicGraceRunning = false;
            Native.KillTimer(window.Handle, TimerMusicGrace);
        }

        void OnMusicGrace()
        {
            StopGrace();
            if (music == null || hostState == HostState.Playing || hostState == HostState.Pausing) return;
            Log.Info("Music: silent for " + settings.Music.GraceSeconds + " s, ending the music process (pid " + music.ProcessId + ")" +
                     (CurrentTrack != null ? "; will resume " + Path.GetFileName(CurrentTrack) + " at " + TimeSpan.FromMilliseconds(musicPositionMs).ToString(@"m\:ss") : ""));
            DisposeMusicHost();
            TrimSoon();
            ScheduleMeter();
        }

        void DisposeMusicHost()
        {
            if (music != null) { music.Dispose(); music = null; }
            hostState = HostState.None;
            hostTrack = null;
            StopGrace();
        }

        // ================================================================== host events

        void OnMusicEvent(int playerId, int evt, int seq, int data)
        {
            if (music == null || playerId != music.Id) return;   // an old host that is on its way out
            switch (evt)
            {
                case MusicHost.EVT_READY:
                    Log.Debug("Music host ready (pid " + music.ProcessId + ")");
                    break;
                case MusicHost.EVT_PLAYING:
                    if (seq == hostSeq) { musicErrors = 0; musicRetries = 0; }
                    UpdateStatus();
                    break;
                case MusicHost.EVT_PAUSED:
                    if (seq == hostSeq)
                    {
                        if (string.Equals(hostTrack, CurrentTrack, StringComparison.OrdinalIgnoreCase)) musicPositionMs = data;
                        if (hostState == HostState.Pausing) hostState = HostState.Paused;   // (a later "play" already turned it around)
                        Log.Debug("Music: paused at " + TimeSpan.FromMilliseconds(data).ToString(@"m\:ss"));
                    }
                    UpdateMusic();   // switches track (crossfade), or starts the grace period
                    UpdateStatus();
                    break;
                case MusicHost.EVT_ENDED:
                    if (seq != hostSeq) break;
                    AdvanceQueue();
                    musicPositionMs = 0;
                    if (hostState == HostState.Playing && musicReason.Length == 0 && CurrentTrack != null) LoadTrack(CurrentTrack, 0, false);
                    else { hostState = HostState.Paused; UpdateMusic(); }
                    UpdateStatus();
                    break;
                case MusicHost.EVT_ERROR:
                {
                    if (seq != hostSeq) break;
                    string bad = hostTrack;
                    Log.Warn("Music: cannot play " + Path.GetFileName(bad) + ": " + (data == MusicHost.ErrNoAudio ? "it has no sound" : "0x" + data.ToString("X8")));
                    hostState = HostState.Paused;
                    hostTrack = null;
                    int i = musicQueue.FindIndex(t => string.Equals(t, bad, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                    {
                        musicQueue.RemoveAt(i);
                        if (musicIndex > i || musicIndex >= musicQueue.Count) musicIndex = musicIndex > i ? musicIndex - 1 : 0;
                    }
                    musicPositionMs = 0;
                    if (musicQueue.Count == 0 || ++musicErrors >= 5)
                    {
                        Log.Warn("Music: giving up on " + musicKey);
                        musicFailed.Add(musicKey);
                        musicQueue.Clear();
                    }
                    UpdateMusic();
                    UpdateStatus();
                    break;
                }
                case MusicHost.EVT_DEVICE:
                    Log.Warn("Music: audio output problem (0x" + data.ToString("X8") + ")");   // the host exits; OnMusicExited restarts it
                    break;
            }
        }

        void OnMusicExited(MusicPlayer p)
        {
            if (p != music || exiting) return;   // ended on purpose
            Log.Warn("Music process " + p.ProcessId + " ended unexpectedly");
            music = null;
            hostState = HostState.None;
            hostTrack = null;
            StopGrace();
            if (musicReason.Length == 0 && musicRetries++ < 3) Native.SetTimer(window.Handle, TimerMusicRetry, 2000, IntPtr.Zero);
            UpdateStatus();
        }

        // ================================================================== other apps' audio

        void UpdateAudioMonitor(bool want)
        {
            if (want && audio == null && !audioMonitorFailed)
            {
                try
                {
                    audio = new AudioMonitor(window.Handle, WM_AUDIO_NOTIFY);
                    Log.Info("Music: watching other apps' audio (" + audio.Describe() + ")");
                }
                catch (Exception ex)
                {
                    Log.Error("Music: cannot watch other apps' audio", ex);
                    audioMonitorFailed = true;
                    audio = null;
                }
            }
            else if (!want && audio != null)
            {
                audio.Dispose();
                audio = null;
                otherAudible = false;
                audibleSince = DateTime.MinValue;
                Log.Debug("Music: stopped watching other apps' audio");
            }
        }

        void OnAudioNotify(int kind)
        {
            if (audio == null) return;
            if (kind == AudioMonitor.KindDevice) { Log.Info("Music: default audio device changed"); audio.AttachDefaultDevice(); }
            else if (kind == AudioMonitor.KindSessions) audio.Refresh();
            meterSoon = true;   // something started or stopped: look now rather than at the next tick
            ScheduleMeter();
        }

        // About to start or resume: is another app audible right now? (No confirmation needed to stay quiet.)
        void PrecheckOtherAudio()
        {
            if (!audio.AnyOtherActive) return;
            string who;
            float peak = audio.LoudestOther(out who);
            if (peak <= AudibleThreshold) return;
            otherAudible = true;
            audibleSince = lastAudibleAt = DateTime.UtcNow;
            otherWho = who;
            Log.Info("Music: " + who + " is playing sound; waiting");
        }

        // The meter timer runs only while it can matter: music is playing or waiting to resume, nothing else silences
        // it, and (except for the one-shot resume check) another app has an active audio session.
        void ScheduleMeter()
        {
            uint ms = 0, tolerance = 0;
            if (audio != null && musicReady && musicBaseReason == null)
            {
                DateTime now = DateTime.UtcNow;
                bool active = audio.AnyOtherActive;
                if (otherAudible)
                {
                    if (active) { ms = 1500; tolerance = 500; }
                    else
                    {
                        double left = (lastAudibleAt.AddSeconds(settings.Music.ResumeSeconds) - now).TotalMilliseconds;
                        ms = (uint)Math.Max(50, Math.Min(int.MaxValue, left)); tolerance = 250;
                    }
                }
                else if (active && hostState == HostState.Playing)
                {
                    if (meterSoon) { ms = 250; tolerance = 50; }
                    else if (audibleSince != DateTime.MinValue) { ms = 300; tolerance = 100; }   // confirming
                    else { ms = 1500; tolerance = 500; }
                }
            }
            meterSoon = false;
            if (ms == 0)
            {
                if (meterRunning) { Native.KillTimer(window.Handle, TimerMusicMeter); meterRunning = false; }
                if (!otherAudible) audibleSince = DateTime.MinValue;
                return;
            }
            DateTime due = DateTime.UtcNow.AddMilliseconds(ms);
            if (meterRunning && meterDue <= due.AddMilliseconds(tolerance)) return;   // already due by then: frequent events must not postpone it
            Native.SetCoalescableTimer(window.Handle, TimerMusicMeter, ms, IntPtr.Zero, tolerance);
            meterRunning = true;
            meterDue = due;
        }

        void OnMeterTimer()
        {
            Native.KillTimer(window.Handle, TimerMusicMeter);
            meterRunning = false;
            if (audio == null) return;
            DateTime now = DateTime.UtcNow;
            string who;
            float peak = audio.LoudestOther(out who);
            bool audible = peak > AudibleThreshold;
            if (audible)
            {
                if (audibleSince == DateTime.MinValue || now - lastAudibleAt > AudibleGap) audibleSince = now;
                lastAudibleAt = now;
                otherWho = who;
            }
            else if (!otherAudible && audibleSince != DateTime.MinValue && now - lastAudibleAt > AudibleGap) audibleSince = DateTime.MinValue;

            if (!otherAudible && audible && now - audibleSince >= AudibleConfirm)
            {
                otherAudible = true;
                Log.Info("Music: " + who + " is playing sound (peak " + peak.ToString("0.000") + ")");
                UpdateMusic();
                UpdateStatus();
                return;
            }
            if (otherAudible && now - lastAudibleAt >= TimeSpan.FromSeconds(settings.Music.ResumeSeconds))
            {
                otherAudible = false;
                audibleSince = DateTime.MinValue;
                Log.Info("Music: other apps quiet for " + settings.Music.ResumeSeconds + " s");
                UpdateMusic();
                UpdateStatus();
                return;
            }
            ScheduleMeter();
        }

        // ================================================================== plumbing

        bool OnMusicTimer(IntPtr id)
        {
            if (id == TimerMusicGrace) OnMusicGrace();
            else if (id == TimerMusicMeter) OnMeterTimer();
            else if (id == TimerMusicRetry || id == TimerMusicCheck) { Native.KillTimer(window.Handle, id); UpdateMusic(); UpdateStatus(); }
            else return false;
            return true;
        }

        // Music without a video wallpaper still has to notice fullscreen apps (foreground changes).
        bool MusicWatchesFullscreen { get { return musicReady && settings.Music.PauseOnFullscreen; } }

        string MusicDebugState()
        {
            return "music: " + (musicReason.Length > 0 ? "silent (" + musicReason + ")" : MusicPlaying ? "playing" : "off") +
                   " host=" + (music == null ? "-" : "pid " + music.ProcessId + " " + hostState) +
                   " track=" + (CurrentTrack == null ? "-" : Path.GetFileName(CurrentTrack) + " @" + TimeSpan.FromMilliseconds(musicPositionMs).ToString(@"m\:ss")) +
                   " source=" + (musicKey ?? "-") + " otherAudible=" + otherAudible + " meter=" + meterRunning + " grace=" + musicGraceRunning +
                   " | audio: " + (audio == null ? "not watching" : audio.Describe());
        }

        void ShutdownMusic()
        {
            foreach (var id in new[] { TimerMusicGrace, TimerMusicMeter, TimerMusicRetry, TimerMusicCheck }) Native.KillTimer(window.Handle, id);
            if (musicForm != null && !musicForm.IsDisposed) musicForm.Close();
            if (music != null) { music.Dispose(); music = null; }
            if (audio != null) { audio.Dispose(); audio = null; }
        }
    }
}
