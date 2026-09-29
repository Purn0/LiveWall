using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LiveWall
{
    // Music for one or more wallpapers (Settings > Wallpapers > Music...): the default, none, random, by theme, the
    // video's own sound, or chosen files / a folder. Spec is the result ("custom|..." etc.; null = the default).
    internal sealed class WallpaperMusicDialog : Form
    {
        readonly RadioButton defaultRadio, noneRadio, randomRadio, themeRadio, videoRadio, customRadio;
        readonly Label customLabel;
        string custom;

        public string Spec { get; private set; }

        public WallpaperMusicDialog(string title, string current, string defaultSpec, bool anyVideo)
        {
            SuspendLayout();
            Text = "Music";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 12, 16, 12), Dock = DockStyle.Fill };
            Controls.Add(root);
            root.Controls.Add(new Label { Text = title, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(420, 0),
                                          Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });

            // All radio buttons share one parent (so they exclude each other); extra controls sit in the second column.
            var choices = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            defaultRadio = Radio("Same as the default (" + MusicSpec.Describe(defaultSpec) + ")");
            noneRadio = Radio("None");
            randomRadio = Radio("Random from the music folder");
            themeRadio = Radio("By theme (mood of the picture)");
            videoRadio = Radio("The video's own sound" + (anyVideo ? "" : " (videos only)"));
            videoRadio.Enabled = anyVideo;
            customRadio = Radio("Custom:");
            var files = Button("Files...", (s, e) => Pick(false));
            var folder = Button("Folder...", (s, e) => Pick(true));
            customLabel = new Label { AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0), Margin = new Padding(20, 0, 0, 4) };
            foreach (var r in new[] { defaultRadio, noneRadio, randomRadio, themeRadio, videoRadio })
            {
                choices.Controls.Add(r, 0, choices.RowCount);
                choices.SetColumnSpan(r, 2);
                choices.RowCount++;
            }
            choices.Controls.Add(customRadio, 0, choices.RowCount);
            var pickers = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            pickers.Controls.AddRange(new Control[] { files, folder });
            choices.Controls.Add(pickers, 1, choices.RowCount);
            choices.RowCount++;
            choices.Controls.Add(customLabel, 0, choices.RowCount);
            choices.SetColumnSpan(customLabel, 2);
            root.Controls.Add(choices);

            var ok = Button("OK", (s, e) => Accept());
            var cancel = Button("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            AcceptButton = ok;
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.AddRange(new Control[] { ok, cancel });
            root.Controls.Add(buttons);

            string kind = current == null ? MusicSpec.Default : MusicSpec.Kind(current);
            if (kind == MusicSpec.Custom) custom = current;
            RadioFor(kind).Checked = true;
            UpdateCustomLabel();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
        }

        RadioButton RadioFor(string kind)
        {
            switch (kind)
            {
                case MusicSpec.None: return noneRadio;
                case MusicSpec.Random: return randomRadio;
                case MusicSpec.Theme: return themeRadio;
                case MusicSpec.Video: return videoRadio.Enabled ? videoRadio : defaultRadio;
                case MusicSpec.Custom: return customRadio;
                default: return defaultRadio;
            }
        }

        void Pick(bool folder)
        {
            List<string> chosen;
            if (folder) chosen = FolderPicker.Pick(Handle, "Music folder");
            else
            {
                chosen = new List<string>();
                using (var dlg = new OpenFileDialog { Multiselect = true, Filter = MusicLibrary.DialogFilter, Title = "Choose music" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) chosen.AddRange(dlg.FileNames);
            }
            if (chosen.Count == 0) return;
            custom = MusicSpec.MakeCustom(chosen);
            customRadio.Checked = true;
            UpdateCustomLabel();
        }

        void UpdateCustomLabel()
        {
            var p = MusicSpec.CustomPaths(custom);
            customLabel.Text = p.Count == 0 ? "(choose files or a folder)"
                : string.Join("; ", p.Take(3).Select(x => Path.GetFileName(x.TrimEnd('\\')))) + (p.Count > 3 ? " (+" + (p.Count - 3) + ")" : "");
        }

        void Accept()
        {
            if (customRadio.Checked && custom == null) { Pick(false); if (custom == null) return; }
            Spec = defaultRadio.Checked ? null : noneRadio.Checked ? MusicSpec.None : randomRadio.Checked ? MusicSpec.Random
                 : themeRadio.Checked ? MusicSpec.Theme : videoRadio.Checked ? MusicSpec.Video : custom;
            DialogResult = DialogResult.OK;
            Close();
        }

        static RadioButton Radio(string text) { return new RadioButton { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 3, 12, 3) }; }

        static Button Button(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 26), Margin = new Padding(0, 0, 6, 0), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }
    }
}
