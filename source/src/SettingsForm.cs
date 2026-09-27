using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall
{
    internal sealed class SettingsForm : Form
    {
        static readonly int[] Intervals = { 0, 1, 5, 10, 15, 30, 60, 120, 360, 720, 1440 };
        static readonly FitMode[] Fits = { FitMode.Fill, FitMode.Fit, FitMode.Center };

        readonly AppController app;
        readonly List<string> sources;
        readonly Settings baseline;

        ListView list;
        ComboBox intervalBox, fitBox;
        CheckBox shuffleBox, coveredBox, fullscreenBox, batteryBox, saverBox, startupBox, trayBox, syncBox, inkBox;
        HotkeyBox boardKeyBox, drawKeyBox, collectionKeyBox;
        ComboBox boardStyleBox;
        Label statusLabel;
        Button removeButton, upButton, downButton;

        public SettingsForm(AppController app, Settings current)
        {
            this.app = app;
            baseline = current.Clone();
            sources = new List<string>(current.Sources);

            SuspendLayout();
            Text = "LiveWall Settings";
            Font = SystemFonts.MessageBoxFont;
            Icon = AppIcon.Load(new Size(32, 32));
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ClientSize = new Size(680, 880);      // 96-DPI units; scaled below
            MinimumSize = new Size(640, 700);
            BuildUi();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
            float dpiScale = DeviceDpiScale();
            list.Columns[0].Width = (int)(200 * dpiScale);
            list.Columns[1].Width = (int)(125 * dpiScale);
            list.Columns[2].Width = (int)(215 * dpiScale);
            LoadValues(current);
            RefreshList();
        }

        // ------------------------------------------------------------------ layout

        // One column of rows: section headers, then auto-sized controls; only the wallpaper list stretches.
        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 8, 16, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // Wallpapers
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, AllowDrop = true,
                Dock = DockStyle.Fill, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MinimumSize = new Size(0, 150), Margin = new Padding(0)
            };
            list.Columns.Add("Name", 210);
            list.Columns.Add("Type", 100);
            list.Columns.Add("Location", 230);
            list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            list.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            list.SelectedIndexChanged += (s, e) => UpdateButtons();

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10, 0, 0, 0) };
            buttons.Controls.Add(MakeButton("Add files...", (s, e) => AddFilesDialog()));
            buttons.Controls.Add(MakeButton("Add folder...", (s, e) => AddFolderDialog()));
            removeButton = MakeButton("Remove", (s, e) => RemoveSelected());
            upButton = MakeButton("Move up", (s, e) => MoveSelected(-1));
            downButton = MakeButton("Move down", (s, e) => MoveSelected(1));
            buttons.Controls.AddRange(new Control[] { removeButton, upButton, downButton });

            var listRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 2, 0, 0) };
            listRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            listRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            listRow.Controls.Add(list, 0, 0);
            listRow.Controls.Add(buttons, 1, 0);

            // Slideshow
            intervalBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(6, 3, 20, 3) };
            foreach (int m in Intervals) intervalBox.Items.Add(TrayIcon.DescribeInterval(m));
            shuffleBox = new CheckBox { Text = "Shuffle", AutoSize = true, Margin = new Padding(0, 6, 0, 3) };
            fitBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(6, 3, 3, 3) };
            foreach (FitMode f in Fits) fitBox.Items.Add(TrayIcon.DescribeFit(f));

            // Battery & performance
            coveredBox = Check("Pause when the desktop is completely covered by windows (e.g. maximized apps)");
            fullscreenBox = Check("Pause on every screen while a fullscreen app or game is running");
            batteryBox = Check("Pause while running on battery");
            saverBox = Check("Pause while Energy Saver / Battery Saver is on");

            // Boards & drawing
            boardKeyBox = new HotkeyBox { Margin = new Padding(6, 3, 20, 3) };
            drawKeyBox = new HotkeyBox { Margin = new Padding(6, 3, 3, 3) };
            collectionKeyBox = new HotkeyBox { Margin = new Padding(6, 3, 12, 3) };
            foreach (var hk in new[] { boardKeyBox, drawKeyBox, collectionKeyBox })
            {
                // While a shortcut box has focus, the current shortcuts must not fire (so they can be typed in).
                hk.Enter += (s, e) => app.SuspendHotkeys();
                hk.Leave += (s, e) => app.ResumeHotkeys();
            }
            FormClosed += (s, e) => app.ResumeHotkeys();
            boardStyleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(6, 3, 12, 3) };
            foreach (string st in LiveWall.Ink.InkRenderer.Styles) boardStyleBox.Items.Add(LiveWall.Ink.InkRenderer.StyleName(st));
            inkBox = Check("Show my drawings on wallpapers");
            var boardsFolder = MakeButton("Open board pictures", (s, e) => app.OpenBoardsFolder());
            boardsFolder.Width = 150;
            boardsFolder.Margin = new Padding(0, 1, 0, 0);

            // General
            startupBox = Check("Start LiveWall when I sign in to Windows");
            trayBox = Check("Show the LiveWall icon in the notification area");
            syncBox = Check("Keep the Windows wallpaper in sync (first frame) for accent colors and a seamless sign-in");

            // Bottom bar
            statusLabel = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
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
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(statusLabel, 0, 0);
            bottom.Controls.Add(bottomButtons, 1, 0);

            AddRow(root, Header("Wallpapers", true), false);
            AddRow(root, listRow, true);
            AddRow(root, Note("Add videos, GIFs or images, or whole folders (you can also drag them onto the list). " +
                              "GIFs are converted once into hardware-decoded video, which is far lighter on the battery."), false);
            AddRow(root, Header("Slideshow", false), false);
            AddRow(root, Line(new Label { Text = "Change wallpaper every", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, intervalBox, shuffleBox), false);
            AddRow(root, Line(new Label { Text = "Scaling", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, fitBox), false);
            var collectionsButton = MakeButton("Collections...", (s, e) => app.ShowCollections());
            collectionsButton.Margin = new Padding(0, 1, 0, 0);
            AddRow(root, Line(new Label { Text = "Next collection shortcut", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, collectionKeyBox, collectionsButton), false);
            AddRow(root, Header("Battery & performance", false), false);
            AddRow(root, coveredBox, false);
            AddRow(root, fullscreenBox, false);
            AddRow(root, batteryBox, false);
            AddRow(root, saverBox, false);
            AddRow(root, Note("Always paused while the screen is off, locked or asleep. Videos are decoded by the GPU, " +
                              "never play sound, and are fully unloaded after a minute out of sight."), false);
            AddRow(root, Header("Boards & drawing", false), false);
            AddRow(root, Line(new Label { Text = "Board shortcut", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, boardKeyBox,
                              new Label { Text = "Draw shortcut", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, drawKeyBox), false);
            AddRow(root, Line(new Label { Text = "New boards look like", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, boardStyleBox, boardsFolder), false);
            AddRow(root, inkBox, false);
            AddRow(root, Note("The board shortcut shows the board you used last (today's or the permanent one) as the wallpaper (a fresh daily board every day; earlier days are kept) and " +
                              "hides it again. The draw shortcut opens drawing on the board or wallpaper; Esc when done. Boards are also " +
                              "saved as pictures in Pictures\\LiveWall Boards. To change a shortcut, click its box and press the new keys " +
                              "(Backspace = none)."), false);
            AddRow(root, Header("General", false), false);
            AddRow(root, startupBox, false);
            AddRow(root, trayBox, false);
            AddRow(root, syncBox, false);
            AddRow(root, bottom, false);
        }

        static void AddRow(TableLayoutPanel root, Control c, bool stretch)
        {
            root.RowStyles.Add(stretch ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c, 0, root.RowStyles.Count - 1);
        }

        Label Header(string text, bool first)
        {
            return new Label
            {
                Text = text, AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
                Margin = new Padding(0, first ? 2 : 14, 0, 4)
            };
        }

        float DeviceDpiScale()
        {
            using (var g = CreateGraphics()) return g.DpiX / 96f;
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
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) };
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, Width = 112, Height = 28, Margin = new Padding(0, 0, 6, 6), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        // ------------------------------------------------------------------ values

        void LoadValues(Settings s)
        {
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
            int si = Array.IndexOf(LiveWall.Ink.InkRenderer.Styles, s.BoardStyle);
            boardStyleBox.SelectedIndex = si < 0 ? 0 : si;
            inkBox.Checked = s.ShowWallpaperInk;
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
            s.HotkeyBoard = boardKeyBox.Text.Trim();
            s.HotkeyDraw = drawKeyBox.Text.Trim();
            if (s.HotkeyDraw.Length > 0 && string.Equals(s.HotkeyDraw, s.HotkeyBoard, StringComparison.OrdinalIgnoreCase)) s.HotkeyDraw = "";
            s.HotkeyCollection = collectionKeyBox.Text.Trim();
            if (s.HotkeyCollection.Length > 0 && (string.Equals(s.HotkeyCollection, s.HotkeyBoard, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.HotkeyCollection, s.HotkeyDraw, StringComparison.OrdinalIgnoreCase))) s.HotkeyCollection = "";
            s.BoardStyle = LiveWall.Ink.InkRenderer.Styles[Math.Max(0, boardStyleBox.SelectedIndex)];
            s.ShowWallpaperInk = inkBox.Checked;
            return s;
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
            app.ApplySettings(s);
            baseline.Sources = new List<string>(s.Sources);
            baseline.ShowTrayIcon = s.ShowTrayIcon;
        }

        public void SetStatus(string text) { if (statusLabel != null && statusLabel.Text != text) statusLabel.Text = text; }

        // ------------------------------------------------------------------ list editing

        void RefreshList()
        {
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
                    int n = Playlist.Resolve(new[] { src }).Count;
                    item.SubItems.Add("Folder (" + n + (n == 1 ? " item)" : " items)"));
                    item.SubItems.Add(Path.GetDirectoryName(src.TrimEnd('\\')) ?? src);
                }
                else
                {
                    item.Text = Path.GetFileName(src);
                    MediaKind k;
                    bool exists = File.Exists(src);
                    item.SubItems.Add(!exists ? "Missing" : MediaTypes.TryClassify(src, out k) ? MediaTypes.Describe(k) : "Unsupported");
                    item.SubItems.Add(Path.GetDirectoryName(src));
                    if (!exists) item.ForeColor = SystemColors.GrayText;
                }
                list.Items.Add(item);
            }
            list.EndUpdate();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            int sel = list.SelectedIndices.Count == 1 ? list.SelectedIndices[0] : -1;
            removeButton.Enabled = list.SelectedIndices.Count > 0;
            upButton.Enabled = sel > 0;
            downButton.Enabled = sel >= 0 && sel < sources.Count - 1;
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
            if (idx.Count > 0) RefreshList();
        }

        void MoveSelected(int dir)
        {
            if (list.SelectedIndices.Count != 1) return;
            int i = list.SelectedIndices[0], j = i + dir;
            if (j < 0 || j >= sources.Count) return;
            string t = sources[i]; sources[i] = sources[j]; sources[j] = t;
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
