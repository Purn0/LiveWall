using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LiveWall
{
    // Makes and edits collections: a name, its wallpapers (files and folders) and, optionally, the hours it plays.
    internal sealed class CollectionsForm : Form
    {
        readonly AppController app;
        readonly List<WallpaperCollection> work;

        ListBox names;
        TextBox nameBox;
        ListView sources;
        CheckBox scheduleBox;
        DateTimePicker fromBox, toBox;
        Button deleteButton, upButton, downButton, addFiles, addFolder, addCurrent, removeButton, playButton;
        Label summary;
        bool loading;

        public CollectionsForm(AppController app)
        {
            this.app = app;
            work = app.Collections;

            SuspendLayout();
            Text = "LiveWall Collections";
            Font = SystemFonts.MessageBoxFont;
            Icon = AppIcon.Load(new Size(32, 32));
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ClientSize = new Size(760, 540);
            MinimumSize = new Size(700, 480);
            BuildUi();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
            RefreshNames(work.Count > 0 ? 0 : -1);
        }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(14, 10, 14, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var intro = new Label
            {
                Text = "A collection is a set of wallpapers (for a theme or a mood). Switch between them from the tray icon or with " +
                       (app.CollectionHotkey.Length > 0 ? app.CollectionHotkey : "the collection shortcut") + ". A collection with hours " +
                       "plays by itself during them, e.g. Day 07:00 to 19:00 and Night 19:00 to 07:00; a board you are showing stays until you go back to the wallpaper.",
                AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(intro, 0, 0);
            root.SetColumnSpan(intro, 2);

            // Left: the collections.
            names = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Margin = new Padding(0, 0, 10, 0) };
            names.SelectedIndexChanged += (s, e) => LoadSelected();
            var leftButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 10, 0) };
            var newButton = Button("New", (s, e) => AddCollection(), 58);
            deleteButton = Button("Delete", (s, e) => DeleteSelected(), 60);
            upButton = Button("Up", (s, e) => MoveSelected(-1), 44);
            downButton = Button("Down", (s, e) => MoveSelected(1), 54);
            foreach (var b in new[] { newButton, deleteButton, upButton, downButton }) b.Margin = new Padding(0, 0, 4, 6);
            leftButtons.Controls.AddRange(new Control[] { newButton, deleteButton, upButton, downButton });
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(names, 0, 0);
            left.Controls.Add(leftButtons, 0, 1);
            root.Controls.Add(left, 0, 1);

            // Right: the selected collection.
            nameBox = new TextBox { Width = 260, Margin = new Padding(6, 3, 0, 3) };
            nameBox.TextChanged += (s, e) => Rename();
            sources = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, AllowDrop = true, Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true, Margin = new Padding(0, 6, 0, 0)
            };
            sources.Columns.Add("Name", 190);
            sources.Columns.Add("Type", 110);
            sources.Columns.Add("Location", 180);
            sources.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && Selected != null ? DragDropEffects.Copy : DragDropEffects.None;
            sources.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            sources.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSources(); };
            sources.SelectedIndexChanged += (s, e) => UpdateButtons();

            var srcButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(8, 6, 0, 0) };
            addFiles = Button("Add files...", (s, e) => AddFilesDialog(), 150);
            addFolder = Button("Add folder...", (s, e) => AddPaths(FolderPicker.Pick(Handle, "Choose folders for this collection")), 150);
            addCurrent = Button("Add current wallpaper", (s, e) => { if (app.CurrentWallpaperPath != null) AddPaths(new[] { app.CurrentWallpaperPath }); }, 150);
            removeButton = Button("Remove", (s, e) => RemoveSources(), 150);
            srcButtons.Controls.AddRange(new Control[] { addFiles, addFolder, addCurrent, removeButton });

            scheduleBox = new CheckBox { Text = "Play by itself every day from", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            scheduleBox.CheckedChanged += (s, e) => ScheduleChanged();
            fromBox = TimeBox();
            toBox = TimeBox();
            fromBox.ValueChanged += (s, e) => ScheduleChanged();
            toBox.ValueChanged += (s, e) => ScheduleChanged();
            var schedule = Line(scheduleBox, fromBox, new Label { Text = "to", AutoSize = true, Margin = new Padding(4, 6, 4, 0) }, toBox);

            summary = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 0) };
            playButton = Button("Play it now", (s, e) => PlayNow(), 150);
            playButton.Margin = new Padding(0, 14, 6, 6);
            srcButtons.Controls.Add(playButton);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Margin = new Padding(0) };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var nameLine = Line(new Label { Text = "Name", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, nameBox);
            right.Controls.Add(nameLine, 0, 0);
            right.SetColumnSpan(nameLine, 2);
            right.Controls.Add(sources, 0, 1);
            right.Controls.Add(srcButtons, 1, 1);
            right.Controls.Add(schedule, 0, 2);
            right.SetColumnSpan(schedule, 2);
            right.Controls.Add(summary, 0, 3);
            right.SetColumnSpan(summary, 2);
            root.Controls.Add(right, 1, 1);

            // Bottom.
            var ok = Button("OK", (s, e) => { Apply(); Close(); }, 88);
            var cancel = Button("Cancel", (s, e) => Close(), 88);
            var apply = Button("Apply", (s, e) => Apply(), 88);
            AcceptButton = ok;
            CancelButton = cancel;
            var bottom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
            bottom.Controls.AddRange(new Control[] { ok, cancel, apply });
            root.Controls.Add(bottom, 1, 2);
        }

        static Button Button(string text, EventHandler click, int width)
        {
            var b = new Button { Text = text, Width = width, Height = 28, Margin = new Padding(0, 0, 6, 6), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        static DateTimePicker TimeBox()
        {
            return new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 70, Margin = new Padding(0, 3, 0, 0) };
        }

        static FlowLayoutPanel Line(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            f.Controls.AddRange(controls);
            return f;
        }

        // ------------------------------------------------------------------ collections

        WallpaperCollection Selected { get { int i = names.SelectedIndex; return i >= 0 && i < work.Count ? work[i] : null; } }

        void RefreshNames(int select)
        {
            loading = true;
            names.BeginUpdate();
            names.Items.Clear();
            foreach (var c in work) names.Items.Add(c.Name + (c.Scheduled ? "   " + c.ScheduleText : ""));
            names.EndUpdate();
            loading = false;
            if (select >= 0 && select < work.Count) names.SelectedIndex = select;
            else LoadSelected();
        }

        void AddCollection()
        {
            work.Add(new WallpaperCollection { Name = UniqueName("New collection", null) });
            RefreshNames(work.Count - 1);
            nameBox.Focus();
            nameBox.SelectAll();
        }

        string UniqueName(string wanted, WallpaperCollection except)
        {
            wanted = (wanted ?? "").Trim();
            if (wanted.Length == 0) wanted = "Collection";
            string name = wanted;
            for (int n = 2; work.Any(c => c != except && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = wanted + " " + n;
            return name;
        }

        void DeleteSelected()
        {
            var c = Selected;
            if (c == null) return;
            if (MessageBox.Show(this, "Delete the collection \"" + c.Name + "\"? The wallpapers themselves are not touched.", "LiveWall",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int i = names.SelectedIndex;
            work.RemoveAt(i);
            RefreshNames(Math.Min(i, work.Count - 1));
        }

        void MoveSelected(int dir)
        {
            int i = names.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= work.Count) return;
            var t = work[i]; work[i] = work[j]; work[j] = t;
            RefreshNames(j);
        }

        void LoadSelected()
        {
            if (loading) return;
            var c = Selected;
            loading = true;
            nameBox.Text = c != null ? c.Name : "";
            scheduleBox.Checked = c != null && c.Scheduled;
            fromBox.Value = DateTime.Today.AddMinutes(c != null ? c.StartMinute : 7 * 60);
            toBox.Value = DateTime.Today.AddMinutes(c != null ? c.EndMinute : 19 * 60);
            loading = false;
            RefreshSources();
            UpdateButtons();
        }

        void Rename()
        {
            var c = Selected;
            if (loading || c == null) return;
            string wanted = nameBox.Text.Trim();
            if (wanted.Length == 0) return;
            c.Name = UniqueName(wanted, c);
            loading = true;
            names.Items[names.SelectedIndex] = c.Name + (c.Scheduled ? "   " + c.ScheduleText : "");
            loading = false;
        }

        void ScheduleChanged()
        {
            var c = Selected;
            if (loading || c == null) return;
            c.Scheduled = scheduleBox.Checked;
            c.StartMinute = fromBox.Value.Hour * 60 + fromBox.Value.Minute;
            c.EndMinute = toBox.Value.Hour * 60 + toBox.Value.Minute;
            loading = true;
            names.Items[names.SelectedIndex] = c.Name + (c.Scheduled ? "   " + c.ScheduleText : "");
            loading = false;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            var c = Selected;
            bool has = c != null;
            foreach (Control x in new Control[] { nameBox, sources, addFiles, addFolder, scheduleBox, playButton, deleteButton }) x.Enabled = has;
            addCurrent.Enabled = has && app.CurrentWallpaperPath != null;
            removeButton.Enabled = has && sources.SelectedIndices.Count > 0;
            fromBox.Enabled = toBox.Enabled = has && scheduleBox.Checked;
            upButton.Enabled = has && names.SelectedIndex > 0;
            downButton.Enabled = has && names.SelectedIndex < work.Count - 1;
            if (!has) { summary.Text = work.Count == 0 ? "No collections yet: click New." : ""; return; }
            int n = Playlist.Resolve(c.Sources).Count;
            string text = n + (n == 1 ? " wallpaper" : " wallpapers");
            if (c.Scheduled)
            {
                text += c.StartMinute == c.EndMinute ? ". Pick two different times."
                    : ". Plays by itself from " + WallpaperCollection.Clock(c.StartMinute) + " to " + WallpaperCollection.Clock(c.EndMinute) +
                      (c.EndMinute < c.StartMinute ? " (the next day)." : ".");
                var overlap = work.FirstOrDefault(o => o != c && o.Scheduled && Overlaps(o, c));
                if (overlap != null) text += " Overlaps \"" + overlap.Name + "\": the one higher in the list wins.";
            }
            summary.Text = text;
        }

        static bool Overlaps(WallpaperCollection a, WallpaperCollection b)
        {
            for (int m = 0; m < 1440; m += 5) if (a.Covers(m) && b.Covers(m)) return true;
            return false;
        }

        // ------------------------------------------------------------------ wallpapers in the collection

        void RefreshSources()
        {
            var c = Selected;
            sources.BeginUpdate();
            sources.Items.Clear();
            if (c != null)
                foreach (string src in c.Sources)
                {
                    var item = new ListViewItem { ToolTipText = src };
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
                    sources.Items.Add(item);
                }
            sources.EndUpdate();
        }

        void AddPaths(IEnumerable<string> paths)
        {
            var c = Selected;
            if (c == null) return;
            bool added = false;
            foreach (string p in paths)
            {
                string full;
                try { full = Path.GetFullPath(p); } catch { continue; }
                MediaKind k;
                if (!Directory.Exists(full) && !(File.Exists(full) && MediaTypes.TryClassify(full, out k))) continue;
                if (c.Sources.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase))) continue;
                c.Sources.Add(full);
                added = true;
            }
            if (added) { RefreshSources(); UpdateButtons(); }
        }

        void AddFilesDialog()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaTypes.DialogFilter, Title = "Add wallpapers to the collection" })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
        }

        void RemoveSources()
        {
            var c = Selected;
            if (c == null) return;
            foreach (int i in sources.SelectedIndices.Cast<int>().OrderByDescending(i => i)) c.Sources.RemoveAt(i);
            RefreshSources();
            UpdateButtons();
        }

        // ------------------------------------------------------------------ apply

        void Apply() { app.ApplyCollections(work); }

        void PlayNow()
        {
            var c = Selected;
            if (c == null) return;
            if (c.Sources.Count == 0) { MessageBox.Show(this, "Add some wallpapers to this collection first.", "LiveWall"); return; }
            Apply();
            app.UseCollection(c.Name);
        }
    }
}
