using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Ink;

namespace LiveWall
{
    internal sealed class TrayIcon : IDisposable
    {
        static readonly int[] IntervalChoices = { 0, 1, 5, 10, 15, 30, 60, 120, 360, 720, 1440 };

        readonly AppController app;
        readonly NotifyIcon icon;
        readonly ContextMenu menu;
        readonly Icon normalIcon, pausedIcon;
        IntPtr pausedHandle;

        public TrayIcon(AppController app)
        {
            this.app = app;
            Size size = SystemInformation.SmallIconSize;
            normalIcon = AppIcon.Load(size);
            pausedIcon = MakePausedIcon(normalIcon, size, out pausedHandle);
            menu = new ContextMenu();
            menu.Popup += (s, e) => Rebuild();
            Rebuild();
            icon = new NotifyIcon { Icon = normalIcon, Text = "LiveWall", ContextMenu = menu };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) app.ShowSettings(); };
            icon.BalloonTipClicked += (s, e) => app.ShowSettings();
            icon.Visible = true;
        }

        // music: the track playing (second line of the tooltip), or null.
        public void SetStatus(string text, string music, bool paused)
        {
            string line2 = music == null ? "" : "\n\u266A " + (music.Length > 30 ? music.Substring(0, 28) + "..." : music);
            int room = 63 - line2.Length;   // NotifyIcon limit
            string t = (text.Length > room ? text.Substring(0, room - 3) + "..." : text) + line2;
            if (icon.Text != t) icon.Text = t;
            Icon want = paused ? pausedIcon : normalIcon;
            if (icon.Icon != want) icon.Icon = want;
        }

        public void ShowBalloon(string title, string text, bool warning)
        {
            icon.ShowBalloonTip(6000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        void Rebuild()
        {
            menu.MenuItems.Clear();
            var header = new MenuItem(app.CurrentTitle) { Enabled = false };
            menu.MenuItems.Add(header);
            menu.MenuItems.Add("-");
            bool several = app.PlayableCount > 1;
            menu.MenuItems.Add(new MenuItem("Next wallpaper", (s, e) => app.Next()) { Enabled = several });
            menu.MenuItems.Add(new MenuItem("Previous wallpaper", (s, e) => app.Previous()) { Enabled = several });
            menu.MenuItems.Add(new MenuItem(app.UserPaused ? "Resume" : "Pause", (s, e) => app.TogglePause()) { Enabled = app.HasLiveWallpaper || app.UserPaused });
            AddCollectionItems();
            AddMusicItems();
            menu.MenuItems.Add("-");
            AddBoardItems();
            menu.MenuItems.Add("-");

            var every = new MenuItem("Change every");
            foreach (int minutes in IntervalChoices)
            {
                int m = minutes;
                every.MenuItems.Add(new MenuItem(DescribeInterval(m), (s, e) => app.SetInterval(m)) { RadioCheck = true, Checked = app.IntervalMinutes == m });
            }
            menu.MenuItems.Add(every);
            menu.MenuItems.Add(new MenuItem("Shuffle", (s, e) => app.ToggleShuffle()) { Checked = app.ShuffleOn });
            var scaling = new MenuItem("Scaling");
            foreach (FitMode f in new[] { FitMode.Fill, FitMode.Fit, FitMode.Center })
            {
                FitMode mode = f;
                scaling.MenuItems.Add(new MenuItem(DescribeFit(mode), (s, e) => app.SetFit(mode)) { RadioCheck = true, Checked = app.Fit == mode });
            }
            menu.MenuItems.Add(scaling);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(new MenuItem("Add wallpapers...", (s, e) => app.AddWallpapersDialog()));
            menu.MenuItems.Add(new MenuItem("Settings...", (s, e) => app.ShowSettings()) { DefaultItem = true });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(new MenuItem("Exit LiveWall", (s, e) => app.Exit()));
        }

        void AddCollectionItems()
        {
            var collections = app.Collections;
            string playing = app.PlayingCollection;
            var menuItem = new MenuItem("Collection: " + (playing.Length == 0 ? "All wallpapers" : playing));
            menuItem.MenuItems.Add(new MenuItem("Next collection" + ShortcutText(app.CollectionHotkey), (s, e) => app.NextCollection())
                { Enabled = collections.Count > 0 });
            menuItem.MenuItems.Add("-");
            menuItem.MenuItems.Add(new MenuItem("All wallpapers", (s, e) => app.UseCollection("")) { RadioCheck = true, Checked = playing.Length == 0 });
            foreach (var c in collections)
            {
                string name = c.Name;
                menuItem.MenuItems.Add(new MenuItem(name + (c.Scheduled ? "   (" + c.ScheduleText + ")" : ""), (s, e) => app.UseCollection(name))
                    { RadioCheck = true, Checked = string.Equals(name, playing, StringComparison.OrdinalIgnoreCase), Enabled = c.Sources.Count > 0 });
            }
            menuItem.MenuItems.Add("-");
            if (app.HasWallpaper && collections.Count > 0)
            {
                var addTo = new MenuItem("Add this wallpaper to");
                foreach (var c in collections)
                {
                    string name = c.Name;
                    addTo.MenuItems.Add(new MenuItem(name, (s, e) => app.AddCurrentWallpaperTo(name)));
                }
                menuItem.MenuItems.Add(addTo);
            }
            menuItem.MenuItems.Add(new MenuItem("Edit collections...", (s, e) => app.ShowCollections()));
            menu.MenuItems.Add(menuItem);
        }

        void AddMusicItems()
        {
            var music = new MenuItem("Music");
            music.MenuItems.Add(new MenuItem(Shorten(app.MusicStatus, 60).Replace("&", "&&")) { Enabled = false });
            music.MenuItems.Add("-");
            music.MenuItems.Add(new MenuItem((app.MusicMuted ? "Play" : "Pause") + ShortcutText(app.MusicHotkey), (s, e) => app.ToggleMusicMute()));
            music.MenuItems.Add(new MenuItem("Next track", (s, e) => app.NextTrack()) { Enabled = app.CanSkipTrack });
            var volume = new MenuItem("Volume");
            foreach (int v in new[] { 25, 50, 75, 100 })
            {
                int level = v;
                volume.MenuItems.Add(new MenuItem(v + "%", (s, e) => app.SetMusicVolume(level)) { RadioCheck = true, Checked = app.MusicVolume == v });
            }
            music.MenuItems.Add(volume);
            music.MenuItems.Add(new MenuItem("Silence while other apps play sound", (s, e) => app.ToggleMusicSilenceForOtherAudio())
                { Checked = app.MusicSilencesForOtherAudio });
            music.MenuItems.Add("-");
            if (app.HasMusicTarget)
            {
                string mine = app.CurrentWallpaperMusic;
                string kind = mine == null ? MusicSpec.Default : MusicSpec.Kind(mine);
                var forThis = new MenuItem("For this wallpaper");
                forThis.MenuItems.Add(new MenuItem("Default (" + MusicSpec.Describe(app.MusicDefault) + ")", (s, e) => app.SetCurrentWallpaperMusic(null))
                    { RadioCheck = true, Checked = kind == MusicSpec.Default });
                foreach (string k in new[] { MusicSpec.None, MusicSpec.Random, MusicSpec.Theme, MusicSpec.Video })
                {
                    string spec = k;
                    forThis.MenuItems.Add(new MenuItem(MusicSpec.Describe(spec), (s, e) => app.SetCurrentWallpaperMusic(spec)) { RadioCheck = true, Checked = kind == spec });
                }
                if (kind == MusicSpec.Custom)
                    forThis.MenuItems.Add(new MenuItem(Shorten(MusicSpec.Describe(mine), 60).Replace("&", "&&")) { RadioCheck = true, Checked = true, Enabled = false });
                forThis.MenuItems.Add(new MenuItem("Choose music files...", (s, e) => app.ChooseCurrentWallpaperMusicFiles(null)));
                forThis.MenuItems.Add(new MenuItem("Choose a music folder...", (s, e) => app.ChooseCurrentWallpaperMusicFolder(IntPtr.Zero)));
                music.MenuItems.Add(forThis);
            }
            music.MenuItems.Add(new MenuItem("Music settings...", (s, e) => app.ShowMusicSettings()));
            menu.MenuItems.Add(music);
        }

        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 3) + "..."; }

        void AddBoardItems()
        {
            var boards = new MenuItem("Board");
            boards.MenuItems.Add(new MenuItem("Today's board" + ShortcutText(app.BoardHotkey), (s, e) => app.ShowBoard(BoardKind.Daily, DateTime.Today, false))
                { RadioCheck = true, Checked = app.TodaysBoardShown });
            boards.MenuItems.Add(new MenuItem("Permanent board", (s, e) => app.ShowBoard(BoardKind.Permanent, DateTime.Today, false))
                { RadioCheck = true, Checked = app.PermanentBoardShown });
            var earlier = new MenuItem("Earlier days");
            foreach (DateTime day in app.EarlierDailyBoards())
            {
                DateTime d = day;
                earlier.MenuItems.Add(new MenuItem(d.ToString("dddd, d MMMM yyyy"), (s, e) => app.ShowBoard(BoardKind.Daily, d, false))
                    { RadioCheck = true, Checked = app.IsBoardShown(d) });
            }
            if (earlier.MenuItems.Count == 0) earlier.Enabled = false;
            boards.MenuItems.Add(earlier);
            boards.MenuItems.Add("-");
            boards.MenuItems.Add(new MenuItem("Back to the wallpaper", (s, e) => app.ExitBoard()) { Enabled = app.BoardShown });
            boards.MenuItems.Add(new MenuItem("Open board pictures", (s, e) => app.OpenBoardsFolder()));
            menu.MenuItems.Add(boards);

            string target = app.HasWallpaper ? "Draw on the wallpaper" : "Draw on the board";
            menu.MenuItems.Add(new MenuItem((app.Drawing ? "Finish drawing" : target) + ShortcutText(app.DrawHotkey), (s, e) => app.StartDrawing()));
            if (app.HasWallpaper)
            {
                var ink = new MenuItem("Drawings on wallpapers");
                ink.MenuItems.Add(new MenuItem("Show drawings", (s, e) => app.ToggleWallpaperInk()) { Checked = app.WallpaperInkVisible });
                ink.MenuItems.Add(new MenuItem("Remove drawings from this wallpaper...", (s, e) => app.ClearWallpaperInk()) { Enabled = app.CurrentWallpaperHasInk });
                menu.MenuItems.Add(ink);
            }
        }

        static string ShortcutText(string hotkey) { return string.IsNullOrWhiteSpace(hotkey) ? "" : "\t" + hotkey; }

        public static string DescribeInterval(int minutes)
        {
            if (minutes <= 0) return "Never";
            if (minutes < 60) return minutes == 1 ? "1 minute" : minutes + " minutes";
            if (minutes < 1440) return minutes == 60 ? "1 hour" : (minutes / 60) + " hours";
            return minutes == 1440 ? "1 day" : (minutes / 1440) + " days";
        }

        public static string DescribeFit(FitMode f)
        {
            switch (f)
            {
                case FitMode.Fit: return "Fit (show all, with bars)";
                case FitMode.Center: return "Center (no scaling)";
                default: return "Fill (crop to fill screen)";
            }
        }

        static Icon MakePausedIcon(Icon baseIcon, Size size, out IntPtr handle)
        {
            using (var bmp = new Bitmap(size.Width, size.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawIcon(baseIcon, new Rectangle(0, 0, size.Width, size.Height));
                    // Dim the icon and add a pause badge in the lower-right corner.
                    using (var veil = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(veil, 0, 0, size.Width, size.Height);
                    float d = size.Width * 0.62f, x = size.Width - d, y = size.Height - d;
                    using (var badge = new SolidBrush(Color.FromArgb(255, 245, 158, 11))) g.FillEllipse(badge, x, y, d - 0.5f, d - 0.5f);
                    float bw = d * 0.16f, bh = d * 0.46f, by = y + (d - bh) / 2;
                    using (var bar = new SolidBrush(Color.FromArgb(255, 30, 30, 30)))
                    {
                        g.FillRectangle(bar, x + d * 0.30f, by, bw, bh);
                        g.FillRectangle(bar, x + d * 0.54f, by, bw, bh);
                    }
                }
                handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            if (pausedHandle != IntPtr.Zero) DestroyIcon(pausedHandle);
        }
    }

    internal static class AppIcon
    {
        // The multi-size icon is embedded as a managed resource; pick the best size for the request.
        public static Icon Load(Size size)
        {
            var asm = typeof(AppIcon).Assembly;
            using (var s = asm.GetManifestResourceStream("LiveWall.app.ico"))
            {
                if (s == null) return SystemIcons.Application;
                return new Icon(s, size);
            }
        }
    }
}
