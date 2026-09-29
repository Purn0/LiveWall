using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Ink;
using LiveWall.Interop;

namespace LiveWall
{
    // Settings in tabs: Wallpapers, Slideshow & scaling, Boards & drawing, Music, Battery & performance, General.
    // Sized for a 1366x768 screen at 125% (tabs scroll when needed); the size and the last tab are remembered.
    internal sealed class SettingsForm : Form
    {
        public const string TabWallpapers = "wallpapers", TabSlideshow = "slideshow", TabBoards = "boards", TabMusic = "music",
            TabBattery = "battery", TabGeneral = "general";
        static readonly int[] Intervals = { 0, 1, 5, 10, 15, 30, 60, 120, 360, 720, 1440 };
        static readonly FitMode[] Fits = { FitMode.Fill, FitMode.Fit, FitMode.Center };
        static readonly string[] DefaultKinds = { MusicSpec.None, MusicSpec.Random, MusicSpec.Theme, MusicSpec.Video, MusicSpec.Custom };
        static readonly string[] WallpaperKinds = { MusicSpec.Default, MusicSpec.None, MusicSpec.Random, MusicSpec.Theme, MusicSpec.Video, MusicSpec.Custom };
        static readonly Size DefaultClient = new Size(660, 500);   // 96-DPI units

        readonly AppController app;
        readonly List<string> sources;
        readonly Settings baseline;
        readonly MusicSettings baselineMusic;
        // Per-wallpaper music chosen here, not applied yet: wallpaper path (or "board") -> spec, null = the default.
        readonly Dictionary<string, string> musicChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string currentKey, currentName;   // the wallpaper shown when the window opened
        float dpiScale = 1;
        bool loading;

        TabControl tabs;
        // Wallpapers
        ListView list;
        Button removeButton, upButton, downButton, musicButton;
        // Slideshow & scaling
        ComboBox intervalBox, fitBox;
        CheckBox shuffleBox;
        HotkeyBox collectionKeyBox;
        // Boards & drawing
        HotkeyBox boardKeyBox, drawKeyBox;
        ComboBox boardStyleBox;
        CheckBox inkBox, animateBox;
        Button removeInkButton;
        // Music
        Label nowLabel, moodLabel, defaultCustomLabel, currentCustomLabel, keyLabel, volumeLabel;
        Button playButton, nextButton;
        ComboBox defaultBox, currentBox;
        TextBox folderBox, keyBox;
        TrackBar volumeBar;
        NumericUpDown fadeBox, graceBox, resumeBox;
        CheckBox otherBox, frontBox, musicBatteryBox, musicSaverBox, aiBox;
        HotkeyBox musicKeyBox;
        string defaultCustom, currentCustom;
        // Battery & performance, General
        CheckBox coveredBox, fullscreenBox, batteryBox, saverBox, startupBox, trayBox, syncBox;
        Label statusLabel;

        public SettingsForm(AppController app, Settings current, string tab)
        {
            this.app = app;
            baseline = current.Clone();
            baselineMusic = baseline.Music;
            sources = new List<string>(current.Sources);
            currentKey = app.MusicWallpaperKey;
            currentName = app.MusicWallpaperName;

            SuspendLayout();
            Text = "LiveWall Settings";
            Font = SystemFonts.MessageBoxFont;
            Icon = AppIcon.Load(new Size(32, 32));
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ClientSize = DefaultClient;
            MinimumSize = new Size(560, 400);
            BuildUi();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
            dpiScale = DeviceDpiScale();
            list.Columns[0].Width = (int)(150 * dpiScale);
            list.Columns[1].Width = (int)(110 * dpiScale);
            list.Columns[2].Width = (int)(110 * dpiScale);
            list.Columns[3].Width = (int)(200 * dpiScale);
            RestoreSize(current);
            LoadValues(current);
            RefreshList();
            ShowTab(tab);
            FormClosing += (s, e) => RememberSize();
            FormClosed += (s, e) => app.ResumeHotkeys();
        }

        public void ShowTab(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            foreach (TabPage p in tabs.TabPages) if (p.Name == id) { tabs.SelectedTab = p; return; }
        }

        // The remembered size (96-DPI units), kept on the screen.
        void RestoreSize(Settings s)
        {
            if (s.SettingsWidth > 0 && s.SettingsHeight > 0)
                ClientSize = new Size((int)(s.SettingsWidth * dpiScale), (int)(s.SettingsHeight * dpiScale));
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        }

        void RememberSize()
        {
            if (WindowState != FormWindowState.Normal) return;
            app.RememberSettingsWindow((int)(ClientSize.Width / dpiScale), (int)(ClientSize.Height / dpiScale), tabs.SelectedTab != null ? tabs.SelectedTab.Name : "");
        }

        float DeviceDpiScale()
        {
            using (var g = CreateGraphics()) return g.DpiX / 96f;
        }

        // ================================================================== layout

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 10, 10, 10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0) };
            root.Controls.Add(tabs, 0, 0);

            BuildWallpapersTab();
            BuildSlideshowTab();
            BuildBoardsTab();
            BuildMusicTab();
            BuildBatteryTab();
            BuildGeneralTab();

            // Bottom bar: status line, OK / Cancel / Apply.
            statusLabel = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText, UseMnemonic = false };
            var ok = MakeButton("OK", (s, e) => { Apply(); Close(); });
            var cancel = MakeButton("Cancel", (s, e) => Close());
            var apply = MakeButton("Apply", (s, e) => Apply());
            ok.Width = cancel.Width = apply.Width = 88;
            ok.Margin = cancel.Margin = new Padding(0, 0, 8, 0);
            apply.Margin = new Padding(0);
            AcceptButton = ok;
            CancelButton = cancel;
            var bottomButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            bottomButtons.Controls.AddRange(new Control[] { ok, cancel, apply });
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(statusLabel, 0, 0);
            bottom.Controls.Add(bottomButtons, 1, 0);
            root.Controls.Add(bottom, 0, 1);
        }

        // A tab whose rows stack from the top (and scroll when the window is small), or fill it (the wallpaper list).
        TableLayoutPanel Page(string id, string title, bool fill)
        {
            var page = new TabPage(title) { Name = id, BackColor = SystemColors.Window, AutoScroll = !fill, Padding = new Padding(12, 8, 12, 8) };
            var table = new TableLayoutPanel { ColumnCount = 1, Dock = fill ? DockStyle.Fill : DockStyle.Top, AutoSize = !fill, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(table);
            tabs.TabPages.Add(page);
            return table;
        }

        void BuildWallpapersTab()
        {
            var t = Page(TabWallpapers, "Wallpapers", true);
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, AllowDrop = true,
                Dock = DockStyle.Fill, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MinimumSize = new Size(0, 120), Margin = new Padding(0)
            };
            list.Columns.Add("Name", 150);
            list.Columns.Add("Type", 110);
            list.Columns.Add("Music", 110);
            list.Columns.Add("Location", 200);
            list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            list.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.DoubleClick += (s, e) => ChooseMusicForSelected();
            var menu = new ContextMenu();
            var menuMusic = new MenuItem("Music...", (s, e) => ChooseMusicForSelected());
            var menuRemove = new MenuItem("Remove", (s, e) => RemoveSelected());
            menu.MenuItems.AddRange(new[] { menuMusic, menuRemove });
            menu.Popup += (s, e) => { menuMusic.Enabled = menuRemove.Enabled = list.SelectedIndices.Count > 0; };
            list.ContextMenu = menu;

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10, 0, 0, 0) };
            buttons.Controls.Add(MakeButton("Add files...", (s, e) => AddFilesDialog()));
            buttons.Controls.Add(MakeButton("Add folder...", (s, e) => AddFolderDialog()));
            removeButton = MakeButton("Remove", (s, e) => RemoveSelected());
            upButton = MakeButton("Move up", (s, e) => MoveSelected(-1));
            downButton = MakeButton("Move down", (s, e) => MoveSelected(1));
            musicButton = MakeButton("Music...", (s, e) => ChooseMusicForSelected());
            musicButton.Margin = new Padding(0, 12, 6, 6);
            buttons.Controls.AddRange(new Control[] { removeButton, upButton, downButton, musicButton });

            var listRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            listRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            listRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            listRow.Controls.Add(list, 0, 0);
            listRow.Controls.Add(buttons, 1, 0);
            AddRow(t, listRow, true);
            AddRow(t, Note("Videos, GIFs, pictures or whole folders (you can also drag them onto the list). GIFs are converted once into " +
                           "hardware-decoded video. Music: right-click a wallpaper (or Music...) to give it its own music; the default is on the Music tab."), false);
        }

        void BuildSlideshowTab()
        {
            var t = Page(TabSlideshow, "Slideshow & scaling", false);
            intervalBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(6, 3, 20, 3) };
            foreach (int m in Intervals) intervalBox.Items.Add(TrayIcon.DescribeInterval(m));
            shuffleBox = new CheckBox { Text = "Shuffle", AutoSize = true, Margin = new Padding(0, 6, 0, 3) };
            fitBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(6, 3, 3, 3) };
            foreach (FitMode f in Fits) fitBox.Items.Add(TrayIcon.DescribeFit(f));
            collectionKeyBox = Hotkey();
            var collections = MakeButton("Collections...", (s, e) => app.ShowCollections());
            collections.Margin = new Padding(0, 1, 0, 0);

            AddRow(t, Line(Label("Change wallpaper every"), intervalBox, shuffleBox), false);
            AddRow(t, Line(Label("Scaling"), fitBox), false);
            AddRow(t, Header("Collections"), false);
            AddRow(t, Line(Label("Next collection shortcut"), collectionKeyBox, collections), false);
            AddRow(t, Note("Collections are named sets of wallpapers (e.g. Day and Night). One can play by itself between two times every day. " +
                           "The slideshow waits while you draw on the wallpaper and while the desktop is covered."), false);
        }

        void BuildBoardsTab()
        {
            var t = Page(TabBoards, "Boards & drawing", false);
            boardKeyBox = Hotkey();
            drawKeyBox = Hotkey();
            boardStyleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(6, 3, 12, 3) };
            foreach (string st in InkRenderer.Styles) boardStyleBox.Items.Add(InkRenderer.StyleName(st));
            inkBox = Check("Show my drawings on wallpapers");
            animateBox = Check("Play glowing animations on boards (twinkle, pulse, flicker)");
            var today = MakeButton("Open today's board", (s, e) => app.ShowBoard(BoardKind.Daily, DateTime.Today, false));
            var permanent = MakeButton("Open permanent board", (s, e) => app.ShowBoard(BoardKind.Permanent, DateTime.Today, false));
            removeInkButton = MakeButton("Remove drawings from current wallpaper", (s, e) => { app.ClearWallpaperInk(); removeInkButton.Enabled = app.CurrentWallpaperHasInk; });
            var folder = MakeButton("Open boards folder", (s, e) => app.OpenBoardsFolder());
            foreach (var b in new[] { today, permanent, removeInkButton, folder }) { b.AutoSize = true; b.MinimumSize = new Size(0, 28); }

            AddRow(t, Line(Label("Board shortcut"), boardKeyBox, Label("   Draw shortcut"), drawKeyBox), false);
            AddRow(t, Line(Label("New boards look like"), boardStyleBox), false);
            AddRow(t, inkBox, false);
            AddRow(t, animateBox, false);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(600, 0) };
            buttons.Controls.AddRange(new Control[] { today, permanent, removeInkButton, folder });
            AddRow(t, buttons, false);
            AddRow(t, Note("Drawing keys: P pen, H highlighter, E eraser, V select, L / A / R / O line, arrow, rectangle, ellipse, F fill, T text, " +
                           "I eyedropper, G glow, 1-9 colors, [ ] size, Ctrl+Z / Ctrl+Y undo / redo, Ctrl+S save a copy, Esc done."), false);
            AddRow(t, Note("Glow (G while drawing) makes pens, shapes and text shine; animated glow turns a board into a short looping " +
                           "video, played and paused like a video wallpaper. Without animations a board is a still picture and costs nothing."), false);
            AddRow(t, Note("The board shortcut shows the board you used last (today's or the permanent one) as the wallpaper and hides it again; " +
                           "a fresh daily board every day, earlier days are kept. The draw shortcut opens drawing on the board or wallpaper. Boards " +
                           "are also saved as pictures in Pictures\\LiveWall Boards. To change a shortcut, click its box and press the new keys " +
                           "(Backspace = none)."), false);
        }

        void BuildMusicTab()
        {
            var t = Page(TabMusic, "Music", false);
            nowLabel = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(360, 0), Margin = new Padding(0, 7, 8, 3) };
            playButton = MakeButton("Pause", (s, e) => { app.ToggleMusicMute(); RefreshNowPlaying(); });
            nextButton = MakeButton("Next song", (s, e) => { app.NextTrack(); RefreshNowPlaying(); });
            playButton.Margin = nextButton.Margin = new Padding(0, 2, 6, 2);

            defaultBox = Combo(DefaultKinds.Select(MusicSpec.Describe));
            defaultBox.SelectedIndexChanged += (s, e) =>
            {
                if (!loading && Kind(defaultBox, DefaultKinds) == MusicSpec.Custom && defaultCustom == null) PickCustom(true, false);
                UpdateMusicLabels();
            };
            defaultCustomLabel = Note("");
            var defaultFiles = Small("Files...", (s, e) => PickCustom(true, false));
            var defaultFolder = Small("Folder...", (s, e) => PickCustom(true, true));

            currentBox = Combo(WallpaperKinds.Select(MusicSpec.Describe));
            currentBox.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                if (Kind(currentBox, WallpaperKinds) == MusicSpec.Custom && currentCustom == null) { PickCustom(false, false); return; }
                SetCurrentWallpaperChoice();
            };
            currentCustomLabel = Note("");
            var currentFiles = Small("Files...", (s, e) => PickCustom(false, false));
            var currentFolder = Small("Folder...", (s, e) => PickCustom(false, true));
            moodLabel = Note("");

            folderBox = new TextBox { Width = 330, Margin = new Padding(6, 3, 6, 3) };
            var browse = Small("Browse...", (s, e) => { var f = FolderPicker.Pick(Handle, "Music folder"); if (f.Count > 0) folderBox.Text = f[0]; });

            volumeBar = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 200, AutoSize = false, Height = 30,
                                     Margin = new Padding(6, 0, 0, 0), BackColor = SystemColors.Window };
            volumeLabel = new Label { AutoSize = true, Margin = new Padding(0, 7, 12, 3) };
            volumeBar.ValueChanged += (s, e) => volumeLabel.Text = volumeBar.Value + "%";
            musicKeyBox = Hotkey();
            fadeBox = new NumericUpDown { Minimum = 0.2m, Maximum = 10m, DecimalPlaces = 1, Increment = 0.1m, Width = 60, Margin = new Padding(6, 3, 6, 3) };
            graceBox = Number(1, 600);
            resumeBox = Number(1, 600);
            otherBox = Check("another app plays sound (it comes back when they are quiet)");
            frontBox = Check("a fullscreen or maximized app is in front (not File Explorer, Settings or the desktop)");
            musicBatteryBox = Check("running on battery");
            musicSaverBox = Check("Energy Saver / Battery Saver is on");
            aiBox = Check("Ask AI (Claude) for the mood of each wallpaper");
            keyBox = new TextBox { Width = 300, UseSystemPasswordChar = true, Margin = new Padding(6, 3, 6, 3) };
            keyLabel = Note("");
            var forget = Small("Forget key", (s, e) => { baselineMusic.AiKey = ""; keyBox.Text = ""; UpdateMusicLabels(); });

            AddRow(t, Header("Now playing", true), false);
            AddRow(t, Line(nowLabel, playButton, nextButton), false);
            AddRow(t, Header("Sources"), false);
            AddRow(t, Line(Label("All wallpapers", 100), defaultBox, defaultFiles, defaultFolder), false);
            AddRow(t, defaultCustomLabel, false);
            // The name and mood go on the line below (a long name would push the buttons off the edge).
            AddRow(t, Line(Label(currentKey == "board" ? "Boards" : "This wallpaper", 100), currentBox, currentFiles, currentFolder), false);
            AddRow(t, moodLabel, false);
            AddRow(t, currentCustomLabel, false);
            if (currentKey == null) currentBox.Enabled = currentFiles.Enabled = currentFolder.Enabled = false;
            AddRow(t, Line(Label("Music folder", 100), folderBox, browse), false);
            AddRow(t, Note("\"Random\" shuffles this folder. \"By theme\" plays its subfolder named after the wallpaper's mood - calm, energetic, " +
                           "dark, happy, dreamy or cozy - or the whole folder if there is none. \"The video's own sound\" plays a video " +
                           "wallpaper's soundtrack. Other wallpapers: Wallpapers tab, right-click > Music."), false);
            AddRow(t, Note("With 3 or more wallpapers in a slideshow, each wallpaper gets its own song, repeated (changing every 15 minutes or " +
                           "less), or one song for the first half of its time and another for the second half. The song fades out just before " +
                           "the wallpaper changes. With one or two wallpapers, or on a board, the songs just play on."), false);
            AddRow(t, Header("Playback"), false);
            AddRow(t, Line(Label("Volume"), volumeBar, volumeLabel, Label("Pause / play shortcut"), musicKeyBox), false);
            AddRow(t, Line(Label("Fade"), fadeBox, Label("s     Close the player after"), graceBox, Label("s of silence;  resume after"), resumeBox, Label("s of quiet")), false);
            AddRow(t, Header("Silent while"), false);
            AddRow(t, otherBox, false);
            AddRow(t, frontBox, false);
            AddRow(t, musicBatteryBox, false);
            AddRow(t, musicSaverBox, false);
            AddRow(t, Note("Always silent while the PC is locked, the screen is off or asleep. Silencing fades out, pauses and then closes the " +
                           "player completely, so the sound card can sleep; the music comes back from the same place."), false);
            AddRow(t, Header("Mood by AI (optional)"), false);
            AddRow(t, aiBox, false);
            AddRow(t, Line(Label("API key"), keyBox, forget), false);
            AddRow(t, keyLabel, false);
            AddRow(t, Note("Sends each wallpaper's picture (a small JPEG) once to api.anthropic.com with your key; the answer is kept. " +
                           "Without it, the mood comes from the picture's colors on this PC."), false);
        }

        void BuildBatteryTab()
        {
            var t = Page(TabBattery, "Battery & performance", false);
            coveredBox = Check("Pause when the desktop is completely covered by windows (e.g. maximized apps)");
            fullscreenBox = Check("Pause on every screen while a fullscreen app or game is running");
            batteryBox = Check("Pause while running on battery");
            saverBox = Check("Pause while Energy Saver / Battery Saver is on");
            AddRow(t, coveredBox, false);
            AddRow(t, fullscreenBox, false);
            AddRow(t, batteryBox, false);
            AddRow(t, saverBox, false);
            AddRow(t, Note("Always paused while the screen is off, locked or asleep. Videos are decoded by the GPU, never open an audio stream, " +
                           "and are fully unloaded after a minute out of sight. Music has its own rules on the Music tab."), false);
        }

        void BuildGeneralTab()
        {
            var t = Page(TabGeneral, "General", false);
            startupBox = Check("Start LiveWall when I sign in to Windows");
            trayBox = Check("Show the LiveWall icon in the notification area");
            syncBox = Check("Keep the Windows wallpaper in sync (first frame) for accent colors and a seamless sign-in");
            AddRow(t, startupBox, false);
            AddRow(t, trayBox, false);
            AddRow(t, syncBox, false);
            AddRow(t, Note("Without the icon, start LiveWall from the Start menu to open these settings (it never runs twice). The Start " +
                           "menu also has LiveWall Board, LiveWall Draw and LiveWall Music: pin them to the taskbar for one-click buttons."), false);
        }

        // ------------------------------------------------------------------ small helpers

        static void AddRow(TableLayoutPanel t, Control c, bool stretch)
        {
            t.RowStyles.Add(stretch ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
        }

        Label Header(string text, bool first = false)
        {
            return new Label { Text = text, AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, Font.Size + 1f, FontStyle.Bold),
                               Margin = new Padding(0, first ? 2 : 12, 0, 3) };
        }

        static Label Label(string text, int minWidth = 0)
        {
            return new Label { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 7, 0, 3), MinimumSize = new Size(minWidth, 0) };
        }

        static FlowLayoutPanel Line(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            f.Controls.AddRange(controls);
            return f;
        }

        static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 0, 2) }; }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(590, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 2) };
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, Width = 112, Height = 28, Margin = new Padding(0, 0, 6, 6), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        static Button Small(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(76, 26), Margin = new Padding(0, 2, 6, 2), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        static ComboBox Combo(IEnumerable<string> items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(6, 3, 6, 3) };
            foreach (string s in items) c.Items.Add(s);
            return c;
        }

        static NumericUpDown Number(int min, int max) { return new NumericUpDown { Minimum = min, Maximum = max, Width = 56, Margin = new Padding(6, 3, 6, 3) }; }

        // While a shortcut box has focus, the current shortcuts must not fire (so they can be typed in).
        HotkeyBox Hotkey()
        {
            var hk = new HotkeyBox { Margin = new Padding(6, 3, 6, 3) };
            hk.Enter += (s, e) => app.SuspendHotkeys();
            hk.Leave += (s, e) => app.ResumeHotkeys();
            return hk;
        }

        static string Kind(ComboBox box, string[] kinds) { return box.SelectedIndex >= 0 && box.SelectedIndex < kinds.Length ? kinds[box.SelectedIndex] : kinds[0]; }

        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 3) + "..."; }

        // ================================================================== values

        void LoadValues(Settings s)
        {
            loading = true;
            int ii = Array.IndexOf(Intervals, s.IntervalMinutes);
            if (ii < 0)
            {
                intervalBox.Items.Add(TrayIcon.DescribeInterval(s.IntervalMinutes));
                ii = intervalBox.Items.Count - 1;
            }
            intervalBox.SelectedIndex = ii;
            int fi = Array.IndexOf(Fits, s.Fit);
            fitBox.SelectedIndex = fi < 0 ? 0 : fi;
            shuffleBox.Checked = s.Shuffle;
            coveredBox.Checked = s.PauseWhenCovered;
            fullscreenBox.Checked = s.PauseOnFullscreen;
            batteryBox.Checked = s.PauseOnBattery;
            saverBox.Checked = s.PauseOnEnergySaver;
            trayBox.Checked = s.ShowTrayIcon;
            syncBox.Checked = s.SyncWindowsWallpaper;
            startupBox.Checked = Startup.IsEnabled();
            boardKeyBox.Text = s.HotkeyBoard;
            drawKeyBox.Text = s.HotkeyDraw;
            collectionKeyBox.Text = s.HotkeyCollection;
            int si = Array.IndexOf(InkRenderer.Styles, s.BoardStyle);
            boardStyleBox.SelectedIndex = si < 0 ? 0 : si;
            inkBox.Checked = s.ShowWallpaperInk;
            animateBox.Checked = s.AnimateBoards;
            removeInkButton.Enabled = app.CurrentWallpaperHasInk;

            var m = s.Music;
            string dk = MusicSpec.Kind(m.Default);
            if (dk == MusicSpec.Custom) defaultCustom = m.Default;
            defaultBox.SelectedIndex = Math.Max(0, Array.IndexOf(DefaultKinds, dk));
            string mine = app.MusicOverrideFor(currentKey);
            string wk = mine == null ? MusicSpec.Default : MusicSpec.Kind(mine);
            if (wk == MusicSpec.Custom) currentCustom = mine;
            currentBox.SelectedIndex = Math.Max(0, Array.IndexOf(WallpaperKinds, wk));
            folderBox.Text = m.Folder;
            try { folderBox.SetCueBanner(m.EffectiveFolder); } catch { }
            volumeBar.Value = Math.Max(0, Math.Min(100, m.Volume));
            volumeLabel.Text = volumeBar.Value + "%";
            musicKeyBox.Text = m.Hotkey;
            fadeBox.Value = Math.Max(fadeBox.Minimum, Math.Min(fadeBox.Maximum, m.FadeMs / 1000m));
            graceBox.Value = Math.Max(1, Math.Min(600, m.GraceSeconds));
            resumeBox.Value = Math.Max(1, Math.Min(600, m.ResumeSeconds));
            otherBox.Checked = m.SilenceForOtherAudio;
            frontBox.Checked = m.PauseOnFullscreen;
            musicBatteryBox.Checked = m.PauseOnBattery;
            musicSaverBox.Checked = m.PauseOnEnergySaver;
            aiBox.Checked = m.AskAi;
            string mood = app.CurrentWallpaperMood;
            string about = currentKey == null || currentKey == "board" || currentName == null ? "" : Shorten(currentName, 60);
            if (mood != null) about += (about.Length > 0 ? "  \u2022  mood: " : "Mood: ") + mood.Replace(" ai", " (by AI)");
            moodLabel.Text = about;
            loading = false;
            RefreshNowPlaying();
            UpdateMusicLabels();
        }

        Settings Collect()
        {
            var s = baseline.Clone();
            s.Sources = new List<string>(sources);
            int ii = intervalBox.SelectedIndex;
            s.IntervalMinutes = ii >= 0 && ii < Intervals.Length ? Intervals[ii] : baseline.IntervalMinutes;
            s.Fit = Fits[Math.Max(0, fitBox.SelectedIndex)];
            s.Shuffle = shuffleBox.Checked;
            s.PauseWhenCovered = coveredBox.Checked;
            s.PauseOnFullscreen = fullscreenBox.Checked;
            s.PauseOnBattery = batteryBox.Checked;
            s.PauseOnEnergySaver = saverBox.Checked;
            s.ShowTrayIcon = trayBox.Checked;
            s.SyncWindowsWallpaper = syncBox.Checked;
            // Four shortcuts; a duplicate of an earlier one is dropped (board, draw, collection, music).
            var used = new List<string>();
            s.HotkeyBoard = Unique(boardKeyBox, used);
            s.HotkeyDraw = Unique(drawKeyBox, used);
            s.HotkeyCollection = Unique(collectionKeyBox, used);
            s.BoardStyle = InkRenderer.Styles[Math.Max(0, boardStyleBox.SelectedIndex)];
            s.ShowWallpaperInk = inkBox.Checked;
            s.AnimateBoards = animateBox.Checked;

            var m = baselineMusic.Clone();
            string dk = Kind(defaultBox, DefaultKinds);
            m.Default = dk == MusicSpec.Custom ? (defaultCustom ?? MusicSpec.None) : dk;
            m.Folder = folderBox.Text.Trim();
            m.Volume = volumeBar.Value;
            m.Hotkey = Unique(musicKeyBox, used);
            m.FadeMs = (int)(fadeBox.Value * 1000);
            m.GraceSeconds = (int)graceBox.Value;
            m.ResumeSeconds = (int)resumeBox.Value;
            m.SilenceForOtherAudio = otherBox.Checked;
            m.PauseOnFullscreen = frontBox.Checked;
            m.PauseOnBattery = musicBatteryBox.Checked;
            m.PauseOnEnergySaver = musicSaverBox.Checked;
            m.AskAi = aiBox.Checked;
            if (keyBox.Text.Trim().Length > 0) { m.AiKey = Dpapi.Protect(keyBox.Text.Trim()); baselineMusic.AiKey = m.AiKey; keyBox.Text = ""; }
            s.Music = m;
            return s;
        }

        static string Unique(HotkeyBox box, List<string> used)
        {
            string k = box.Text.Trim();
            if (k.Length > 0 && used.Any(u => string.Equals(u, k, StringComparison.OrdinalIgnoreCase))) { box.Text = ""; return ""; }
            if (k.Length > 0) used.Add(k);
            return k;
        }

        void Apply()
        {
            if (!trayBox.Checked && baseline.ShowTrayIcon)
            {
                MessageBox.Show(this, "The tray icon will be hidden. To open these settings again, start LiveWall from the Start menu " +
                    "(it will not start a second copy).", "LiveWall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Settings s = Collect();
            try { Startup.Set(startupBox.Checked); }
            catch (Exception ex) { Log.Error("Startup registration", ex); }
            app.ApplySettings(s);                                   // everything but the music...
            app.ApplyMusicSettings(s.Music, new Dictionary<string, string>(musicChanges));   // ...which goes here with the per-wallpaper choices
            musicChanges.Clear();
            baseline.Sources = new List<string>(s.Sources);
            baseline.ShowTrayIcon = s.ShowTrayIcon;
            RefreshList();
            RefreshNowPlaying();
            UpdateMusicLabels();
        }

        public void SetStatus(string text) { if (statusLabel != null && statusLabel.Text != text) statusLabel.Text = text; }

        // ================================================================== music tab

        // Also called by the app when the song or the state changes.
        public void RefreshNowPlaying()
        {
            string text = app.MusicPlaying ? "\u266A  " + app.MusicTrackTitle : app.MusicStatus;
            if (nowLabel.Text != text) nowLabel.Text = text;
            playButton.Text = app.MusicMuted ? "Play" : "Pause";
            nextButton.Enabled = app.CanSkipTrack;
            if (removeInkButton != null) removeInkButton.Enabled = app.CurrentWallpaperHasInk;
        }

        void UpdateMusicLabels()
        {
            defaultCustomLabel.Text = Kind(defaultBox, DefaultKinds) == MusicSpec.Custom && defaultCustom != null ? Paths(defaultCustom) : "";
            currentCustomLabel.Text = Kind(currentBox, WallpaperKinds) == MusicSpec.Custom && currentCustom != null ? Paths(currentCustom) : "";
            keyLabel.Text = baselineMusic.AiKey.Length > 0 ? "A key is stored (encrypted for your Windows account). Type a new one to replace it." : "No key stored.";
            foreach (var l in new[] { defaultCustomLabel, currentCustomLabel, moodLabel }) l.Visible = l.Text.Length > 0;
        }

        static string Paths(string spec)
        {
            var p = MusicSpec.CustomPaths(spec);
            return string.Join("; ", p.Take(3).Select(x => Path.GetFileName(x.TrimEnd('\\')))) + (p.Count > 3 ? " (+" + (p.Count - 3) + ")" : "");
        }

        void PickCustom(bool forDefault, bool folder)
        {
            List<string> chosen;
            if (folder) chosen = FolderPicker.Pick(Handle, "Music folder");
            else
            {
                chosen = new List<string>();
                using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MusicLibrary.DialogFilter, Title = "Choose music" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) chosen.AddRange(dlg.FileNames);
            }
            loading = true;
            if (chosen.Count > 0)
            {
                string spec = MusicSpec.MakeCustom(chosen);
                if (forDefault) { defaultCustom = spec; defaultBox.SelectedIndex = Array.IndexOf(DefaultKinds, MusicSpec.Custom); }
                else { currentCustom = spec; currentBox.SelectedIndex = Array.IndexOf(WallpaperKinds, MusicSpec.Custom); }
            }
            else
            {
                // Nothing chosen: don't leave "Custom" selected without files.
                if (forDefault && defaultCustom == null && Kind(defaultBox, DefaultKinds) == MusicSpec.Custom) defaultBox.SelectedIndex = 0;
                if (!forDefault && currentCustom == null && Kind(currentBox, WallpaperKinds) == MusicSpec.Custom) currentBox.SelectedIndex = 0;
            }
            loading = false;
            if (!forDefault) SetCurrentWallpaperChoice();
            UpdateMusicLabels();
        }

        // The Music tab's "This wallpaper" choice, kept with the per-wallpaper choices of the Wallpapers tab.
        void SetCurrentWallpaperChoice()
        {
            if (currentKey == null) return;
            string wk = Kind(currentBox, WallpaperKinds);
            musicChanges[currentKey] = wk == MusicSpec.Default ? null : wk == MusicSpec.Custom ? currentCustom : wk;
            UpdateMusicLabels();
            RefreshList();
        }

        // ================================================================== wallpaper list

        string SpecFor(string wallpaper)
        {
            string spec;
            return musicChanges.TryGetValue(wallpaper, out spec) ? spec : app.MusicOverrideFor(wallpaper);
        }

        static string ShortMusic(string spec)
        {
            switch (spec == null ? MusicSpec.Default : MusicSpec.Kind(spec))
            {
                case MusicSpec.None: return "None";
                case MusicSpec.Random: return "Random";
                case MusicSpec.Theme: return "By theme";
                case MusicSpec.Video: return "Video's own sound";
                case MusicSpec.Custom: return MusicSpec.Describe(spec);
                default: return "Default";
            }
        }

        // A file: its choice. A folder: the choice of its wallpapers ("Mixed" when they differ).
        string MusicColumn(string src, List<string> files)
        {
            if (files == null) return ShortMusic(SpecFor(src));
            var kinds = files.Select(f => ShortMusic(SpecFor(f))).Distinct().ToList();
            return kinds.Count == 0 ? "Default" : kinds.Count == 1 ? kinds[0] : "Mixed";
        }

        void RefreshList()
        {
            var selected = list.SelectedIndices.Cast<int>().ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (string src in sources)
            {
                var item = new ListViewItem();
                item.ToolTipText = src;
                if (Directory.Exists(src))
                {
                    item.Text = Path.GetFileName(src.TrimEnd('\\'));
                    if (item.Text.Length == 0) item.Text = src;
                    var files = Playlist.Resolve(new[] { src }).Select(i => i.Path).ToList();
                    item.SubItems.Add("Folder (" + files.Count + (files.Count == 1 ? " item)" : " items)"));
                    item.SubItems.Add(MusicColumn(src, files));
                    item.SubItems.Add(Path.GetDirectoryName(src.TrimEnd('\\')) ?? src);
                }
                else
                {
                    item.Text = Path.GetFileName(src);
                    MediaKind k;
                    bool exists = File.Exists(src);
                    item.SubItems.Add(!exists ? "Missing" : MediaTypes.TryClassify(src, out k) ? MediaTypes.Describe(k) : "Unsupported");
                    item.SubItems.Add(MusicColumn(src, null));
                    item.SubItems.Add(Path.GetDirectoryName(src));
                    if (!exists) item.ForeColor = SystemColors.GrayText;
                }
                list.Items.Add(item);
            }
            foreach (int i in selected) if (i < list.Items.Count) list.Items[i].Selected = true;
            list.EndUpdate();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            int sel = list.SelectedIndices.Count == 1 ? list.SelectedIndices[0] : -1;
            removeButton.Enabled = musicButton.Enabled = list.SelectedIndices.Count > 0;
            upButton.Enabled = sel > 0;
            downButton.Enabled = sel >= 0 && sel < sources.Count - 1;
        }

        // Music for the selected wallpapers (a folder: every wallpaper in it).
        void ChooseMusicForSelected()
        {
            var rows = list.SelectedIndices.Cast<int>().Select(i => sources[i]).ToList();
            if (rows.Count == 0) return;
            var targets = new List<string>();
            foreach (string src in rows)
            {
                if (Directory.Exists(src)) targets.AddRange(Playlist.Resolve(new[] { src }).Select(i => i.Path));
                else targets.Add(src);
            }
            if (targets.Count == 0) return;
            MediaKind k;
            bool anyVideo = targets.Any(p => MediaTypes.TryClassify(p, out k) && k == MediaKind.Video);
            var specs = targets.Select(SpecFor).Distinct().ToList();
            string title = rows.Count == 1 && !Directory.Exists(rows[0]) ? "Music for " + Path.GetFileName(rows[0])
                : "Music for " + targets.Count + " wallpaper" + (targets.Count == 1 ? "" : "s");
            using (var dlg = new WallpaperMusicDialog(title, specs.Count == 1 ? specs[0] : null, Kind(defaultBox, DefaultKinds) == MusicSpec.Custom
                                                          ? (defaultCustom ?? MusicSpec.None) : Kind(defaultBox, DefaultKinds), anyVideo))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                foreach (string p in targets) musicChanges[p] = dlg.Spec;
                if (currentKey != null && targets.Any(p => string.Equals(p, currentKey, StringComparison.OrdinalIgnoreCase)))
                {
                    // Keep the Music tab's "This wallpaper" in step.
                    loading = true;
                    string wk = dlg.Spec == null ? MusicSpec.Default : MusicSpec.Kind(dlg.Spec);
                    if (wk == MusicSpec.Custom) currentCustom = dlg.Spec;
                    currentBox.SelectedIndex = Math.Max(0, Array.IndexOf(WallpaperKinds, wk));
                    loading = false;
                    UpdateMusicLabels();
                }
            }
            RefreshList();
        }

        void AddPaths(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string p in paths)
            {
                string full;
                try { full = Path.GetFullPath(p); } catch { continue; }
                MediaKind k;
                if (!Directory.Exists(full) && !(File.Exists(full) && MediaTypes.TryClassify(full, out k))) continue;
                if (sources.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase))) continue;
                sources.Add(full);
                added++;
            }
            if (added > 0) RefreshList();
        }

        void AddFilesDialog()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaTypes.DialogFilter, Title = "Add wallpapers" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
            }
        }

        void AddFolderDialog()
        {
            AddPaths(FolderPicker.Pick(Handle, "Choose folders with wallpapers"));
        }

        void RemoveSelected()
        {
            var idx = list.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
            foreach (int i in idx) sources.RemoveAt(i);
            if (idx.Count > 0) { list.SelectedIndices.Clear(); RefreshList(); }
        }

        void MoveSelected(int dir)
        {
            if (list.SelectedIndices.Count != 1) return;
            int i = list.SelectedIndices[0], j = i + dir;
            if (j < 0 || j >= sources.Count) return;
            string t = sources[i]; sources[i] = sources[j]; sources[j] = t;
            list.SelectedIndices.Clear();
            RefreshList();
            list.Items[j].Selected = true;
            list.Items[j].Focused = true;
            list.Select();
        }
    }

    // Click, then press a shortcut: shows it as "Ctrl+Alt+B". Backspace/Delete clears it.
    internal sealed class HotkeyBox : TextBox
    {
        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Width = 120;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if (k == Keys.Tab && (mods == Keys.None || mods == Keys.Shift)) return base.ProcessCmdKey(ref msg, keyData);
            if (mods == Keys.None && (k == Keys.Escape || k == Keys.Enter)) return base.ProcessCmdKey(ref msg, keyData);
            if (mods == Keys.None && (k == Keys.Back || k == Keys.Delete)) { Text = ""; return true; }
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return true;
            bool fkey = k >= Keys.F1 && k <= Keys.F24;
            if ((mods & (Keys.Control | Keys.Alt)) == 0 && !fkey) return true;   // needs Ctrl or Alt (or an F key)
            Text = LiveWall.Ink.Hotkeys.Format(keyData);
            SelectionStart = Text.Length;
            return true;
        }
    }

    internal static class CueBanner
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        // Grey hint text shown while a text box is empty.
        public static void SetCueBanner(this TextBox box, string text) { SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), text); }
    }

    internal static class FolderPicker
    {
        // The modern (Vista+) folder picker with multi-select.
        public static List<string> Pick(IntPtr owner, string title)
        {
            var result = new List<string>();
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellApi.CLSID_FileOpenDialog));
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | ShellApi.FOS_PICKFOLDERS | ShellApi.FOS_FORCEFILESYSTEM | ShellApi.FOS_ALLOWMULTISELECT | ShellApi.FOS_PATHMUSTEXIST);
                dlg.SetTitle(title);
                if (dlg.Show(owner) < 0) return result;
                IShellItemArray items;
                if (dlg.GetResults(out items) < 0) return result;
                uint count;
                items.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IShellItem item;
                    if (items.GetItemAt(i, out item) < 0) continue;
                    IntPtr name;
                    if (item.GetDisplayName(ShellApi.SIGDN_FILESYSPATH, out name) >= 0)
                    {
                        string path = ShellApi.TakeCoTaskString(name);
                        if (!string.IsNullOrEmpty(path)) result.Add(path);
                    }
                    Marshal.ReleaseComObject(item);
                }
                Marshal.ReleaseComObject(items);
            }
            catch (Exception ex) { Log.Error("Folder picker", ex); }
            finally { if (dlg != null) Marshal.ReleaseComObject(dlg); }
            return result;
        }
    }
}
