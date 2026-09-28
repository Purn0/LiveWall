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
            TimerMusicCheck = new IntPtr(33), TimerMusicPrefade = new IntPtr(34), TimerMusicHalf = new IntPtr(35);
        static readonly TimeSpan FullscreenSettle = TimeSpan.FromMilliseconds(1500);   // an app in front this long: fade out
        static readonly TimeSpan FrontResumeDelay = TimeSpan.FromMilliseconds(2000);   // gone from the front this long: fade in
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
        int musicIndex;                          // last track taken from the queue
        long musicPositionMs;                    // where CurrentTrack continues
        bool musicShuffle, musicLoopSource;      // musicLoopSource: the video's own sound (always one looping track)
        Func<List<string>> musicBuild;           // finds the current source's tracks again (null: nothing to look for)
        bool musicRescanning;

        // Many wallpapers in a slideshow: each wallpaper gets its own song (interval <= 15 min, repeated) or two
        // (alternating); with one or two wallpapers, or on a board, the queue just plays on.
        readonly List<string> musicSet = new List<string>();
        int musicSetPos;
        string musicSetFor;                      // the wallpaper the set was chosen for
        string lastSetSong;                      // the song of the last set, when the source changed under it

        // The slideshow's current period (set by ScheduleNextAdvance; MinValue = no change coming): two songs split it
        // in halves, and the song fades out just before the wallpaper changes.
        DateTime slideshowStart = DateTime.MinValue, slideshowDue = DateTime.MinValue;
        DateTime prefadeArmed = DateTime.MinValue, halfArmed = DateTime.MinValue;
        string prefadeFor;                       // fading out ahead of this wallpaper's change (the change follows the fade)
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
        // A fullscreen or maximized app in front.
        DateTime frontSince = DateTime.MinValue, frontGoneSince = DateTime.MinValue;
        IntPtr frontWindow, frontOverride;       // frontOverride: the user chose to play music over this window anyway
        string frontWhat;
        IntPtr frontHook;                        // location changes of the foreground window's thread (maximize/restore)
        uint frontHookThread;
        WinEventProc frontHookProc;

        string CurrentTrack
        {
            get
            {
                if (musicSet.Count > 0) return musicSet[Math.Min(musicSetPos, musicSet.Count - 1)];
                return musicQueue.Count > 0 && musicIndex < musicQueue.Count ? musicQueue[musicIndex] : null;
            }
        }

        // A wallpaper's song repeats (for its whole time, or its half); the queue loops only a single track.
        bool LoopCurrent { get { return musicLoopSource || musicSet.Count > 0 || musicQueue.Count == 1; } }

        // 0 = the queue plays on; 1 = one repeating song per wallpaper; 2 = one song for the first half of the
        // wallpaper's time and another for the second half. No slideshow (interval "never"): the queue plays on.
        int SongsPerWallpaper
        {
            get
            {
                if (board != null || PlayableCount < 3 || settings.IntervalMinutes <= 0) return 0;
                return settings.IntervalMinutes <= 15 ? 1 : 2;
            }
        }

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

        public string MusicHotkey { get { return settings.Music.Hotkey; } }

        public void ToggleMusicMute()
        {
            settings.Music.Muted = !settings.Music.Muted;
            settings.Save();
            Log.Info(settings.Music.Muted ? "Music paused by user" : "Music resumed by user");
            UpdateMusic();
            UpdateStatus();
        }

        // The music shortcut (and --music-toggle, e.g. a pinned "LiveWall Music" button): pause / play, with a note
        // naming the song. Quiet only because of the app in front? Then play anyway while that window stays in front.
        public void MusicButton()
        {
            if (!settings.Music.Muted && frontWindow != IntPtr.Zero && musicBaseReason != null && musicBaseReason.EndsWith("app in front"))
            {
                frontOverride = frontWindow;
                Log.Info("Music: playing over " + frontWhat + " (user)");
                UpdateMusic();
                UpdateStatus();
            }
            else ToggleMusicMute();
            string key = string.IsNullOrEmpty(settings.Music.Hotkey) ? "" : "   (" + settings.Music.Hotkey + ")";
            if (settings.Music.Muted) Toast.Show("Music paused" + key);
            else if (MusicPlaying) Toast.Show("\u266A  " + MusicTrackTitle + key);
            else if (musicReason.Length > 0) Toast.Show("Music on - waiting: " + musicReason);
            else Toast.Show("No music for this wallpaper (Settings > Music...)");
        }

        public void NextTrack()
        {
            if (!CanSkipTrack) return;
            AdvanceQueue();
            if (musicSet.Count > 0) TakeSet(musicSet.Count, false);   // this wallpaper's song(s): the next ones
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
            if (m.Hotkey != old.Hotkey) RegisterHotkeys();
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

        // Returns true when the queue was started afresh (a different source).
        bool SelectSource(MusicWant want, List<string> tracks)
        {
            if (want.Key == musicKey && musicQueue.Count > 0) return false;   // same source as before: keep playing
            // Another source with the very same tracks (e.g. two moods without their own subfolders): the same queue goes on.
            if (musicQueue.Count > 0 && tracks.Count == musicQueue.Count && want.Loop == musicLoopSource &&
                new HashSet<string>(musicQueue, StringComparer.OrdinalIgnoreCase).SetEquals(tracks))
            {
                musicKey = want.Key;
                musicBuild = want.Build;
                return false;
            }
            if (musicSet.Count > 0) lastSetSong = CurrentTrack;
            musicKey = want.Key;
            musicShuffle = want.Shuffle;
            musicLoopSource = want.Loop;
            musicBuild = want.Build;
            musicQueue = new List<string>(tracks);
            if (musicShuffle) Shuffle(musicQueue, null);
            musicIndex = 0;
            musicPositionMs = 0;
            musicErrors = 0;
            musicSet.Clear();   // (musicSetFor stays: the next wallpaper still gets a different song)
            foreach (string k in musicTracks.Keys.Where(k => !string.Equals(k, want.Key, StringComparison.OrdinalIgnoreCase)).ToList())
                musicTracks.Remove(k);   // look at other folders again when they are chosen again
            Log.Info("Music source: " + want.Describe + " (" + musicQueue.Count + " track" + (musicQueue.Count == 1 ? "" : "s") + ")");
            return true;
        }

        void AdvanceQueue()
        {
            if (musicQueue.Count == 0) return;
            musicIndex++;
            if (musicIndex < musicQueue.Count) return;
            musicIndex = 0;
            if (musicShuffle && musicQueue.Count > 1) Shuffle(musicQueue, musicQueue[musicQueue.Count - 1]);
            RescanSource();   // a full pass: pick up music added (or moved) since
        }

        // Each wallpaper of a slideshow gets its own song(s); see SongsPerWallpaper.
        void ApplySongSet(bool freshQueue)
        {
            int n = musicLoopSource ? 0 : Math.Min(SongsPerWallpaper, musicQueue.Count);
            string wall = MusicWallpaperKey;
            if (n == 0)
            {
                if (musicSet.Count == 0) return;
                int i = musicQueue.FindIndex(t => string.Equals(t, CurrentTrack, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) musicIndex = i;   // keep the song that is playing; the queue goes on from it
                musicSet.Clear();
                musicSetFor = null;
                return;
            }
            if (musicSetFor == wall && musicSet.Count == n) return;
            bool nextWallpaper = musicSetFor != null && musicSetFor != wall;
            if (nextWallpaper)
            {
                string previous = musicSet.Count > 0 ? CurrentTrack : lastSetSong;
                if (!freshQueue) AdvanceQueue();   // new song(s) for the new wallpaper...
                if (musicQueue.Count > 1 && string.Equals(musicQueue[musicIndex], previous, StringComparison.OrdinalIgnoreCase))
                    AdvanceQueue();                // ...never the one the last wallpaper had
            }
            TakeSet(n, !nextWallpaper);
            musicSetFor = wall;
            Log.Info("Music: " + (n == 1 ? "song for this wallpaper (repeats): " : "songs for this wallpaper: ") +
                     string.Join(" / ", musicSet.Select(Path.GetFileNameWithoutExtension)));
        }

        // The set starts at the current queue position. keepPosition: if the song playing now is still the first one,
        // it goes on from where it is.
        void TakeSet(int n, bool keepPosition)
        {
            string before = CurrentTrack;
            musicSet.Clear();
            musicSet.Add(musicQueue[musicIndex]);
            while (musicSet.Count < n) { AdvanceQueue(); if (musicQueue.Count == 0) break; musicSet.Add(musicQueue[musicIndex]); }
            musicSetPos = 0;
            if (!keepPosition || !string.Equals(before, musicSet[0], StringComparison.OrdinalIgnoreCase)) musicPositionMs = 0;
        }

        // Looks at the source's folders again (a file was missing, or a full pass is done) and merges the result.
        void RescanSource()
        {
            if (musicBuild == null || musicRescanning) return;
            musicRescanning = true;
            string key = musicKey;
            var build = musicBuild;
            List<string> found = null;
            worker.Enqueue("music-rescan:" + key, true, () => { found = build(); return true; }, ok =>
            {
                musicRescanning = false;
                if (found == null || key != musicKey) return;
                MergeTracks(found);
                UpdateMusic();
                UpdateStatus();
            });
        }

        void MergeTracks(List<string> found)
        {
            var now = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(musicQueue, StringComparer.OrdinalIgnoreCase);
            string at = musicQueue.Count > 0 && musicIndex < musicQueue.Count ? musicQueue[musicIndex] : null;
            int removed = musicQueue.RemoveAll(t => !now.Contains(t) && !string.Equals(t, at, StringComparison.OrdinalIgnoreCase));
            var added = found.Where(t => !known.Contains(t)).ToList();
            foreach (string t in added)
            {
                int after = Math.Max(0, musicQueue.FindIndex(x => string.Equals(x, at, StringComparison.OrdinalIgnoreCase))) + 1;
                musicQueue.Insert(musicShuffle ? random.Next(after, musicQueue.Count + 1) : Math.Min(after, musicQueue.Count), t);
            }
            if (!musicShuffle && added.Count > 0) musicQueue.Sort(StringComparer.OrdinalIgnoreCase);   // keep a custom folder in order
            musicIndex = Math.Max(0, musicQueue.FindIndex(x => string.Equals(x, at, StringComparison.OrdinalIgnoreCase)));
            musicTracks[musicKey] = found;
            if (found.Count > 0) musicFailed.Remove(musicKey);
            if (removed > 0 || added.Count > 0) Log.Info("Music: folder changed, " + added.Count + " track(s) added, " + removed + " gone");
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

            if (musicReady) ApplySongSet(SelectSource(want, tracks));
            UpdateFrontHook();
            if (reason == null && audio != null && !otherAudible && hostState != HostState.Playing) PrecheckOtherAudio();
            if (reason == null && otherAudible) reason = "another app is playing sound";
            if (reason == null && prefadeFor != null) reason = "changing wallpaper";   // faded out; the change comes next

            musicReason = reason ?? "";
            if (reason == null) PlayMusic();
            else if (preparing && hostState == HostState.Playing) { }   // keep the current music until the next source is known
            else SilenceMusic(reason);
            ScheduleMeter();
            ScheduleSlideshowMusic();
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
            if (m.PauseOnFullscreen && editor == null) return AppInFrontSettled();
            return null;
        }

        // "maximized app in front" / "fullscreen app in front", or null. The window must stay in front for a moment
        // before playing music fades out (Alt+Tab, splash screens: no dip); a one-shot timer looks again then.
        string AppInFrontSettled()
        {
            string what;
            bool transient;
            IntPtr fg = Occlusion.AppInFront(out what, out transient);
            if (transient) fg = frontWindow;   // taskbar, Start, Alt+Tab: nothing changes (e.g. clicking a pinned "LiveWall Music")
            else if (fg != IntPtr.Zero) frontGoneSince = DateTime.MinValue;
            else if (frontWindow != IntPtr.Zero && frontWindow != frontOverride && hostState != HostState.Playing && Native.IsWindow(frontWindow))
            {
                // The app left the front: wait a moment before the music comes back (a quick look elsewhere: no fade in/out).
                DateTime now = DateTime.UtcNow;
                if (frontGoneSince == DateTime.MinValue) frontGoneSince = now;
                TimeSpan wait = FrontResumeDelay - (now - frontGoneSince);
                if (wait > TimeSpan.Zero)
                {
                    Native.SetTimer(window.Handle, TimerMusicCheck, (uint)wait.TotalMilliseconds + 20, IntPtr.Zero);
                    fg = frontWindow;
                }
                else frontGoneSince = DateTime.MinValue;
            }
            if (fg != frontWindow)
            {
                frontWindow = fg;
                frontSince = DateTime.UtcNow;
                if (fg != IntPtr.Zero && fg != frontOverride) frontOverride = IntPtr.Zero;   // another app: the user's "play anyway" ends
                if (fg != IntPtr.Zero && what != frontWhat) Log.Info("Music: " + what + " in front");
                frontWhat = what;
            }
            if (fg != IntPtr.Zero && !Native.IsWindow(fg)) { frontWindow = fg = IntPtr.Zero; }
            if (fg == IntPtr.Zero || fg == frontOverride) return null;
            TimeSpan left = FullscreenSettle - (DateTime.UtcNow - frontSince);
            if (left > TimeSpan.Zero && hostState == HostState.Playing)
            {
                Native.SetTimer(window.Handle, TimerMusicCheck, (uint)left.TotalMilliseconds + 20, IntPtr.Zero);
                return null;
            }
            return frontWhat != null && frontWhat.StartsWith("maximized") ? "maximized app in front" : "fullscreen app in front";
        }

        // Maximize / restore of the window in front sends no foreground or minimize event: while the music watches for
        // it, listen to that one window thread's location changes (a callback per move; nothing otherwise).
        void UpdateFrontHook()
        {
            uint pid = 0, tid = 0;
            if (MusicWatchesFullscreen)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero) tid = Native.GetWindowThreadProcessId(fg, out pid);
                if (pid == Native.GetCurrentProcessId()) tid = 0;
            }
            if (tid == frontHookThread) return;
            if (frontHook != IntPtr.Zero) { Native.UnhookWinEvent(frontHook); frontHook = IntPtr.Zero; }
            frontHookThread = 0;
            if (tid == 0) return;
            if (frontHookProc == null) frontHookProc = OnFrontWindowMoved;
            frontHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, frontHookProc,
                                               pid, tid, Native.WINEVENT_OUTOFCONTEXT);
            if (frontHook != IntPtr.Zero) frontHookThread = tid;
        }

        void OnFrontWindowMoved(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd != Native.GetForegroundWindow()) return;   // carets, child windows
            ScheduleEvaluate(300);   // after the move/resize settles
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
            hostSeq = music.Load(track, positionMs, LoopCurrent, fadeIn, true);
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
                    if (prefadeFor != null && hostState == HostState.Paused) FinishPrefade();   // silent now: change the wallpaper
                    UpdateMusic();   // switches track (crossfade), or starts the grace period
                    UpdateStatus();
                    break;
                case MusicHost.EVT_ENDED:
                    if (seq != hostSeq) break;
                    if (musicSet.Count == 0) AdvanceQueue();   // (a wallpaper's song repeats; it ends only if loaded before its set)
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
                    if (musicSet.RemoveAll(t => string.Equals(t, bad, StringComparison.OrdinalIgnoreCase)) > 0) { musicSet.Clear(); musicSetFor = null; }
                    musicPositionMs = 0;
                    // Moved or renamed since the folder was read: read it again (the file may be there under a new name).
                    bool missing = data == unchecked((int)0x80070002) || data == unchecked((int)0x80070003);
                    if (missing) RescanSource(); else musicErrors++;
                    if (musicQueue.Count == 0 || musicErrors >= 5)
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
            else if (id == TimerMusicHalf) { Native.KillTimer(window.Handle, id); halfArmed = DateTime.MinValue; OnHalfTime(); }
            else if (id == TimerMusicPrefade) { Native.KillTimer(window.Handle, id); prefadeArmed = DateTime.MinValue; OnPrefade(); }
            else return false;
            return true;
        }

        // ================================================================== slideshow timing

        // Two one-shot timers, armed only when they can do something: the half-way switch to a wallpaper's second song,
        // and the fade-out 1.5 s before the slideshow changes a wallpaper whose song goes with it.
        void ScheduleSlideshowMusic()
        {
            bool slideshow = slideshowDue != DateTime.MinValue && board == null && editor == null;
            DateTime half = slideshow && musicSet.Count == 2 && musicSetPos == 0
                ? slideshowStart.AddTicks((slideshowDue - slideshowStart).Ticks / 2) : DateTime.MinValue;
            ArmAt(TimerMusicHalf, half, ref halfArmed, 1000);
            if (prefadeFor != null) return;   // its watchdog is running
            bool songChanges = musicSet.Count > 0 || musicLoopSource;
            DateTime pre = slideshow && songChanges && music != null && hostState == HostState.Playing && musicReason.Length == 0
                ? slideshowDue.AddMilliseconds(-MusicHost.FadeMs) : DateTime.MinValue;
            ArmAt(TimerMusicPrefade, pre, ref prefadeArmed, 50);
        }

        void ArmAt(IntPtr id, DateTime at, ref DateTime armed, uint tolerance)
        {
            if (at == armed) return;
            armed = at;
            if (at == DateTime.MinValue) { Native.KillTimer(window.Handle, id); return; }
            double ms = (at - DateTime.UtcNow).TotalMilliseconds;
            Native.SetCoalescableTimer(window.Handle, id, (uint)Math.Max(10, Math.Min(int.MaxValue, ms)), IntPtr.Zero, tolerance);
        }

        void OnHalfTime()
        {
            if (musicSet.Count != 2 || musicSetPos != 0) return;
            musicSetPos = 1;
            musicPositionMs = 0;
            Log.Info("Music: half-way through this wallpaper, next song: " + Path.GetFileNameWithoutExtension(CurrentTrack));
            UpdateMusic();   // a different track than the host has: fade out, switch, fade in
            UpdateStatus();
        }

        // 1.5 s before the change: fade the song out; the wallpaper changes once it is silent (FinishPrefade).
        void OnPrefade()
        {
            if (prefadeFor != null) { Log.Warn("Music: the fade-out before the wallpaper change did not report back"); FinishPrefade(); UpdateMusic(); return; }
            if (music == null || hostState != HostState.Playing || musicReason.Length > 0 || board != null || editor != null ||
                slideshowDue == DateTime.MinValue || SlideshowWouldDefer) return;
            prefadeFor = MusicWallpaperKey;
            music.Pause();
            hostState = HostState.Pausing;
            Log.Info("Music: fading out before the wallpaper changes");
            Native.SetTimer(window.Handle, TimerMusicPrefade, (uint)MusicHost.FadeMs + 2500, IntPtr.Zero);   // watchdog
        }

        void FinishPrefade()
        {
            string wall = prefadeFor;
            prefadeFor = null;
            Native.KillTimer(window.Handle, TimerMusicPrefade);
            prefadeArmed = DateTime.MinValue;
            // Still the same wallpaper (not changed meanwhile by the slideshow timer or by hand): change it now.
            if (wall != null && wall == MusicWallpaperKey && slideshowDue != DateTime.MinValue) OnSlideshowTimer();
        }

        // Music without a video wallpaper still has to notice fullscreen apps (foreground changes).
        bool MusicWatchesFullscreen { get { return musicReady && settings.Music.PauseOnFullscreen; } }

        string MusicDebugState()
        {
            return "music: " + (musicReason.Length > 0 ? "silent (" + musicReason + ")" : MusicPlaying ? "playing" : "off") +
                   " host=" + (music == null ? "-" : "pid " + music.ProcessId + " " + hostState) +
                   " track=" + (CurrentTrack == null ? "-" : Path.GetFileName(CurrentTrack) + " @" + TimeSpan.FromMilliseconds(musicPositionMs).ToString(@"m\:ss")) +
                   " source=" + (musicKey ?? "-") + " songsPerWallpaper=" + SongsPerWallpaper + " set=" + musicSet.Count + "/" + musicSetPos +
                   " nextChange=" + (slideshowDue == DateTime.MinValue ? "-" : ((int)(slideshowDue - DateTime.UtcNow).TotalSeconds) + "s") +
                   " prefade=" + (prefadeFor != null ? "fading" : prefadeArmed != DateTime.MinValue ? "armed" : "-") +
                   " half=" + (halfArmed != DateTime.MinValue ? "armed" : "-") +
                   " otherAudible=" + otherAudible + " meter=" + meterRunning + " grace=" + musicGraceRunning +
                   " front=" + (frontWindow == IntPtr.Zero ? "-" : frontWhat + (frontWindow == frontOverride ? " (playing anyway)" : "")) +
                   " frontHook=" + (frontHook != IntPtr.Zero) + " | audio: " + (audio == null ? "not watching" : audio.Describe());
        }

        void ShutdownMusic()
        {
            foreach (var id in new[] { TimerMusicGrace, TimerMusicMeter, TimerMusicRetry, TimerMusicCheck, TimerMusicPrefade, TimerMusicHalf })
                Native.KillTimer(window.Handle, id);
            if (frontHook != IntPtr.Zero) { Native.UnhookWinEvent(frontHook); frontHook = IntPtr.Zero; }
            if (musicForm != null && !musicForm.IsDisposed) musicForm.Close();
            if (music != null) { music.Dispose(); music = null; }
            if (audio != null) { audio.Dispose(); audio = null; }
        }
    }
}
