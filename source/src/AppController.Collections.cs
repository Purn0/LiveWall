using System;
using System.Collections.Generic;
using System.Linq;
using LiveWall.Interop;

namespace LiveWall
{
    // Collections: named sets of wallpapers. The playlist is the playing collection's wallpapers ("" = all of them).
    //
    //  * The user picks one (tray, Ctrl+Alt+W cycles); a collection with hours takes over during them, unless the user
    //    picked another one after it started (that choice holds until the next start or end of any collection's hours).
    //  * One coalescable timer, set to the next start/end time; nothing polls.
    //  * A switch never replaces a board on screen (the board stays until the user goes back to the wallpaper) and
    //    waits while the user is drawing on the wallpaper.
    internal sealed partial class AppController
    {
        static readonly IntPtr TimerCollection = new IntPtr(21);
        string playingCollection = "";       // collection whose wallpapers are in `items`
        DateTime overrideUntil;              // the user's choice beats the hours until then
        bool collectionPending;              // a switch waits for the drawing to finish

        static bool Same(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase); }

        WallpaperCollection FindCollection(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return settings.Collections.FirstOrDefault(c => Same(c.Name, name));
        }

        WallpaperCollection ScheduledAt(DateTime t)
        {
            int minute = t.Hour * 60 + t.Minute;
            return settings.Collections.FirstOrDefault(c => c.Sources.Count > 0 && c.Covers(minute));
        }

        // What should play now.
        string EffectiveCollection()
        {
            DateTime now = DateTime.Now;
            if (now >= overrideUntil)
            {
                var scheduled = ScheduledAt(now);
                if (scheduled != null) return scheduled.Name;
            }
            var chosen = FindCollection(settings.ActiveCollection);
            return chosen != null ? chosen.Name : "";
        }

        IList<string> PlaylistSources
        {
            get { var c = FindCollection(playingCollection); return c != null ? c.Sources : settings.Sources; }
        }

        // Every source of every collection: cached conversions of wallpapers in other collections are kept.
        IList<string> AllSources
        {
            get
            {
                var all = new List<string>(settings.Sources);
                foreach (var c in settings.Collections)
                    foreach (string s in c.Sources)
                        if (!all.Any(x => Same(x, s))) all.Add(s);
                return all;
            }
        }

        static string DisplayName(string collection) { return string.IsNullOrEmpty(collection) ? "All wallpapers" : collection; }

        // ================================================================== public surface for tray / settings

        public string PlayingCollection { get { return playingCollection; } }
        public List<WallpaperCollection> Collections { get { return settings.Collections.Select(c => c.Clone()).ToList(); } }
        public string CollectionHotkey { get { return settings.HotkeyCollection; } }
        public string CurrentWallpaperPath { get { return current != null ? current.Path : null; } }

        // The user's choice ("" = all wallpapers).
        public void UseCollection(string name)
        {
            var c = FindCollection(name);
            name = c != null ? c.Name : "";
            settings.ActiveCollection = name;
            settings.Save();
            DateTime now = DateTime.Now;
            var scheduled = ScheduledAt(now);
            overrideUntil = scheduled != null && !Same(scheduled.Name, name) ? NextBoundary(now) : DateTime.MinValue;
            ApplyCollection(true);
        }

        // Hotkey: the next collection that has wallpapers.
        public void NextCollection()
        {
            var names = new List<string> { "" };
            names.AddRange(settings.Collections.Where(c => Playlist.Resolve(c.Sources).Count > 0).Select(c => c.Name));
            int i = names.FindIndex(n => Same(n, playingCollection));
            UseCollection(names[(i + 1) % names.Count]);
        }

        public void ApplyCollections(List<WallpaperCollection> list)
        {
            var before = FindCollection(playingCollection);
            var oldSources = before != null ? new List<string>(before.Sources) : new List<string>(settings.Sources);
            settings.Collections = list.Select(c => c.Clone()).ToList();
            if (FindCollection(settings.ActiveCollection) == null) settings.ActiveCollection = "";
            settings.Save();
            string effective = EffectiveCollection();
            if (Same(effective, playingCollection) && !oldSources.SequenceEqual(PlaylistSources, StringComparer.OrdinalIgnoreCase))
            {
                // Same collection, different wallpapers: keep the current one if it is still in it.
                playingCollection = effective;
                BuildPlaylist();
                int idx = CurrentIndex();
                if (idx >= 0) BuildOrder(idx);
                else if (board == null) ShowInitial();
                else PickBehindBoard();
                ScheduleNextAdvance();
            }
            ApplyCollection(false);
            UpdateStatus();
        }

        public void AddCurrentWallpaperTo(string name)
        {
            if (current == null || board != null) return;
            var list = Collections;
            var c = list.FirstOrDefault(x => Same(x.Name, name));
            if (c == null) return;
            if (!c.Sources.Any(s => Same(s, current.Path)))
            {
                c.Sources.Add(current.Path);
                ApplyCollections(list);
            }
            Toast.Show("Added to " + c.Name + ": " + current.Name);
        }

        CollectionsForm collectionsForm;

        public void ShowCollections()
        {
            if (collectionsForm != null && !collectionsForm.IsDisposed)
            {
                if (collectionsForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                    collectionsForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
                collectionsForm.Activate();
                return;
            }
            collectionsForm = new CollectionsForm(this);
            collectionsForm.FormClosed += (s, e) => { collectionsForm = null; TrimSoon(); };
            collectionsForm.Show();
            collectionsForm.Activate();
        }

        // ================================================================== switching

        // Plays whatever should play now; re-arms the timer.
        void ApplyCollection(bool announce)
        {
            if (exiting) return;
            string effective = EffectiveCollection();
            ScheduleCollectionTimer();
            if (Same(effective, playingCollection))
            {
                if (announce) Announce();
                return;
            }
            if (editor != null && !editor.IsBoard) { collectionPending = true; return; }   // not under someone drawing on it
            collectionPending = false;
            playingCollection = effective;
            BuildPlaylist();
            Log.Info("Wallpapers: " + DisplayName(playingCollection) + " (" + items.Count + ")");
            if (announce) Announce();
            if (board != null) { PickBehindBoard(); UpdateStatus(); return; }   // shown when the board goes away
            int idx = CurrentIndex();
            if (idx >= 0 && !items[idx].Failed)
            {
                // What is on screen belongs to the new collection too: keep it playing (no restart).
                BuildOrder(idx);
                ScheduleNextAdvance();
                Prefetch(PeekNext());
                UpdateStatus();
                return;
            }
            ShowInitial();
        }

        void Announce()
        {
            Toast.Show("Wallpapers: " + DisplayName(playingCollection) + "  (" + items.Count + ")" +
                       (board != null ? "  - after the board" : ""));
        }

        // The wallpaper to come back to after the board.
        void PickBehindBoard()
        {
            int idx = InitialIndex();
            if (idx >= 0) { BuildOrder(idx); current = items[idx]; }
            else current = null;
        }

        DateTime NextBoundary(DateTime now)
        {
            DateTime best = DateTime.MaxValue;
            foreach (var c in settings.Collections)
            {
                if (!c.Scheduled || c.Sources.Count == 0) continue;
                foreach (int m in new[] { c.StartMinute, c.EndMinute })
                {
                    DateTime t = now.Date.AddMinutes(m);
                    if (t <= now) t = t.AddDays(1);
                    if (t < best) best = t;
                }
            }
            return best;
        }

        void ScheduleCollectionTimer()
        {
            Native.KillTimer(window.Handle, TimerCollection);
            DateTime now = DateTime.Now, next = NextBoundary(now);
            if (overrideUntil > now && overrideUntil < next) next = overrideUntil;
            if (next == DateTime.MaxValue) return;
            double ms = (next - now).TotalMilliseconds + 1000;
            Native.SetCoalescableTimer(window.Handle, TimerCollection, (uint)Math.Max(1000, Math.Min(ms, int.MaxValue)), IntPtr.Zero, 30000);
        }

        void OnCollectionTimer()
        {
            Native.KillTimer(window.Handle, TimerCollection);
            ApplyCollection(false);
        }
    }
}
