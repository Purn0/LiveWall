using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LiveWall
{
    // Minimal music settings (phase 3 redesigns the settings UI): default source, this wallpaper's source, music
    // folder, volume, when to go silent, and the optional online mood tagging.
    internal sealed class MusicForm : Form
    {
        static readonly string[] DefaultKinds = { MusicSpec.None, MusicSpec.Random, MusicSpec.Theme, MusicSpec.Video, MusicSpec.Custom };
        static readonly string[] WallpaperKinds = { MusicSpec.Default, MusicSpec.None, MusicSpec.Random, MusicSpec.Theme, MusicSpec.Video, MusicSpec.Custom };

        readonly AppController app;
        readonly MusicSettings baseline;
        readonly string wallpaperKey;
        string defaultCustom, wallpaperCustom;     // chosen files/folders ("custom|...")
        string wallpaperSpecApplied;               // this wallpaper's choice as last applied (null = default)

        ComboBox defaultBox, wallpaperBox;
        TextBox folderBox, keyBox;
        NumericUpDown volumeBox, graceBox, resumeBox;
        CheckBox otherBox, fullscreenBox, batteryBox, saverBox, aiBox;
        Label defaultCustomLabel, wallpaperCustomLabel, moodLabel, nowLabel, keyLabel;
        Button playButton, nextButton;
        HotkeyBox hotkeyBox;

        public MusicForm(AppController app, MusicSettings current)
        {
            this.app = app;
            baseline = current;
            wallpaperKey = app.MusicWallpaperKey;
            wallpaperSpecApplied = current.OverrideFor(wallpaperKey);

            SuspendLayout();
            Text = "LiveWall Music";
            Font = SystemFonts.MessageBoxFont;
            Icon = AppIcon.Load(new Size(32, 32));
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BuildUi();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
            LoadValues();
        }

        void BuildUi()
        {
            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 8, 16, 12), Dock = DockStyle.Fill };
            Controls.Add(root);

            defaultBox = Combo(DefaultKinds.Select(MusicSpec.Describe));
            defaultBox.SelectedIndexChanged += (s, e) => { if (Kind(defaultBox, DefaultKinds) == MusicSpec.Custom && defaultCustom == null) PickCustom(true, false); UpdateLabels(); };
            defaultCustomLabel = Note("");
            var defaultFiles = Small("Files...", (s, e) => PickCustom(true, false));
            var defaultFolder = Small("Folder...", (s, e) => PickCustom(true, true));

            wallpaperBox = Combo(WallpaperKinds.Select(MusicSpec.Describe));
            wallpaperBox.SelectedIndexChanged += (s, e) => { if (Kind(wallpaperBox, WallpaperKinds) == MusicSpec.Custom && wallpaperCustom == null) PickCustom(false, false); UpdateLabels(); };
            wallpaperCustomLabel = Note("");
            var wallpaperFiles = Small("Files...", (s, e) => PickCustom(false, false));
            var wallpaperFolder = Small("Folder...", (s, e) => PickCustom(false, true));
            moodLabel = Note("");

            folderBox = new TextBox { Width = 330, Margin = new Padding(6, 3, 6, 3) };
            var browse = Small("Browse...", (s, e) =>
            {
                var f = FolderPicker.Pick(Handle, "Music folder");
                if (f.Count > 0) folderBox.Text = f[0];
            });

            volumeBox = Number(0, 100);
            otherBox = Check("Silence while other apps play sound (resumes when they are quiet)");
            fullscreenBox = Check("Silence while a fullscreen or maximized app is in front (not File Explorer, Settings or the desktop)");
            batteryBox = Check("Silence while running on battery");
            saverBox = Check("Silence while Energy Saver / Battery Saver is on");
            graceBox = Number(1, 600);
            resumeBox = Number(1, 600);

            aiBox = Check("Ask AI (Claude) for the mood of each wallpaper");
            keyBox = new TextBox { Width = 300, UseSystemPasswordChar = true, Margin = new Padding(6, 3, 6, 3) };
            keyLabel = Note("");
            var forget = Small("Forget key", (s, e) => { baseline.AiKey = ""; keyBox.Text = ""; UpdateLabels(); });

            nowLabel = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(360, 0), Margin = new Padding(0, 7, 8, 3) };
            playButton = Small("Pause", (s, e) => { app.ToggleMusicMute(); RefreshNowPlaying(); });
            nextButton = Small("Next song", (s, e) => { app.NextTrack(); RefreshNowPlaying(); });
            hotkeyBox = new HotkeyBox { Margin = new Padding(6, 3, 6, 3) };
            // While the shortcut box has focus, the current shortcuts must not fire (so they can be typed in).
            hotkeyBox.Enter += (s, e) => app.SuspendHotkeys();
            hotkeyBox.Leave += (s, e) => app.ResumeHotkeys();
            FormClosed += (s, e) => app.ResumeHotkeys();
            var ok = Small("OK", (s, e) => { Apply(); Close(); });
            var cancel = Small("Cancel", (s, e) => Close());
            var apply = Small("Apply", (s, e) => Apply());
            AcceptButton = ok;
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.AddRange(new Control[] { ok, cancel, apply });

            string name = app.MusicWallpaperName;
            Add(root, Header("Now playing", true));
            Add(root, Line(nowLabel, playButton, nextButton));
            Add(root, Header("Sources", false));
            Add(root, Line(Label("All wallpapers"), defaultBox, defaultFiles, defaultFolder));
            Add(root, defaultCustomLabel);
            Add(root, Line(Label(name == null ? "This wallpaper" : "This wallpaper (" + Shorten(name, 34) + ")"), wallpaperBox, wallpaperFiles, wallpaperFolder));
            Add(root, wallpaperCustomLabel);
            Add(root, moodLabel);
            if (name == null) { wallpaperBox.Enabled = wallpaperFiles.Enabled = wallpaperFolder.Enabled = false; }
            Add(root, Line(Label("Music folder"), folderBox, browse));
            Add(root, Note("\"Random\" shuffles this folder. \"By theme\" plays its subfolder named after the wallpaper's mood - calm, " +
                           "energetic, dark, happy, dreamy or cozy - or the whole folder if there is none. \"The video's own sound\" plays the " +
                           "video wallpaper's soundtrack (the video itself stays muted)."));
            Add(root, Note("With 3 or more wallpapers in a slideshow, each wallpaper gets its own song, repeated (changing every 15 minutes " +
                           "or less), or two songs taking turns (longer). With one or two wallpapers, or on a board, the songs just play on."));
            Add(root, Header("Playback", false));
            Add(root, Line(Label("Volume (%)"), volumeBox, Label("   Pause / play shortcut"), hotkeyBox));
            Add(root, otherBox);
            Add(root, fullscreenBox);
            Add(root, batteryBox);
            Add(root, saverBox);
            Add(root, Line(Label("Close the music player after"), graceBox, Label("s of silence;  resume after"), resumeBox, Label("s of quiet")));
            Add(root, Note("Always silent while the PC is locked, the screen is off or asleep. Silencing fades out, pauses and then closes " +
                           "the player completely, so the audio device can sleep; the music comes back from the same place."));
            Add(root, Header("Mood by AI (optional)", false));
            Add(root, aiBox);
            Add(root, Line(Label("API key"), keyBox, forget));
            Add(root, keyLabel);
            Add(root, Note("Sends each wallpaper's picture (small JPEG) once to api.anthropic.com using your key; the answer is kept. " +
                           "Without it, the mood comes from the picture's colors on this PC."));
            Add(root, buttons);
        }

        void LoadValues()
        {
            var m = baseline;
            string dk = MusicSpec.Kind(m.Default);
            if (dk == MusicSpec.Custom) defaultCustom = m.Default;
            defaultBox.SelectedIndex = Math.Max(0, Array.IndexOf(DefaultKinds, dk));
            string wk = wallpaperSpecApplied == null ? MusicSpec.Default : MusicSpec.Kind(wallpaperSpecApplied);
            if (wk == MusicSpec.Custom) wallpaperCustom = wallpaperSpecApplied;
            wallpaperBox.SelectedIndex = Math.Max(0, Array.IndexOf(WallpaperKinds, wk));
            folderBox.Text = m.Folder;
            try { folderBox.SetCueBanner(m.EffectiveFolder); } catch { }
            volumeBox.Value = Math.Max(0, Math.Min(100, m.Volume));
            otherBox.Checked = m.SilenceForOtherAudio;
            fullscreenBox.Checked = m.PauseOnFullscreen;
            batteryBox.Checked = m.PauseOnBattery;
            saverBox.Checked = m.PauseOnEnergySaver;
            graceBox.Value = Math.Max(1, Math.Min(600, m.GraceSeconds));
            resumeBox.Value = Math.Max(1, Math.Min(600, m.ResumeSeconds));
            aiBox.Checked = m.AskAi;
            string mood = app.CurrentWallpaperMood;
            moodLabel.Text = mood == null ? "" : "Mood of this wallpaper: " + mood.Replace(" ai", " (by AI)");
            hotkeyBox.Text = m.Hotkey;
            RefreshNowPlaying();
            UpdateLabels();
        }

        // Also called by the app when the song or the state changes.
        public void RefreshNowPlaying()
        {
            string title = app.MusicTrackTitle;
            string text = app.MusicPlaying ? "\u266A  " + title : app.MusicStatus;
            if (nowLabel.Text != text) nowLabel.Text = text;
            playButton.Text = app.MusicMuted ? "Play" : "Pause";
            nextButton.Enabled = app.CanSkipTrack;
        }

        void UpdateLabels()
        {
            defaultCustomLabel.Text = Kind(defaultBox, DefaultKinds) == MusicSpec.Custom && defaultCustom != null ? Paths(defaultCustom) : "";
            wallpaperCustomLabel.Text = Kind(wallpaperBox, WallpaperKinds) == MusicSpec.Custom && wallpaperCustom != null ? Paths(wallpaperCustom) : "";
            keyLabel.Text = baseline.AiKey.Length > 0 ? "A key is stored (encrypted for your Windows account). Type a new one to replace it." : "No key stored.";
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
            if (chosen.Count > 0)
            {
                string spec = MusicSpec.MakeCustom(chosen);
                if (forDefault) { defaultCustom = spec; defaultBox.SelectedIndex = Array.IndexOf(DefaultKinds, MusicSpec.Custom); }
                else { wallpaperCustom = spec; wallpaperBox.SelectedIndex = Array.IndexOf(WallpaperKinds, MusicSpec.Custom); }
            }
            else
            {
                // Nothing chosen: don't leave "Custom" selected without files.
                if (forDefault && defaultCustom == null && Kind(defaultBox, DefaultKinds) == MusicSpec.Custom) defaultBox.SelectedIndex = 0;
                if (!forDefault && wallpaperCustom == null && Kind(wallpaperBox, WallpaperKinds) == MusicSpec.Custom) wallpaperBox.SelectedIndex = 0;
            }
            UpdateLabels();
        }

        void Apply()
        {
            var m = baseline.Clone();
            string dk = Kind(defaultBox, DefaultKinds);
            m.Default = dk == MusicSpec.Custom ? (defaultCustom ?? MusicSpec.None) : dk;
            m.Folder = folderBox.Text.Trim();
            m.Volume = (int)volumeBox.Value;
            m.SilenceForOtherAudio = otherBox.Checked;
            m.PauseOnFullscreen = fullscreenBox.Checked;
            m.PauseOnBattery = batteryBox.Checked;
            m.PauseOnEnergySaver = saverBox.Checked;
            m.GraceSeconds = (int)graceBox.Value;
            m.ResumeSeconds = (int)resumeBox.Value;
            m.AskAi = aiBox.Checked;
            if (keyBox.Text.Trim().Length > 0) { m.AiKey = Dpapi.Protect(keyBox.Text.Trim()); baseline.AiKey = m.AiKey; keyBox.Text = ""; }
            m.Hotkey = hotkeyBox.Text.Trim();
            foreach (string other in new[] { app.BoardHotkey, app.DrawHotkey, app.CollectionHotkey })
                if (m.Hotkey.Length > 0 && string.Equals(m.Hotkey, other, StringComparison.OrdinalIgnoreCase)) { m.Hotkey = ""; hotkeyBox.Text = ""; }

            string wk = Kind(wallpaperBox, WallpaperKinds);
            string spec = wk == MusicSpec.Default ? null : wk == MusicSpec.Custom ? wallpaperCustom : wk;
            bool changed = wallpaperBox.Enabled && !string.Equals(spec, wallpaperSpecApplied, StringComparison.Ordinal);
            app.ApplyMusicSettings(m, wallpaperKey, changed, spec);
            if (changed) wallpaperSpecApplied = spec;
            UpdateLabels();
            RefreshNowPlaying();
        }

        // ------------------------------------------------------------------ small layout helpers

        static string Kind(ComboBox box, string[] kinds) { return box.SelectedIndex >= 0 && box.SelectedIndex < kinds.Length ? kinds[box.SelectedIndex] : kinds[0]; }

        static void Add(TableLayoutPanel root, Control c)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c, 0, root.RowStyles.Count - 1);
        }

        Label Header(string text, bool first)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold), Margin = new Padding(0, first ? 2 : 14, 0, 4) };
        }

        static Label Label(string text) { return new Label { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 7, 0, 3) }; }
        static Label Note(string text) { return new Label { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 2) }; }
        static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 0, 2) }; }
        static NumericUpDown Number(int min, int max) { return new NumericUpDown { Minimum = min, Maximum = max, Width = 60, Margin = new Padding(6, 3, 6, 3) }; }

        static ComboBox Combo(IEnumerable<string> items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(6, 3, 6, 3) };
            foreach (string s in items) c.Items.Add(s);
            return c;
        }

        static Button Small(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 26), Margin = new Padding(0, 2, 6, 2), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        static FlowLayoutPanel Line(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            f.Controls.AddRange(controls);
            return f;
        }

        static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 3) + "..."; }
    }

    internal static class CueBanner
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        // Grey hint text shown while a text box is empty.
        public static void SetCueBanner(this TextBox box, string text) { SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), text); }
    }
}
