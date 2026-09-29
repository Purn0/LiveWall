using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LiveWall.Ink;
using LiveWall.Interop;

namespace LiveWall
{
    // Boards and drawing.
    //
    //  * A board (today's, an earlier day's, or the permanent one) replaces the wallpaper. It is rendered once to a PNG
    //    and handed to Windows like any picture, so it costs nothing while shown.
    //  * Drawing happens in InkEditor, a full-screen window on the screen under the mouse; the desktop icons would
    //    otherwise take every click. Closing it (Esc / the hotkey / Done) puts the result on the desktop.
    //  * Drawings on a wallpaper are kept per wallpaper and shown by InkLayer: a static layered window per screen,
    //    above the picture or video and below the icons. Video surfaces are placed directly below it (SurfaceAnchor).
    internal sealed partial class AppController
    {
        const int HotkeyBoardId = 0x4C01, HotkeyDrawId = 0x4C02, HotkeyCollectionId = 0x4C03, HotkeyMusicId = 0x4C04;
        static readonly IntPtr TimerBoardDay = new IntPtr(20);

        BoardKind? board;              // board shown instead of the wallpaper
        DateTime boardDate;            // the daily board's day
        bool boardFollowsToday;        // a daily board opened as "today" moves on to the next day at midnight
        InkDocument boardDoc;
        InkEditor editor;
        InkLayer inkLayer;
        InkDocument currentInk;        // drawings on the current wallpaper (null when there are none)
        string boardVideo;             // the board's glow animation playing as the wallpaper (null = the still picture)
        string boardVideoFailed;       // an animation that would not play: the still picture stays
        bool encodingAnimation;        // a board animation is being made (efficiency mode is off meanwhile)

        void InitInk()
        {
            if (string.IsNullOrEmpty(settings.UserId))
            {
                settings.UserId = Guid.NewGuid().ToString("N").Substring(0, 12);
                settings.Save();
            }
            inkLayer = new InkLayer(ui);
            RegisterHotkeys();
        }

        void ShutdownInk()
        {
            Hotkeys.Unregister(window.Handle, HotkeyBoardId);
            Hotkeys.Unregister(window.Handle, HotkeyDrawId);
            Hotkeys.Unregister(window.Handle, HotkeyCollectionId);
            Hotkeys.Unregister(window.Handle, HotkeyMusicId);
            CloseEditor();
            if (inkLayer != null) { inkLayer.Dispose(); inkLayer = null; }
        }

        void RegisterHotkeys()
        {
            if (hotkeysSuspended) return;
            Hotkeys.Unregister(window.Handle, HotkeyBoardId);
            Hotkeys.Unregister(window.Handle, HotkeyDrawId);
            Hotkeys.Unregister(window.Handle, HotkeyCollectionId);
            Hotkeys.Unregister(window.Handle, HotkeyMusicId);
            var failed = new List<string>();
            if (!Hotkeys.Register(window.Handle, HotkeyBoardId, settings.HotkeyBoard)) failed.Add(settings.HotkeyBoard);
            if (!Hotkeys.Register(window.Handle, HotkeyDrawId, settings.HotkeyDraw)) failed.Add(settings.HotkeyDraw);
            if (!Hotkeys.Register(window.Handle, HotkeyCollectionId, settings.HotkeyCollection)) failed.Add(settings.HotkeyCollection);
            if (!Hotkeys.Register(window.Handle, HotkeyMusicId, settings.Music.Hotkey)) failed.Add(settings.Music.Hotkey);
            if (failed.Count == 0) Log.Info("Shortcuts registered: board " + settings.HotkeyBoard + ", draw " + settings.HotkeyDraw + ", collection " +
                                            settings.HotkeyCollection + ", music " + settings.Music.Hotkey);
            if (failed.Count > 0 && tray != null)
                tray.ShowBalloon("Shortcut not available",
                    string.Join(" and ", failed) + (failed.Count == 1 ? " is" : " are") + " already used by another app. You can pick another in LiveWall Settings.", true);
        }

        bool hotkeysSuspended;

        // While a shortcut is being typed into Settings.
        public void SuspendHotkeys()
        {
            if (hotkeysSuspended) return;
            hotkeysSuspended = true;
            Hotkeys.Unregister(window.Handle, HotkeyBoardId);
            Hotkeys.Unregister(window.Handle, HotkeyDrawId);
            Hotkeys.Unregister(window.Handle, HotkeyCollectionId);
            Hotkeys.Unregister(window.Handle, HotkeyMusicId);
        }

        public void ResumeHotkeys()
        {
            if (!hotkeysSuspended || exiting) return;
            hotkeysSuspended = false;
            RegisterHotkeys();
        }

        void OnHotkey(int id)
        {
            if (id == HotkeyBoardId) ToggleBoard();
            else if (id == HotkeyDrawId) StartDrawing();
            else if (id == HotkeyCollectionId) NextCollection();
            else if (id == HotkeyMusicId) MusicButton();
        }

        void ApplyInkSettings(Settings old, Settings s)
        {
            if (old.HotkeyBoard != s.HotkeyBoard || old.HotkeyDraw != s.HotkeyDraw || old.HotkeyCollection != s.HotkeyCollection) RegisterHotkeys();
            if (old.ShowWallpaperInk != s.ShowWallpaperInk) RefreshInkOverlays();
            if (old.AnimateBoards != s.AnimateBoards && board != null && editor == null) RenderBoard();
            // A new default look applies right away to a board that has nothing on it yet.
            if (old.BoardStyle != s.BoardStyle && board != null && boardDoc != null && !boardDoc.Exists && editor == null)
            {
                boardDoc = OpenBoardDoc(board.Value, boardDate);
                RenderBoard();
            }
        }

        // ================================================================== public surface for tray / settings

        public bool BoardShown { get { return board != null; } }
        public bool TodaysBoardShown { get { return board == BoardKind.Daily && boardDate == DateTime.Today; } }
        public bool PermanentBoardShown { get { return board == BoardKind.Permanent; } }
        public bool Drawing { get { return editor != null; } }
        public string BoardHotkey { get { return settings.HotkeyBoard; } }
        public string DrawHotkey { get { return settings.HotkeyDraw; } }
        public bool WallpaperInkVisible { get { return settings.ShowWallpaperInk; } }
        public bool HasWallpaper { get { return current != null && board == null; } }
        public bool CurrentWallpaperHasInk { get { return board == null && currentInk != null && currentInk.VisibleCount > 0; } }

        public List<DateTime> EarlierDailyBoards()
        {
            return Boards.DailyDates().Where(d => d != DateTime.Today).Take(14).ToList();
        }

        public bool IsBoardShown(DateTime day) { return board == BoardKind.Daily && boardDate == day; }

        string BoardTitle { get { return board == null ? "" : Boards.Title(board.Value, boardDate); } }

        // Hotkey 1: the board used last (today's or the permanent one) as the wallpaper; again: back to the wallpaper
        // (finishing any drawing first).
        public void ToggleBoard()
        {
            if (editor != null) CloseEditor();
            if (board != null) ExitBoard();
            else ShowBoard(settings.LastBoard == "permanent" ? BoardKind.Permanent : BoardKind.Daily, DateTime.Today, false);
        }

        // Hotkey 2: draw on whatever is shown (again: finish).
        public void StartDrawing()
        {
            if (editor != null) { CloseEditor(); return; }
            if (board != null) { OpenEditor(boardDoc, true, null); return; }
            if (current == null) { ShowBoard(BoardKind.Daily, DateTime.Today, true); return; }
            OpenWallpaperEditor(current);
        }

        public void ShowBoard(BoardKind kind, DateTime date, bool edit)
        {
            date = date.Date;
            if (editor != null)
            {
                if (editor.IsBoard && board == kind && (kind == BoardKind.Permanent || boardDate == date)) { editor.Activate(); return; }
                CloseEditor();
            }
            bool same = board == kind && (kind == BoardKind.Permanent || boardDate == date) && boardDoc != null;
            board = kind;
            boardDate = date;
            boardFollowsToday = kind == BoardKind.Daily && date == DateTime.Today;
            if (!same)
            {
                boardDoc = OpenBoardDoc(kind, date);
                settings.LastBoard = kind == BoardKind.Permanent ? "permanent" : "daily";
                settings.BoardMode = kind == BoardKind.Permanent ? "permanent"
                    : boardFollowsToday ? "daily" : "daily:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                settings.Save();
                Log.Info("Showing " + BoardTitle.ToLowerInvariant());
            }

            Native.KillTimer(window.Handle, TimerSlideshow);
            advanceDeferred = false;
            convertStatus = null;
            // The video (if any) keeps running underneath until the board image is on screen; the same board's animation
            // just goes on.
            currentVideo = same && boardVideo != null ? boardVideo : null;
            RefreshInkOverlays();
            ScheduleBoardDay();
            if (!same || nativeCurrent == null || !nativeCurrent.StartsWith(Boards.RenderDir, StringComparison.OrdinalIgnoreCase)) RenderBoard();
            if (edit) OpenEditor(boardDoc, true, null);
            Evaluate();
            UpdateStatus();
        }

        public void ExitBoard()
        {
            if (board == null) return;
            LeaveBoardState();
            if (current != null && !current.Failed) ShowItem(current);
            else ShowInitial();
        }

        void LeaveBoardAndStep(int dir)
        {
            LeaveBoardState();
            var next = Step(dir);
            if (next != null) ShowItem(next); else ShowInitial();
        }

        // Forgets the board (the caller shows a wallpaper next).
        void LeaveBoardState()
        {
            if (board == null) return;
            if (editor != null && editor.IsBoard) CloseEditor();
            board = null;
            boardDoc = null;
            boardVideo = null;
            boardFollowsToday = false;
            settings.BoardMode = "";
            settings.Save();
            Native.KillTimer(window.Handle, TimerBoardDay);
        }

        // At start-up: show the board that was up when LiveWall last ran.
        bool RestoreBoard()
        {
            string mode = settings.BoardMode ?? "";
            BoardKind kind;
            DateTime date = DateTime.Today;
            if (mode == "permanent") kind = BoardKind.Permanent;
            else if (mode == "daily") kind = BoardKind.Daily;
            else if (mode.StartsWith("daily:") &&
                     DateTime.TryParseExact(mode.Substring(6), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) kind = BoardKind.Daily;
            else return false;
            int idx = InitialIndex();
            if (idx >= 0) { BuildOrder(idx); current = items[idx]; }
            ShowBoard(kind, date, false);
            return true;
        }

        InkDocument OpenBoardDoc(BoardKind kind, DateTime date)
        {
            Size sz = LargestMonitor();
            string path = kind == BoardKind.Daily ? Boards.DailyPath(date) : Boards.PermanentPath;
            return InkDocument.Open(path, sz.Width, sz.Height, InkRenderer.NormalizeStyle(settings.BoardStyle), Boards.Title(kind, date));
        }

        // Renders the board and hands it to Windows; the video underneath (if any) goes once the board is on screen.
        void RenderBoard()
        {
            if (board == null || boardDoc == null) return;
            var doc = boardDoc;
            var kind = board.Value;
            var date = boardDate;
            var layers = doc.Snapshot();
            string style = InkRenderer.NormalizeStyle(doc.Background);
            bool animate = settings.AnimateBoards && InkGlow.AnyAnimated(layers);
            int cw = doc.CanvasWidth, ch = doc.CanvasHeight;
            Size sz = LargestMonitor();
            string header = Boards.Header(kind, date);
            int seed = Boards.Seed(kind, date);
            string name = kind == BoardKind.Daily ? "daily-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "permanent";
            string path = Path.Combine(Boards.RenderDir, name + "-" + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture) + ".png");
            // A copy anyone can open, in Pictures\LiveWall Boards (only once the board has something on it).
            string export = doc.Exists ? Boards.ExportPath(kind, date) : null;
            worker.EnqueueLatest("board-render", () =>
            {
                bool done = InkRenderer.RenderBoardFile(style, layers, cw, ch, sz.Width, sz.Height, header, seed, path);
                InkText.ClearCache();
                if (done && export != null)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(export)); File.Copy(path, export, true); }
                    catch (Exception ex) { Log.Warn("Could not save " + export + ": " + ex.Message); }
                }
                return done;
            }, ok =>
            {
                if (board == null || boardDoc != doc) return;
                if (!ok) { TearDownSurfaces(); return; }
                string anim = animate ? Path.Combine(Boards.AnimDir, "board-" + InkGlow.ContentKey(layers, style, sz.Width & ~1, sz.Height & ~1, header) + ".mp4") : null;
                SetNative(path, set =>
                {
                    if (board == null || boardDoc != doc) return;
                    worker.Enqueue("board-clean", false, () => { Boards.CleanRenders(path); return true; }, null);
                    if (anim != null && string.Equals(anim, boardVideo, StringComparison.OrdinalIgnoreCase) && surfaces.Count > 0)
                    {
                        currentVideo = boardVideo;   // the same animation: it goes on
                        Evaluate();
                        return;
                    }
                    FadeOutSurfaces();   // a video below (or the board's previous animation) fades out over the board
                    boardVideo = null;
                    currentVideo = null;
                    TrimSoon();
                    if (anim == null || string.Equals(anim, boardVideoFailed, StringComparison.OrdinalIgnoreCase)) return;
                    if (File.Exists(anim)) { StartBoardVideo(anim); return; }
                    // Made once in the background (a few seconds); the still picture shows meanwhile.
                    encodingAnimation = true;
                    SetEcoQos(false);
                    worker.EnqueueLatest("board-anim", () =>
                    {
                        bool made = InkGlow.RenderAnimation(style, layers, cw, ch, sz.Width, sz.Height, header, seed, anim);
                        InkText.ClearCache();
                        return made;
                    }, made =>
                    {
                        encodingAnimation = false;
                        Evaluate();   // efficiency mode back on
                        if (board == null || boardDoc != doc) return;
                        if (made) StartBoardVideo(anim);
                        else { Log.Warn("Board animation could not be made; showing the still picture"); boardVideoFailed = anim; }
                    });
                }, true, ShellApi.DWPOS_FILL);
            });
        }

        // The board's glow animation fades in over its still picture (its first frame) and then plays, pauses and
        // unloads like any video wallpaper; the still picture stays with Windows.
        void StartBoardVideo(string video)
        {
            if (board == null) return;
            Log.Info("Board animation: " + Path.GetFileName(video));
            boardVideo = video;
            currentVideo = video;
            if (!host.IsValid && !host.Refresh())
            {
                ScheduleRebuild("desktop not ready", 1000);
                return;
            }
            SyncSurfacesToMonitors();
            foreach (var s in surfaces) { s.Asleep = false; StartNext(s, true); }
            StartPolling(1000);
            Evaluate();
            worker.Enqueue("board-anim-clean", false, () => { Boards.CleanAnimations(video); return true; }, null);
        }

        // The animation would not play: back to the still picture (already Windows' wallpaper).
        void StopBoardVideo(string why)
        {
            Log.Warn("Board animation stopped (" + why + "); showing the still picture");
            boardVideoFailed = boardVideo;
            boardVideo = null;
            currentVideo = null;
            TearDownSurfaces();
            UpdateStatus();
        }

        void ScheduleBoardDay()
        {
            Native.KillTimer(window.Handle, TimerBoardDay);
            if (board != BoardKind.Daily || !boardFollowsToday) return;
            double ms = (DateTime.Today.AddDays(1) - DateTime.Now).TotalMilliseconds + 5000;
            Native.SetCoalescableTimer(window.Handle, TimerBoardDay, (uint)Math.Max(1000, Math.Min(ms, int.MaxValue)), IntPtr.Zero, 30000);
        }

        // A new day: today's board becomes a fresh one (yesterday's is kept). Also re-arms the midnight timer.
        void CheckBoardDay()
        {
            if (board != BoardKind.Daily || !boardFollowsToday || exiting) return;
            if (boardDate == DateTime.Today) { ScheduleBoardDay(); return; }
            if (editor != null && editor.IsBoard)
            {
                Native.SetCoalescableTimer(window.Handle, TimerBoardDay, 60000, IntPtr.Zero, 5000);   // after the drawing is done
                return;
            }
            Log.Info("New day: fresh daily board");
            boardDate = DateTime.Today;
            boardDoc = OpenBoardDoc(BoardKind.Daily, boardDate);
            RenderBoard();
            ScheduleBoardDay();
            UpdateStatus();
        }

        // Test hook (--debug-new-day): as if today's board had been opened yesterday, then the midnight check runs.
        void SimulateNewDay()
        {
            if (board != BoardKind.Daily || !boardFollowsToday) { Log.Info("debug-new-day: today's board is not shown"); return; }
            boardDate = DateTime.Today.AddDays(-1);
            boardDoc = OpenBoardDoc(BoardKind.Daily, boardDate);
            CheckBoardDay();
        }

        // ================================================================== drawing

        void OpenWallpaperEditor(MediaItem item)
        {
            // The editor shows the wallpaper underneath; for a video that is its first frame.
            if (item.Kind == MediaKind.Video && !File.Exists(item.SnapshotPath))
            {
                string video = item.Path, snap = item.SnapshotPath;
                worker.Enqueue("snap:" + item.CacheKey, true, () => MediaWorker.ExtractSnapshot(video, snap), ok =>
                {
                    if (IsCurrent(item) && board == null && editor == null) OpenWallpaperEditorNow(item);
                });
                return;
            }
            OpenWallpaperEditorNow(item);
        }

        void OpenWallpaperEditorNow(MediaItem item)
        {
            Rectangle mon = MonitorUnderCursor();
            var doc = InkDocument.Open(Boards.WallpaperInkPath(item.Path), mon.Width, mon.Height, InkDocument.NoBackground, item.Path);
            Image picture = LoadWallpaperPicture(item);
            OpenEditor(doc, false, picture);
            RefreshInkOverlays();   // hidden while the editor shows them itself
        }

        Image LoadWallpaperPicture(MediaItem item)
        {
            Bitmap b = InkRenderer.LoadImage(item.Kind == MediaKind.Image ? item.Path : SnapshotFor(item));
            if (b == null && item.Kind == MediaKind.Image && string.Equals(nativeCurrent, item.Path, StringComparison.OrdinalIgnoreCase))
                b = InkRenderer.LoadImage(TranscodedWallpaperPath);
            return b;
        }

        // WebP, HEIC, AVIF...: GDI+ can't read them, but Windows keeps a JPEG copy of the current wallpaper.
        static string TranscodedWallpaperPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper"); }
        }

        void OpenEditor(InkDocument doc, bool isBoard, Image picture)
        {
            if (editor != null || doc == null) return;
            Rectangle mon = MonitorUnderCursor();
            string header = isBoard && board != null ? Boards.Header(board.Value, boardDate) : null;
            int seed = isBoard && board != null ? Boards.Seed(board.Value, boardDate) : 0;
            string dailyLabel = board == BoardKind.Daily && boardDate != DateTime.Today
                ? boardDate.ToString("d MMM", CultureInfo.CurrentCulture) : "Today";
            // "Save a copy" names: <this> + the time.
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string exportName = !isBoard || board == null ? "Drawing " + today
                : board == BoardKind.Permanent ? "Permanent board " + today : Path.GetFileNameWithoutExtension(Boards.ExportPath(board.Value, boardDate));
            SetEcoQos(false);   // full speed while drawing; Evaluate puts it back once the editor is gone
            // The slideshow waits while someone draws; it starts counting again when they finish.
            Native.KillTimer(window.Handle, TimerSlideshow);
            advanceDeferred = false;
            try
            {
                editor = new InkEditor(doc, isBoard, isBoard && board == BoardKind.Daily, dailyLabel, mon, settings.UserId, header, seed, picture,
                                       settings.Fit, exportName);
                editor.Finished += OnEditorFinished;
                editor.Show();
                Log.Info("Drawing on " + (isBoard ? BoardTitle.ToLowerInvariant() : "the wallpaper") + " (screen " + mon.Width + "x" + mon.Height + ")");
            }
            catch (Exception ex)
            {
                Log.Error("Could not open the drawing window", ex);
                if (editor != null) { editor.Finished -= OnEditorFinished; editor.Dispose(); }
                editor = null;
                if (picture != null) picture.Dispose();
            }
            Evaluate();
            UpdateStatus();
        }

        void CloseEditor()
        {
            if (editor != null && !editor.IsDisposed) editor.Finish();
            editor = null;
        }

        void OnEditorFinished(object sender, EventArgs e)
        {
            var ed = (InkEditor)sender;
            ed.Finished -= OnEditorFinished;
            if (editor == ed) editor = null;
            ui.Post(_ => ed.Dispose(), null);
            if (exiting) return;
            if (ed.IsBoard)
            {
                if (board != null && boardDoc == ed.Document) RenderBoard();
                if (ed.SwitchBoardRequested && board != null)
                {
                    if (board == BoardKind.Daily) ShowBoard(BoardKind.Permanent, DateTime.Today, true);
                    else ShowBoard(BoardKind.Daily, DateTime.Today, true);
                    return;
                }
            }
            else
            {
                RefreshInkOverlays();
                ScheduleNextAdvance();
            }
            if (collectionPending) ApplyCollection(false);
            Evaluate();
            UpdateStatus();
            TrimSoon();
        }

        bool EditorCovers(RECT r)
        {
            if (editor == null) return false;
            Rectangle m = editor.Monitor;
            return m.Left == r.Left && m.Top == r.Top && m.Right == r.Right && m.Bottom == r.Bottom;
        }

        static Rectangle MonitorUnderCursor()
        {
            POINT p;
            List<RECT> monitors = EnumerateMonitors();
            if (InkNative.GetCursorPos(out p))
                foreach (RECT m in monitors)
                    if (p.X >= m.Left && p.X < m.Right && p.Y >= m.Top && p.Y < m.Bottom) return Rectangle.FromLTRB(m.Left, m.Top, m.Right, m.Bottom);
            if (monitors.Count > 0) return Rectangle.FromLTRB(monitors[0].Left, monitors[0].Top, monitors[0].Right, monitors[0].Bottom);
            return new Rectangle(0, 0, 1920, 1080);
        }

        // ================================================================== drawings on wallpapers

        // Video surfaces go directly below the ink windows (or below the icons when there are none).
        IntPtr SurfaceAnchor
        {
            get
            {
                IntPtr last = inkLayer != null ? inkLayer.Last : IntPtr.Zero;
                return last != IntPtr.Zero ? last : host.InsertAfter;
            }
        }

        void RefreshInkOverlays()
        {
            currentInk = null;
            if (inkLayer == null) return;
            inkLayer.Clear();
            if (board != null || current == null) return;
            string path = Boards.WallpaperInkPath(current.Path);
            if (!File.Exists(path)) return;
            var doc = InkDocument.Open(path, 1920, 1080, InkDocument.NoBackground, current.Path);
            currentInk = doc;
            if (!settings.ShowWallpaperInk || (editor != null && !editor.IsBoard)) return;
            List<InkStroke> strokes = doc.ShownStrokes();
            if (strokes.Count == 0) return;
            if (!host.IsValid && !host.Refresh()) return;
            var screens = EnumerateMonitors().Select(m => new InkLayer.Screen { Bounds = m, BoundsInParent = host.ScreenToParent(m) }).ToList();
            var backdrop = new InkLayer.Backdrop
            {
                Path = current.Kind == MediaKind.Image ? current.Path : SnapshotFor(current),
                Fallback = current.Kind == MediaKind.Image && string.Equals(nativeCurrent, current.Path, StringComparison.OrdinalIgnoreCase)
                    ? TranscodedWallpaperPath : null,
                Fit = settings.Fit
            };
            inkLayer.Show(host.Parent, host.InsertAfter, screens, strokes, doc.CanvasWidth, doc.CanvasHeight, backdrop, () =>
            {
                // Players started meanwhile were put right under the icons, above the new ink: move them below it.
                foreach (var s in surfaces)
                {
                    if (s.Player != null) s.Player.Show(SurfaceAnchor);
                    if (s.NextPlayer != null) s.NextPlayer.Show(SurfaceAnchor);
                }
                TrimSoon();   // the rendering's temporary bitmaps
            });
        }

        public void ToggleWallpaperInk()
        {
            settings.ShowWallpaperInk = !settings.ShowWallpaperInk;
            settings.Save();
            RefreshInkOverlays();
        }

        public void ClearWallpaperInk()
        {
            if (current == null || currentInk == null) return;
            if (MessageBox.Show("Remove all drawings from \"" + current.Name + "\"?", "LiveWall",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (currentInk == null) return;
            currentInk.Erase(currentInk.VisibleStrokes().Select(s => s.Id).ToList(), settings.UserId);
            RefreshInkOverlays();
        }

        // The board pictures (Pictures\LiveWall Boards).
        public void OpenBoardsFolder()
        {
            try
            {
                Directory.CreateDirectory(Boards.ExportDir);
                Process.Start("explorer.exe", "\"" + Boards.ExportDir + "\"");
            }
            catch (Exception ex) { Log.Error("Open boards folder", ex); }
        }
    }
}
