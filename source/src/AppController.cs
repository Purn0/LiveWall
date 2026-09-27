using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LiveWall.Interop;
using LiveWall.Ink;

namespace LiveWall
{
    // The brain: owns the playlist, one surface per monitor, and every decision about what plays when.
    //
    // Battery strategy
    //  * Images are handed to Windows as the normal wallpaper: zero cost while displayed.
    //  * Videos (and GIFs, converted once to H.264) are decoded and scaled by the GPU, in a small player process
    //    per screen (see PlayerHost). This process does no per-frame work at all.
    //  * Playback pauses whenever the video is not visible (covered, fullscreen app, locked, display off) or on
    //    battery/Energy Saver if chosen. When it stops playing, the player process is ended (after a minute out of
    //    sight, a few seconds otherwise), releasing the decoder, its memory and its audio stream, while the Windows
    //    wallpaper (= the video's first frame) stands in until it plays again.
    //  * State changes are event-driven, plus a coalescable 1-2 s check while a video is loaded. EcoQoS and
    //    below-normal priority keep the remaining work on efficient cores.
    internal sealed partial class AppController : ApplicationContext
    {
        static readonly IntPtr TimerPoll = new IntPtr(1), TimerSlideshow = new IntPtr(2), TimerEvaluateSoon = new IntPtr(3),
            TimerRebuild = new IntPtr(4), TimerPromote = new IntPtr(5), TimerStart = new IntPtr(6), TimerRetry = new IntPtr(7),
            TimerTrim = new IntPtr(8);
        static readonly TimeSpan PromoteDelay = TimeSpan.FromMilliseconds(100);
        static readonly TimeSpan VisiblePauseUnloadDelay = TimeSpan.FromSeconds(2);
        static readonly TimeSpan AwayUnloadDelay = TimeSpan.FromSeconds(5);   // screen off / locked: nobody is looking

        sealed class Surface
        {
            public RECT Bounds;
            public VideoPlayer Player;           // on screen
            public VideoPlayer NextPlayer;       // loading; replaces Player once its first frame is ready
            public VideoPlayer Retiring;         // previous player, ended once the new one is visibly on screen
            public DateTime NextReadyAt;
            public bool Hidden;                  // this screen's desktop is not visible
            public bool Playing, StateKnown;
            public DateTime StateSince;          // when Hidden/Playing last changed
            public bool Asleep;                  // decoder unloaded while not playing
        }

        readonly bool autostart;
        readonly bool ecoAlways;
        string startupCommand;
        Settings settings;
        readonly MessageWindow window;
        readonly DesktopHost host = new DesktopHost();
        readonly List<Surface> surfaces = new List<Surface>();
        readonly Occlusion occlusion = new Occlusion();
        readonly PowerState power;
        readonly MediaWorker worker;
        readonly SynchronizationContext ui;
        TrayIcon tray;
        SettingsForm settingsForm;
        readonly List<IntPtr> hooks = new List<IntPtr>();
        readonly WinEventProc hookProc;
        readonly uint taskbarCreatedMessage;
        readonly Random random = new Random();

        List<MediaItem> items = new List<MediaItem>();
        List<int> order = new List<int>();
        int orderPos;
        MediaItem current;
        string currentVideo;        // file being played (the video itself or a converted GIF)
        string nativeCurrent;       // what LiveWall last set as the Windows wallpaper
        string convertStatus;
        string pauseReason = "";
        string lastPowerState;
        string debugOcclusion;
        bool userPaused, advanceDeferred, polling, ecoQos, ecoQosKnown, exiting;
        int rebuildAttempts, deviceRetries;
        uint pollInterval;

        public AppController(bool autostart, string command, bool ecoAlways)
        {
            this.autostart = autostart;
            this.ecoAlways = ecoAlways;
            startupCommand = command;
            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ui = SynchronizationContext.Current;

            settings = Settings.Load();
            window = new MessageWindow("LiveWall" + AppPaths.InstanceSuffix, WndProc);
            worker = new MediaWorker(ui);
            power = new PowerState(window.Handle);
            taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

            hookProc = OnWinEvent;
            Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND);
            Hook(Native.EVENT_SYSTEM_MOVESIZEEND, Native.EVENT_SYSTEM_MOVESIZEEND);
            Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND);
            Hook(Native.EVENT_SYSTEM_DESKTOPSWITCH, Native.EVENT_SYSTEM_DESKTOPSWITCH);
            Hook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED);

            Native.SetPriorityClass(Native.GetCurrentProcess(), Native.BELOW_NORMAL_PRIORITY_CLASS);
            SetEcoQos(true);
            if (settings.ShowTrayIcon) tray = new TrayIcon(this);
            InitInk();

            if (!settings.OriginalCaptured)
            {
                string path; int pos;
                NativeWallpaper.GetCurrent(out path, out pos);
                settings.OriginalWallpaper = path;
                settings.OriginalPosition = pos;
                settings.OriginalCaptured = true;
                settings.Save();
                Log.Info("Remembered the original Windows wallpaper: '" + path + "' (position " + pos + ")");
            }

            Log.Info("LiveWall started (autostart=" + autostart + ", " + power + ")");
            playingCollection = EffectiveCollection();
            BuildPlaylist();
            // At sign-in, give Explorer a moment; the Windows wallpaper (the live one's first frame) shows meanwhile.
            Native.SetTimer(window.Handle, TimerStart, autostart ? 2500u : 10u, IntPtr.Zero);
        }

        void Hook(uint min, uint max)
        {
            IntPtr h = Native.SetWinEventHook(min, max, IntPtr.Zero, hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) hooks.Add(h);
        }

        // ================================================================== public surface for tray / settings

        public int PlayableCount { get { return items.Count(i => !i.Failed); } }
        public bool UserPaused { get { return userPaused; } }
        public bool HasLiveWallpaper { get { return currentVideo != null; } }
        public int IntervalMinutes { get { return settings.IntervalMinutes; } }
        public bool ShuffleOn { get { return settings.Shuffle; } }
        public FitMode Fit { get { return settings.Fit; } }

        public string CurrentTitle
        {
            get
            {
                if (board != null) return BoardTitle + (editor != null ? "  (drawing)" : "");
                if (current == null) return items.Count == 0 ? "No wallpapers added yet" : "Nothing playable";
                string state = convertStatus != null ? "converting" : currentVideo == null ? "picture" : pauseReason.Length > 0 ? "paused" : "playing";
                return Shorten(current.Name, 40) + "  (" + state + ")";
            }
        }

        public void Next() { if (board != null) LeaveBoardAndStep(1); else Advance(1); }
        public void Previous() { if (board != null) LeaveBoardAndStep(-1); else Advance(-1); }

        public void TogglePause()
        {
            userPaused = !userPaused;
            Log.Info(userPaused ? "Paused by user" : "Resumed by user");
            Evaluate();
        }

        public void SetInterval(int minutes)
        {
            settings.IntervalMinutes = minutes;
            settings.Save();
            ScheduleNextAdvance();
        }

        public void ToggleShuffle()
        {
            settings.Shuffle = !settings.Shuffle;
            settings.Save();
            BuildOrder(CurrentIndex());
        }

        public void SetFit(FitMode fit)
        {
            if (settings.Fit == fit) return;
            settings.Fit = fit;
            settings.Save();
            ApplyFitEverywhere();
        }

        public void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
                settingsForm.Activate();
                return;
            }
            settingsForm = new SettingsForm(this, settings);
            settingsForm.FormClosed += (s, e) => { settingsForm = null; TrimSoon(); };
            settingsForm.Show();
            settingsForm.Activate();
            UpdateStatus();
        }

        public void AddWallpapersDialog()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaTypes.DialogFilter, Title = "Add wallpapers" })
            {
                if (dlg.ShowDialog(settingsForm) != DialogResult.OK) return;
                var added = dlg.FileNames.Where(f => !settings.Sources.Any(s => string.Equals(s, f, StringComparison.OrdinalIgnoreCase))).ToList();
                if (added.Count == 0) return;
                // Show the first new wallpaper right away.
                string first = Path.GetFullPath(added[0]);
                settings.LastItem = first;
                var s2 = settings.Clone();
                s2.Sources.AddRange(added);
                ApplySettings(s2);
                int idx = items.FindIndex(i => string.Equals(i.Path, first, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && !IsCurrent(items[idx])) { BuildOrder(idx); ShowItem(items[idx]); }
            }
        }

        public void ApplySettings(Settings s)
        {
            Settings old = settings;
            s.LastItem = old.LastItem;
            s.OriginalCaptured = old.OriginalCaptured;
            s.OriginalWallpaper = old.OriginalWallpaper;
            s.OriginalPosition = old.OriginalPosition;
            s.BoardMode = old.BoardMode;
            s.LastBoard = old.LastBoard;
            s.Collections = old.Collections;          // edited in the Collections window
            s.ActiveCollection = old.ActiveCollection;
            s.UserId = old.UserId;
            settings = s;
            settings.Save();

            if (old.ShowTrayIcon != s.ShowTrayIcon)
            {
                if (s.ShowTrayIcon) tray = new TrayIcon(this);
                else if (tray != null) { tray.Dispose(); tray = null; }
            }

            bool sourcesChanged = !old.Sources.SequenceEqual(s.Sources, StringComparer.OrdinalIgnoreCase);
            if (sourcesChanged)
            {
                BuildPlaylist();
                int idx = CurrentIndex();
                if (idx >= 0) BuildOrder(idx);
                else if (board == null) ShowInitial();
                else current = null;   // shown again when leaving the board
            }
            else if (old.Shuffle != s.Shuffle) BuildOrder(CurrentIndex());

            if (old.Fit != s.Fit) ApplyFitEverywhere();
            if (sourcesChanged || old.IntervalMinutes != s.IntervalMinutes) ScheduleNextAdvance();
            if (old.SyncWindowsWallpaper != s.SyncWindowsWallpaper)
            {
                if (s.SyncWindowsWallpaper) { if (current != null && currentVideo != null) SyncNativeToVideo(current); }
                else if (currentVideo != null)
                {
                    foreach (var sf in surfaces.Where(x => x.Asleep).ToList()) Wake(sf);
                    RestoreOriginalNative();
                }
            }
            ApplyInkSettings(old, s);
            Log.Info("Settings applied (" + items.Count + " items, every " + s.IntervalMinutes + " min, fit " + s.Fit + ")");
            Evaluate();
        }

        public void Exit()
        {
            Shutdown();
            ExitThread();
        }

        // ================================================================== playlist

        void BuildPlaylist()
        {
            IList<string> sources = PlaylistSources;
            items = Playlist.Resolve(sources);
            Log.Info("Playlist: " + items.Count + " item(s) from " + sources.Count + " source(s)" + (playingCollection.Length > 0 ? " in " + playingCollection : ""));
            var keys = new HashSet<string>(Playlist.Resolve(AllSources).Select(i => i.CacheKey));
            worker.Enqueue("clean-cache", false, () => { MediaWorker.CleanCache(keys); return true; }, null);
        }

        int CurrentIndex()
        {
            if (current == null) return -1;
            return items.FindIndex(i => string.Equals(i.Path, current.Path, StringComparison.OrdinalIgnoreCase));
        }

        bool IsCurrent(MediaItem item)
        {
            return current != null && item != null && string.Equals(current.Path, item.Path, StringComparison.OrdinalIgnoreCase);
        }

        void BuildOrder(int startIndex)
        {
            order = Enumerable.Range(0, items.Count).ToList();
            if (settings.Shuffle)
            {
                for (int i = order.Count - 1; i > 0; i--) { int j = random.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
                if (startIndex >= 0) { order.Remove(startIndex); order.Insert(0, startIndex); }
            }
            orderPos = startIndex >= 0 ? order.IndexOf(startIndex) : 0;
        }

        MediaItem Step(int dir)
        {
            if (items.Count == 0) return null;
            if (order.Count != items.Count) BuildOrder(CurrentIndex());
            for (int tries = 0; tries < items.Count; tries++)
            {
                orderPos += dir;
                if (orderPos >= order.Count)
                {
                    int last = order.Count > 0 ? order[order.Count - 1] : -1;
                    if (settings.Shuffle)
                    {
                        BuildOrder(-1);
                        if (order.Count > 1 && order[0] == last) { order.RemoveAt(0); order.Add(last); }   // no immediate repeat
                    }
                    orderPos = 0;
                }
                else if (orderPos < 0) orderPos = order.Count - 1;
                var item = items[order[orderPos]];
                if (!item.Failed) return item;
            }
            return null;
        }

        void ShowInitial()
        {
            if (items.Count == 0)
            {
                LeaveBoardState();
                current = null;
                currentVideo = null;
                TearDownSurfaces();
                RefreshInkOverlays();
                UpdateStatus();
                return;
            }
            int idx = InitialIndex();
            BuildOrder(idx);
            ShowItem(items[idx]);
        }

        int InitialIndex()
        {
            if (items.Count == 0) return -1;
            int idx = items.FindIndex(i => string.Equals(i.Path, settings.LastItem, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = settings.Shuffle ? random.Next(items.Count) : 0;
            return idx;
        }

        void Advance(int dir)
        {
            var next = Step(dir);
            if (next == null) return;
            if (IsCurrent(next) && currentVideo != null) { ScheduleNextAdvance(); return; }
            ShowItem(next);
        }

        void ScheduleNextAdvance()
        {
            Native.KillTimer(window.Handle, TimerSlideshow);
            advanceDeferred = false;
            if (board != null || settings.IntervalMinutes <= 0 || PlayableCount < 2) return;
            long ms = Math.Min(int.MaxValue, settings.IntervalMinutes * 60000L);
            Native.SetCoalescableTimer(window.Handle, TimerSlideshow, (uint)ms, IntPtr.Zero, (uint)Math.Min(30000, ms / 50));
        }

        void OnSlideshowTimer()
        {
            Native.KillTimer(window.Handle, TimerSlideshow);
            if (board != null) return;
            if (editor != null) return;   // never under someone drawing: the slideshow restarts when they finish
            // Don't swap videos nobody can see; switch the moment the desktop is visible again.
            if (currentVideo != null && surfaces.Count > 0 && surfaces.All(s => s.Hidden))
            {
                advanceDeferred = true;
                Log.Debug("Slideshow change deferred until the desktop is visible");
                return;
            }
            Advance(1);
        }

        // ================================================================== showing an item

        void ShowItem(MediaItem item)
        {
            LeaveBoardState();
            current = item;
            settings.LastItem = item.Path;
            settings.Save();
            convertStatus = null;
            deviceRetries = 0;
            ScheduleNextAdvance();
            Log.Info("Showing " + MediaTypes.Describe(item.Kind).ToLowerInvariant() + ": " + item.Name);
            RefreshInkOverlays();   // this wallpaper's drawings (if any), above it and below the icons

            if (item.Kind == MediaKind.Video) ShowVideo(item, item.Path);
            else if (item.Kind == MediaKind.Gif && !item.StaticGif && File.Exists(item.ConvertedVideoPath)) ShowVideo(item, item.ConvertedVideoPath);
            else
            {
                ShowPicture(item);
                if (item.Kind == MediaKind.Gif && !item.StaticGif) ConvertGif(item, true);
            }
            Prefetch(PeekNext());
            UpdateStatus();
        }

        MediaItem PeekNext()
        {
            if (items.Count < 2 || order.Count != items.Count || settings.IntervalMinutes <= 0) return null;
            for (int i = 1; i < order.Count; i++)
            {
                var it = items[order[(orderPos + i) % order.Count]];
                if (!it.Failed) return it;
            }
            return null;
        }

        // Prepares the next wallpaper in the background (low priority) so the switch is instant.
        void Prefetch(MediaItem item)
        {
            if (item == null || IsCurrent(item)) return;
            if (item.Kind == MediaKind.Gif && !item.StaticGif && !File.Exists(item.ConvertedVideoPath)) ConvertGif(item, false);
            if (item.Kind == MediaKind.Video)
            {
                string snap = item.SnapshotPath, video = item.Path;
                if (settings.SyncWindowsWallpaper && !File.Exists(snap))
                    worker.Enqueue("snap:" + item.CacheKey, false, () => MediaWorker.ExtractSnapshot(video, snap), null);
            }
        }

        // Pictures (and a GIF's first frame while it converts) are shown by Windows itself.
        void ShowPicture(MediaItem item)
        {
            currentVideo = null;
            SetNative(item.Path, ok =>
            {
                if (!IsCurrent(item) || currentVideo != null) return;
                TearDownSurfaces();   // reveal the new picture only once Windows has it
                if (!ok && item.Kind == MediaKind.Image) MarkFailed(item, "Windows could not use this image as a wallpaper.");
                TrimSoon();
            });
        }

        void ConvertGif(MediaItem item, bool forDisplay)
        {
            string gif = item.Path, mp4 = item.ConvertedVideoPath;
            Size target = LargestMonitor();
            if (forDisplay) convertStatus = "Preparing " + item.Name;
            worker.Enqueue("gif:" + item.CacheKey, forDisplay, () =>
            {
                if (!MediaWorker.IsAnimatedGif(gif)) { item.StaticGif = true; return false; }
                return MediaWorker.ConvertGif(gif, mp4, target.Width, target.Height,
                    pct => ui.Post(_ => { if (IsCurrent(item) && convertStatus != null) { convertStatus = "Converting " + item.Name + " (" + pct + "%)"; UpdateStatus(); } }, null));
            },
            ok =>
            {
                if (!IsCurrent(item) || currentVideo != null || board != null) return;   // prefetch, already playing, or a board is up
                convertStatus = null;
                if (ok) ShowVideo(item, mp4);
                else if (!item.StaticGif) MarkFailed(item, "This GIF could not be converted to video.");
                UpdateStatus();
            });
        }

        void ShowVideo(MediaItem item, string videoPath)
        {
            currentVideo = videoPath;
            if (!host.IsValid && !host.Refresh())
            {
                Log.Warn("Desktop window not found yet; will retry");
                ScheduleRebuild("desktop not ready", 1000);
                return;
            }
            SyncSurfacesToMonitors();
            foreach (var s in surfaces) { s.Asleep = false; StartNext(s); }
            SyncNativeToVideo(item);
            StartPolling(1000);
            Evaluate();
        }

        void StartNext(Surface s)
        {
            DisposeNext(s);
            if (currentVideo == null) return;
            bool play = !s.Hidden && !VisiblePause();
            bool haveOld = s.Player != null && s.Player.Window != IntPtr.Zero && Native.IsWindow(s.Player.Window);
            try
            {
                // The new window goes on top, fully transparent, and is made opaque at its first frame.
                s.NextPlayer = new VideoPlayer(window.Handle, host.Parent, host.ScreenToParent(s.Bounds),
                    SurfaceAnchor, true, currentVideo, settings.Fit, play, OnHostExited);
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the video player", ex);
                DisposeNext(s);
                var failed = current;
                if (failed != null) ui.Post(_ => MarkFailed(failed, "The video player could not be started (" + ex.Message + ")."), null);
            }
        }

        void Promote(Surface s)
        {
            if (s.Retiring != null) s.Retiring.Dispose();
            s.Retiring = s.Player;               // ended when the new one reports it is on screen (EVT_REVEALED)
            s.Player = s.NextPlayer;
            s.NextPlayer = null;
            s.Player.Reveal();
            Log.Debug("Screen " + s.Bounds + " now showing " + Path.GetFileName(s.Player.Path) + " (" + s.Player.Statistics() + ")");
        }

        void DisposeNext(Surface s)
        {
            if (s.NextPlayer != null) { s.NextPlayer.Dispose(); s.NextPlayer = null; }
        }

        void TearDownSurfaces()
        {
            foreach (var s in surfaces)
            {
                DisposeNext(s);
                if (s.Player != null) { s.Player.Dispose(); s.Player = null; }
                if (s.Retiring != null) { s.Retiring.Dispose(); s.Retiring = null; }
            }
            surfaces.Clear();
            StopPolling();
            Native.KillTimer(window.Handle, TimerPromote);
        }

        void SyncSurfacesToMonitors()
        {
            List<RECT> monitors = EnumerateMonitors();
            bool same = monitors.Count == surfaces.Count;
            for (int i = 0; same && i < monitors.Count; i++)
            {
                RECT a = monitors[i], b = surfaces[i].Bounds;
                same = a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
            }
            if (same) return;
            TearDownSurfaces();
            foreach (RECT m in monitors) surfaces.Add(new Surface { Bounds = m });
            Log.Info("Screens: " + string.Join(", ", monitors.Select(m => m.Width + "x" + m.Height + " at " + m.Left + "," + m.Top)));
        }

        static List<RECT> EnumerateMonitors()
        {
            var list = new List<KeyValuePair<bool, RECT>>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (Native.GetMonitorInfo(hMon, ref mi)) list.Add(new KeyValuePair<bool, RECT>((mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, mi.rcMonitor));
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(p => p.Key).ThenBy(p => p.Value.Left).ThenBy(p => p.Value.Top).Select(p => p.Value).ToList();
        }

        static Size LargestMonitor()
        {
            int w = 1920, h = 1080;
            foreach (RECT r in EnumerateMonitors()) { if ((long)r.Width * r.Height > (long)w * h) { w = r.Width; h = r.Height; } }
            return new Size(w, h);
        }

        void ApplyFitEverywhere()
        {
            foreach (var s in surfaces)
            {
                if (s.Player != null) s.Player.ApplyFit(settings.Fit, s.Bounds.Width, s.Bounds.Height);
                if (s.NextPlayer != null) s.NextPlayer.ApplyFit(settings.Fit, s.Bounds.Width, s.Bounds.Height);
            }
            if (nativeCurrent != null) SetNative(nativeCurrent, null, true, board != null ? ShellApi.DWPOS_FILL : -1);
        }

        void MarkFailed(MediaItem item, string reason)
        {
            if (item == null || item.Failed) return;   // several screens can report the same failure
            item.Failed = true;
            Log.Warn("Skipping " + item.Name + ": " + reason);
            if (tray != null) tray.ShowBalloon("Can't show " + Shorten(item.Name, 40), reason, true);
            if (!IsCurrent(item)) return;
            var next = Step(1);
            if (next != null && !IsCurrent(next)) { ShowItem(next); return; }
            current = null;
            currentVideo = null;
            TearDownSurfaces();
            RefreshInkOverlays();
            UpdateStatus();
        }

        // ================================================================== native (Windows) wallpaper

        void SetNative(string path, Action<bool> done, bool force = false, int positionOverride = -1)
        {
            if (!force && done == null && string.Equals(path, nativeCurrent, StringComparison.OrdinalIgnoreCase)) return;
            int position = positionOverride >= 0 ? positionOverride : NativeWallpaper.PositionFor(settings.Fit);
            worker.EnqueueLatest("native", () => NativeWallpaper.Set(path, position), ok =>
            {
                if (ok) nativeCurrent = path;
                if (done != null) done(ok);
            });
        }

        static string SnapshotFor(MediaItem item)
        {
            return item.Kind == MediaKind.Gif ? item.Path : item.SnapshotPath;
        }

        // Makes the Windows wallpaper the live wallpaper's first frame (accent colors, Mica tint, seamless sign-in
        // and the stand-in while the decoder is unloaded).
        void SyncNativeToVideo(MediaItem item)
        {
            if (!settings.SyncWindowsWallpaper) return;
            string snap = SnapshotFor(item);
            if (item.Kind == MediaKind.Gif || File.Exists(snap)) { SetNative(snap, null); return; }
            string video = item.Path;
            worker.Enqueue("snap:" + item.CacheKey, false, () => MediaWorker.ExtractSnapshot(video, snap), ok =>
            {
                if (ok && IsCurrent(item) && currentVideo != null && settings.SyncWindowsWallpaper) SetNative(snap, null);
            });
        }

        void RestoreOriginalNative()
        {
            string path = settings.OriginalWallpaper;
            int pos = settings.OriginalPosition;
            worker.EnqueueLatest("native", () => { NativeWallpaper.Restore(path, pos); return true; }, ok => nativeCurrent = null);
        }

        // ================================================================== pause / resume decisions

        bool VisiblePause()
        {
            return userPaused || (settings.PauseOnBattery && power.OnBattery) || (settings.PauseOnEnergySaver && power.SaverOn);
        }

        static bool ScreenSaverRunning()
        {
            int running = 0;
            Native.SystemParametersInfo(ShellApi.SPI_GETSCREENSAVERRUNNING, 0, ref running, 0);
            return running != 0;
        }

        bool CanSleep()
        {
            // Unloading is only invisible when Windows shows the matching first frame underneath.
            return settings.SyncWindowsWallpaper && current != null && nativeCurrent != null &&
                   string.Equals(nativeCurrent, SnapshotFor(current), StringComparison.OrdinalIgnoreCase);
        }

        void Evaluate()
        {
            Native.KillTimer(window.Handle, TimerEvaluateSoon);
            if (exiting) return;
            SetEcoQos(editor == null);   // efficiency mode, except while someone is drawing (pen latency)
            if (currentVideo == null || surfaces.Count == 0)
            {
                pauseReason = "";
                UpdateStatus();
                return;
            }

            var bounds = surfaces.Select(s => s.Bounds).ToList();
            Occlusion.Result occ = occlusion.Compute(bounds, IntPtr.Zero);
            if (debugOcclusion != null)   // test hook: "--debug-occlusion=visible|hidden|auto"
            {
                for (int i = 0; i < occ.Covered.Length; i++) occ.Covered[i] = debugOcclusion == "hidden";
                occ.FullscreenApp = false;
            }
            string globalReason = power.SessionLocked ? "locked" : power.DisplayOff ? "screen off" : power.Suspending ? "sleeping"
                : ScreenSaverRunning() ? "screen saver" : (settings.PauseOnFullscreen && occ.FullscreenApp) ? "fullscreen app" : null;
            string visibleReason = userPaused ? "paused" : (settings.PauseOnBattery && power.OnBattery) ? "on battery"
                : (settings.PauseOnEnergySaver && power.SaverOn) ? "Energy Saver" : null;

            DateTime now = DateTime.UtcNow;
            bool anyPlaying = false, anyVisible = false;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                bool hidden = globalReason != null || (settings.PauseWhenCovered && occ.Covered[i]) || EditorCovers(s.Bounds);
                bool play = !hidden && visibleReason == null;
                if (!s.StateKnown || hidden != s.Hidden || play != s.Playing) { s.StateSince = now; s.StateKnown = true; }
                s.Hidden = hidden;
                s.Playing = play;
                if (!hidden) anyVisible = true;

                if (s.Asleep) { if (play) Wake(s); continue; }
                if (s.Player != null) { if (play) s.Player.Play(); else s.Player.Pause(); }
                if (s.NextPlayer != null) { if (play) s.NextPlayer.Play(); else s.NextPlayer.Pause(); }
                if (play && (s.Player != null || s.NextPlayer != null)) anyPlaying = true;

                // Not playing: end the player process (after a minute when covered, a few seconds when paused on
                // screen or nobody is looking). Windows keeps showing the matching first frame and playback resumes
                // from it.
                if (!play && s.Player != null && s.NextPlayer == null && CanSleep())
                {
                    bool away = power.DisplayOff || power.SessionLocked || power.Suspending;
                    TimeSpan after = away ? AwayUnloadDelay : hidden ? TimeSpan.FromSeconds(settings.DeepSleepSeconds) : VisiblePauseUnloadDelay;
                    if (now - s.StateSince >= after) Sleep(s, hidden ? (globalReason ?? "covered") : visibleReason);
                }
            }

            pauseReason = visibleReason ?? (anyVisible ? "" : (globalReason ?? "covered"));
            if (advanceDeferred && anyVisible) { advanceDeferred = false; Advance(1); return; }
            // Poll only while something could change without a notification (screen off / locked end with one), but
            // keep a slow tick while a player is still loaded so it gets unloaded on time.
            bool away2 = power.DisplayOff || power.SessionLocked || power.Suspending;
            bool anyLoaded = surfaces.Any(x => x.Player != null || x.NextPlayer != null);
            if (away2 && !anyLoaded) StopPolling(); else StartPolling(anyPlaying ? 1000u : 2000u);
            UpdateStatus();
            if (Log.Verbose) Log.Debug("Evaluate: playing=" + anyPlaying + " reason='" + pauseReason + "' covered=" + string.Join(",", occ.Covered) + " fs=" + occ.FullscreenApp);
        }

        void Sleep(Surface s, string why)
        {
            Log.Info("Unloading video (" + why + ") on screen " + s.Bounds.Width + "x" + s.Bounds.Height + ": ending " + s.Player.Statistics());
            s.Player.Dispose();
            s.Player = null;
            s.Asleep = true;
            TrimSoon();
        }

        void Wake(Surface s)
        {
            Log.Info("Resuming: reloading video");
            s.Asleep = false;
            StartNext(s);   // starts from the first frame, which is what Windows was showing
        }

        void ScheduleEvaluate(uint ms)
        {
            Native.SetTimer(window.Handle, TimerEvaluateSoon, ms, IntPtr.Zero);
        }

        void StartPolling(uint intervalMs)
        {
            if (polling && pollInterval == intervalMs) return;
            polling = true;
            pollInterval = intervalMs;
            Native.SetCoalescableTimer(window.Handle, TimerPoll, intervalMs, IntPtr.Zero, intervalMs / 4);   // lets Windows batch wakeups
        }

        void StopPolling()
        {
            if (!polling) return;
            polling = false;
            Native.KillTimer(window.Handle, TimerPoll);
        }

        void OnPoll()
        {
            if (surfaces.Count == 0 || currentVideo == null) { StopPolling(); return; }
            foreach (var s in surfaces) if (s.Retiring != null) { s.Retiring.Dispose(); s.Retiring = null; }
            if (!host.IsValid) { ScheduleRebuild("desktop window changed", 500); return; }
            // Keep our surfaces directly below the icons in case Explorer reordered its children.
            if (!inkLayer.AllAlive) RefreshInkOverlays();
            if (host.ModernLayout)
            {
                // Expected order under the icons: [ink windows], then a video surface.
                var ink = inkLayer.Windows;
                bool inkOk = ink.Count == 0 || Native.GetWindow(host.DefView, Native.GW_HWNDNEXT) == ink[0];
                IntPtr below = Native.GetWindow(SurfaceAnchor, Native.GW_HWNDNEXT);
                bool none = surfaces.All(s => s.Player == null || s.Player.Window == IntPtr.Zero);
                if (!inkOk || (!none && !surfaces.Any(s => s.Player != null && s.Player.Window == below)))
                {
                    Log.Info("Restoring surface z-order under the desktop icons");
                    if (!inkOk) inkLayer.Restack(host.InsertAfter);
                    foreach (var s in surfaces) if (s.Player != null) s.Player.Show(SurfaceAnchor);
                }
            }
            Evaluate();
        }

        void SetEcoQos(bool on)
        {
            if (ecoAlways) on = true;
            if (ecoQosKnown && on == ecoQos) return;
            ecoQos = on;
            ecoQosKnown = true;
            var st = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED | Native.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = on ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED | Native.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION : 0
            };
            int size = Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE));
            if (!Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref st, size))
            {
                st.ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                st.StateMask = on ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0;
                Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref st, size);
            }
            Log.Debug("EcoQoS " + (on ? "on" : "off"));
        }

        void TrimSoon()
        {
            Native.SetTimer(window.Handle, TimerTrim, 3000, IntPtr.Zero);
        }

        void Trim()
        {
            GC.Collect();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        // ================================================================== status

        void UpdateStatus()
        {
            string text;
            bool paused = false;
            if (board != null) text = BoardTitle + (editor != null ? " - drawing" : "");
            else if (current == null) text = items.Count == 0 ? "add wallpapers in Settings" : "nothing playable";
            else if (convertStatus != null) text = convertStatus.ToLowerInvariant().StartsWith("converting") ? convertStatus : convertStatus + "...";
            else if (currentVideo == null) text = current.Name;
            else if (pauseReason.Length > 0) { text = current.Name + " - paused (" + pauseReason + ")"; paused = true; }
            else text = current.Name;
            if (userPaused) paused = true;
            if (tray != null) tray.SetStatus("LiveWall: " + text, paused);
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                string detail = board != null ? "Showing " + text.Substring(0, 1).ToLowerInvariant() + text.Substring(1) : current == null ? text
                    : convertStatus ?? ((currentVideo == null ? "Showing picture " : pauseReason.Length > 0 ? "Paused (" + pauseReason + "): " : "Playing ") + current.Name);
                settingsForm.SetStatus(detail);
            }
        }

        static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        // ================================================================== window messages, events, timers

        IntPtr? WndProc(uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_TIMER:
                    OnTimer(wParam);
                    return IntPtr.Zero;
                case InkNative.WM_HOTKEY:
                    OnHotkey((int)wParam.ToInt64());
                    return IntPtr.Zero;
                case PlayerHost.WM_HOST_EVENT:
                    OnHostEvent((int)(wParam.ToInt64() & 0xFFFFFF), (int)(wParam.ToInt64() >> 24), lParam.ToInt64());
                    return IntPtr.Zero;
                case Native.WM_COPYDATA:
                {
                    var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
                    if (cds.dwData.ToInt64() != Ipc.Magic || cds.lpData == IntPtr.Zero) return IntPtr.Zero;
                    string cmd = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0');
                    ui.Post(_ => HandleCommand(cmd), null);   // don't run UI (dialogs) inside the sender's SendMessage
                    return new IntPtr(1);
                }
                case Native.WM_DISPLAYCHANGE:
                    ScheduleRebuild("display change", 800);
                    return null;
                case Native.WM_POWERBROADCAST:
                    if (power.HandleMessage(msg, wParam, lParam))
                    {
                        string state = power.ToString();
                        if (state != lastPowerState) { Log.Info("Power: " + state); lastPowerState = state; }
                        if ((uint)wParam.ToInt64() == Native.PBT_APMRESUMEAUTOMATIC) { ScheduleRebuild("resume from sleep", 2000); ApplyCollection(false); }
                        CheckBoardDay();
                        Evaluate();
                    }
                    return new IntPtr(1);
                case Native.WM_WTSSESSION_CHANGE:
                    if (power.HandleMessage(msg, wParam, lParam)) { Log.Info("Session: " + power); CheckBoardDay(); ApplyCollection(false); Evaluate(); }
                    return null;
                case Native.WM_QUERYENDSESSION:
                    return new IntPtr(1);
                case Native.WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) Shutdown();
                    return IntPtr.Zero;
            }
            if (msg == 0x001E /*WM_TIMECHANGE*/) { CheckBoardDay(); ApplyCollection(false); }
            if (msg == taskbarCreatedMessage && msg != 0) ScheduleRebuild("Explorer restarted", 1500);
            return null;
        }

        void OnTimer(IntPtr id)
        {
            if (id == TimerPoll) OnPoll();
            else if (id == TimerEvaluateSoon) Evaluate();
            else if (id == TimerSlideshow) OnSlideshowTimer();
            else if (id == TimerPromote) OnPromoteTimer();
            else if (id == TimerRebuild) { Native.KillTimer(window.Handle, TimerRebuild); Rebuild(); }
            else if (id == TimerRetry) { Native.KillTimer(window.Handle, TimerRetry); RetryVideo(); }
            else if (id == TimerTrim) { Native.KillTimer(window.Handle, TimerTrim); Trim(); }
            else if (id == TimerStart) { Native.KillTimer(window.Handle, TimerStart); OnStart(); }
            else if (id == TimerBoardDay) { Native.KillTimer(window.Handle, TimerBoardDay); CheckBoardDay(); }
            else if (id == TimerCollection) OnCollectionTimer();
        }

        void OnStart()
        {
            if (!host.Refresh()) Log.Warn("Desktop (Progman) not found yet");
            else Log.Info("Desktop: " + host);
            if (board != null)
            {
                // A board was opened (shortcut) while LiveWall was still starting: keep it, just pick the wallpaper behind it.
                int idx = InitialIndex();
                if (idx >= 0) { BuildOrder(idx); current = items[idx]; }
            }
            else if (!RestoreBoard()) ShowInitial();
            ScheduleCollectionTimer();
            string cmd = startupCommand;
            startupCommand = null;
            if (cmd != null) HandleCommand(cmd);
            else if (items.Count == 0 && !autostart) ShowSettings();
        }

        void HandleCommand(string cmd)
        {
            Log.Info("Command: " + cmd);
            if (cmd.StartsWith("collection=")) { UseCollection(cmd.Substring(11)); return; }
            switch (cmd)
            {
                case "next": Next(); break;
                case "prev": case "previous": Previous(); break;
                case "pause": if (!userPaused) TogglePause(); break;
                case "resume": if (userPaused) TogglePause(); break;
                case "toggle-pause": TogglePause(); break;
                case "add": AddWallpapersDialog(); break;
                case "board": ToggleBoard(); break;
                case "daily-board": ShowBoard(BoardKind.Daily, DateTime.Today, false); break;
                case "permanent-board": ShowBoard(BoardKind.Permanent, DateTime.Today, false); break;
                case "wallpaper": ExitBoard(); break;
                case "draw": StartDrawing(); break;
                case "exit": Exit(); break;
                case "status": Log.Info("Status: " + DebugState()); break;
                case "next-collection": NextCollection(); break;
                case "all-wallpapers": UseCollection(""); break;
                case "debug-occlusion=visible": debugOcclusion = "visible"; Evaluate(); break;
                case "debug-occlusion=hidden": debugOcclusion = "hidden"; Evaluate(); break;
                case "debug-occlusion=auto": debugOcclusion = null; Evaluate(); break;
                case "debug-new-day": SimulateNewDay(); break;
                default: ShowSettings(); break;
            }
        }

        string DebugState()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("current=").Append(current == null ? "-" : current.Name).Append(" video=").Append(currentVideo == null ? "-" : Path.GetFileName(currentVideo));
            sb.Append(" native=").Append(nativeCurrent == null ? "-" : Path.GetFileName(nativeCurrent)).Append(" reason='").Append(pauseReason).Append("'");
            foreach (var s in surfaces)
                sb.Append(" | ").Append(s.Bounds.Width).Append('x').Append(s.Bounds.Height).Append(s.Hidden ? " hidden" : " visible").Append(s.Asleep ? " asleep" : "")
                  .Append(" player=").Append(s.Player == null ? "-" : s.Player.Statistics()).Append(s.NextPlayer != null ? " (loading next)" : "");
            sb.Append(" | covering: ").Append(occlusion.Describe());
            return sb.ToString();
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (surfaces.Count == 0 || currentVideo == null) return;
            if ((evt == Native.EVENT_OBJECT_CLOAKED || evt == Native.EVENT_OBJECT_UNCLOAKED) && (idObject != 0 || idChild != 0)) return;
            ScheduleEvaluate(evt == Native.EVENT_SYSTEM_MINIMIZESTART ? 250u : 60u);
        }

        // A player host reported something (wParam = event << 24 | player id).
        void OnHostEvent(int playerId, int evt, long data)
        {
            foreach (var s in surfaces)
            {
                bool isNext = s.NextPlayer != null && s.NextPlayer.Id == playerId;
                VideoPlayer p = isNext ? s.NextPlayer : (s.Player != null && s.Player.Id == playerId ? s.Player : null);
                if (p == null) continue;
                p.OnHostEvent(evt, data);
                if (evt == PlayerHost.EVT_READY && inkLayer.Last != IntPtr.Zero) p.Show(SurfaceAnchor);   // below drawings made meanwhile
                else if (evt == PlayerHost.EVT_FIRST_FRAME && isNext)
                {
                    s.NextReadyAt = DateTime.UtcNow;
                    Native.SetTimer(window.Handle, TimerPromote, (uint)PromoteDelay.TotalMilliseconds + 20, IntPtr.Zero);
                }
                else if (evt == PlayerHost.EVT_REVEALED && s.Retiring != null) { s.Retiring.Dispose(); s.Retiring = null; }
                else if (evt == PlayerHost.EVT_LOST) ScheduleRebuild("surface destroyed", 1500);
                else if (evt == PlayerHost.EVT_ERROR) OnPlaybackError(p);
                return;
            }
        }

        // A player process ended. Expected when we disposed it; otherwise it crashed or was killed.
        void OnHostExited(VideoPlayer p)
        {
            if (p.Disposed || exiting) return;
            bool known = surfaces.Any(s => s.Player == p || s.NextPlayer == p);
            if (!known) return;
            if (!p.Failed)
            {
                Log.Warn("Video player process " + p.ProcessId + " ended unexpectedly");
                p.Dispose();
                if (deviceRetries++ < 3) Native.SetTimer(window.Handle, TimerRetry, 1500, IntPtr.Zero);
                else if (current != null) MarkFailed(current, "The video player keeps stopping.");
            }
        }

        void OnPromoteTimer()
        {
            Native.KillTimer(window.Handle, TimerPromote);
            bool waiting = false;
            DateTime now = DateTime.UtcNow;
            foreach (var s in surfaces)
            {
                if (s.NextPlayer == null || !s.NextPlayer.FirstFrameReady) continue;
                if (now - s.NextReadyAt >= PromoteDelay) Promote(s); else waiting = true;
            }
            if (waiting) Native.SetTimer(window.Handle, TimerPromote, 30, IntPtr.Zero);
            Evaluate();
        }

        void OnPlaybackError(VideoPlayer p)
        {
            int hr = p.ErrorHResult;
            Log.Warn("Playback problem in " + Path.GetFileName(p.Path) + ": " + (p.ErrorText ?? ("0x" + hr.ToString("X8"))));
            p.Dispose();
            bool deviceProblem = hr == unchecked((int)0x887A0005) || hr == unchecked((int)0x887A0007) || hr == unchecked((int)0x887A0020) ||
                                 hr == unchecked((int)0x88760868) /* D3DERR_DEVICELOST */;
            if (deviceProblem && deviceRetries++ < 3)
            {
                // GPU reset / driver update / resume from sleep: rebuild the players rather than giving up on the file.
                Native.SetTimer(window.Handle, TimerRetry, 1500, IntPtr.Zero);
                return;
            }
            if (current == null || !string.Equals(p.Path, currentVideo, StringComparison.OrdinalIgnoreCase)) return;
            string reason = p.ErrorText ?? "Playback failed.";
            if (hr == unchecked((int)0xC00D5212) || hr == unchecked((int)0xC00D36C4))
                reason += " If this is an HEVC/H.265, AV1 or VP9 video, install the matching video extension from the Microsoft Store, or convert it to H.264 MP4.";
            MarkFailed(current, reason);
        }

        void RetryVideo()
        {
            if (current == null || currentVideo == null) return;
            Log.Info("Recreating video players (attempt " + deviceRetries + ")");
            string video = currentVideo;
            var item = current;
            TearDownSurfaces();
            host.Refresh();
            if (!inkLayer.AllAlive) RefreshInkOverlays();
            ShowVideo(item, video);
        }

        void ScheduleRebuild(string reason, uint delayMs)
        {
            if (exiting) return;
            Log.Info("Rebuild scheduled: " + reason);
            Native.SetTimer(window.Handle, TimerRebuild, delayMs, IntPtr.Zero);
        }

        void Rebuild()
        {
            if (exiting) return;
            IntPtr oldProgman = host.Progman, oldParent = host.Parent;
            if (!host.Refresh())
            {
                if (++rebuildAttempts < 60) Native.SetTimer(window.Handle, TimerRebuild, 2000, IntPtr.Zero);
                else Log.Warn("Giving up waiting for the desktop window");
                return;
            }
            rebuildAttempts = 0;
            if (current != null && currentVideo == null && board == null) { RefreshInkOverlays(); return; }   // picture: redo its drawings
            if (current == null || currentVideo == null) return;

            // Only start over when something actually changed: Explorer's desktop window, the screens, or our windows.
            bool sameHost = host.Progman == oldProgman && host.Parent == oldParent;
            List<RECT> monitors = EnumerateMonitors();
            bool sameMonitors = monitors.Count == surfaces.Count;
            for (int i = 0; sameMonitors && i < monitors.Count; i++)
            {
                RECT a = monitors[i], b = surfaces[i].Bounds;
                sameMonitors = a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
            }
            bool windowsAlive = surfaces.All(s =>
                (s.Player == null || !s.Player.Ready || (Native.IsWindow(s.Player.Window) && Native.GetParent(s.Player.Window) == host.Parent)) &&
                (s.NextPlayer == null || !s.NextPlayer.Ready || Native.IsWindow(s.NextPlayer.Window)));
            if (sameHost && sameMonitors && windowsAlive)
            {
                Log.Info("Desktop unchanged; nothing to rebuild");
                if (!inkLayer.AllAlive) RefreshInkOverlays();
                foreach (var s in surfaces) if (s.Player != null) s.Player.Show(SurfaceAnchor);
                Evaluate();
                return;
            }
            Log.Info("Rebuilding surfaces (desktop " + (sameHost ? "same" : "changed") + ", screens " + (sameMonitors ? "same" : "changed") + "): " + host);
            string video = currentVideo;
            var item = current;
            TearDownSurfaces();
            RefreshInkOverlays();
            ShowVideo(item, video);
        }

        void Shutdown()
        {
            if (exiting) return;
            exiting = true;
            Log.Info("Exiting");
            foreach (IntPtr h in hooks) Native.UnhookWinEvent(h);
            hooks.Clear();
            foreach (var id in new[] { TimerPoll, TimerSlideshow, TimerEvaluateSoon, TimerRebuild, TimerPromote, TimerStart, TimerRetry, TimerTrim, TimerBoardDay, TimerCollection })
                Native.KillTimer(window.Handle, id);
            ShutdownInk();
            power.Dispose();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            if (tray != null) { tray.Dispose(); tray = null; }
            TearDownSurfaces();
            settings.Save();
            worker.Dispose();
            window.Dispose();
        }
    }
}
