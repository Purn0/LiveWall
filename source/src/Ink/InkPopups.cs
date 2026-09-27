using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    // Dark, borderless popups of the drawing editor: the color picker and the text panel.
    internal class InkPopup : Form
    {
        protected static readonly Color Back = Color.FromArgb(32, 33, 36), Field = Color.FromArgb(48, 49, 53),
            Line = Color.FromArgb(80, 84, 90), Accent = Color.FromArgb(26, 115, 232), Dim = Color.FromArgb(200, 255, 255, 255);
        protected readonly float S;
        protected readonly Font UiFont, SmallFont;

        protected InkPopup(float scale)
        {
            S = scale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Back;
            ForeColor = Color.White;
            DoubleBuffered = true;
            UiFont = new Font("Segoe UI", 13 * S, GraphicsUnit.Pixel);
            SmallFont = new Font("Segoe UI", 12 * S, GraphicsUnit.Pixel);
            Font = UiFont;
        }

        protected int P(float v) { return (int)Math.Round(v * S); }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Keeps the popup on `monitor`, next to `near` (screen coordinates).
        public void PlaceNear(Rectangle near, Rectangle monitor)
        {
            int x = near.Right + P(12), y = near.Top;
            if (x + Width > monitor.Right - P(8)) x = near.Left - Width - P(12);
            if (x < monitor.Left + P(8)) x = Math.Max(monitor.Left + P(8), Math.Min(monitor.Right - Width - P(8), near.Left));
            if (y + Height > monitor.Bottom - P(8)) y = monitor.Bottom - Height - P(8);
            Location = new Point(x, Math.Max(monitor.Top + P(8), y));
        }

        // Under `anchor` (a toolbar button), kept on `monitor`.
        public void PlaceBelow(Rectangle anchor, Rectangle monitor)
        {
            int x = Math.Max(monitor.Left + P(8), Math.Min(monitor.Right - Width - P(8), anchor.Left));
            int y = anchor.Bottom + P(10);
            if (y + Height > monitor.Bottom - P(8)) y = Math.Max(monitor.Top + P(8), anchor.Top - Height - P(10));
            Location = new Point(x, y);
        }

        protected Label MakeLabel(string text, int x, int y, int w = 0)
        {
            var l = new Label { Text = text, Location = new Point(x, y), AutoSize = w == 0, ForeColor = Dim, BackColor = Back, Font = SmallFont };
            if (w > 0) l.Size = new Size(w, P(20));
            Controls.Add(l);
            return l;
        }

        protected TextBox MakeBox(int x, int y, int w)
        {
            var t = new TextBox { Location = new Point(x, y), Width = w, BackColor = Field, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = UiFont };
            Controls.Add(t);
            return t;
        }

        protected Button MakeButton(string text, int x, int y, int w, int h, bool accent = false)
        {
            var b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                BackColor = accent ? Accent : Field, Font = UiFont, UseVisualStyleBackColor = false, TabStop = false
            };
            b.FlatAppearance.BorderColor = accent ? Accent : Line;
            b.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(40, 130, 245) : Color.FromArgb(62, 64, 69);
            Controls.Add(b);
            return b;
        }

        protected CheckBox MakeToggle(string text, int x, int y, int w, int h, Font font = null)
        {
            var c = new CheckBox
            {
                Text = text, Appearance = Appearance.Button, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White, BackColor = Field, Font = font ?? UiFont, TextAlign = ContentAlignment.MiddleCenter, TabStop = false
            };
            c.FlatAppearance.BorderColor = Line;
            c.FlatAppearance.CheckedBackColor = Accent;
            c.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 64, 69);
            Controls.Add(c);
            return c;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Line)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { UiFont.Dispose(); SmallFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Any color: hue/saturation/value square, RGB, HSV and hex fields, basic and recent colors, and an eyedropper.
    internal sealed class ColorPicker : InkPopup
    {
        public event Action<int> ColorChanged;
        public event Action EyedropperRequested;

        static readonly int[] Basic =
        {
            unchecked((int)0xFF000000), unchecked((int)0xFF5F6368), unchecked((int)0xFFBDC1C6), unchecked((int)0xFFFFFFFF),
            unchecked((int)0xFF8B4513), unchecked((int)0xFFFF7043), unchecked((int)0xFFFFEB3B), unchecked((int)0xFFCDDC39),
            unchecked((int)0xFF00BCD4), unchecked((int)0xFF3F51B5), unchecked((int)0xFFE91E63), unchecked((int)0xFFB39DDB),
        };

        readonly Rectangle svRect, hueRect, newRect, oldRect;
        readonly List<KeyValuePair<Rectangle, int>> swatches = new List<KeyValuePair<Rectangle, int>>();
        readonly TextBox hex, r, g, b, h, s, v;
        readonly List<int> recent;
        Bitmap svBitmap;
        float hue, sat, val;
        int original, current;
        bool updating, dragSV, dragHue;

        public ColorPicker(float scale, List<int> recentColors) : base(scale)
        {
            recent = recentColors;
            ClientSize = new Size(P(300), P(412));
            svRect = new Rectangle(P(12), P(12), P(276), P(160));
            hueRect = new Rectangle(P(12), P(182), P(276), P(16));
            newRect = new Rectangle(P(12), P(210), P(34), P(28));
            oldRect = new Rectangle(P(46), P(210), P(34), P(28));
            MakeLabel("Hex", P(92), P(215));
            hex = MakeBox(P(124), P(211), P(96));
            var drop = MakeButton("\uEF3C", P(250), P(208), P(38), P(32));
            drop.Font = new Font(FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 16 * S, GraphicsUnit.Pixel);
            drop.Click += (o, e) => { var h2 = EyedropperRequested; if (h2 != null) h2(); };
            new ToolTip().SetToolTip(drop, "Pick a color from the screen");

            MakeLabel("R", P(12), P(254)); r = MakeBox(P(28), P(250), P(52));
            MakeLabel("G", P(110), P(254)); g = MakeBox(P(126), P(250), P(52));
            MakeLabel("B", P(208), P(254)); b = MakeBox(P(224), P(250), P(52));
            MakeLabel("H", P(12), P(288)); h = MakeBox(P(28), P(284), P(52));
            MakeLabel("S", P(110), P(288)); s = MakeBox(P(126), P(284), P(52));
            MakeLabel("V", P(208), P(288)); v = MakeBox(P(224), P(284), P(52));
            foreach (var t in new[] { r, g, b }) t.TextChanged += (o, e) => FromRgbBoxes();
            foreach (var t in new[] { h, s, v }) t.TextChanged += (o, e) => FromHsvBoxes();
            hex.TextChanged += (o, e) => FromHex();

            int x = P(12), y = P(322), cell = P(23);
            foreach (Color c in InkRenderer.Palette) { swatches.Add(new KeyValuePair<Rectangle, int>(new Rectangle(x, y, cell - P(3), cell - P(3)), c.ToArgb())); x += cell; }
            foreach (int c in Basic.Take(3)) { swatches.Add(new KeyValuePair<Rectangle, int>(new Rectangle(x, y, cell - P(3), cell - P(3)), c)); x += cell; }
            x = P(12); y += cell;
            foreach (int c in Basic.Skip(3)) { swatches.Add(new KeyValuePair<Rectangle, int>(new Rectangle(x, y, cell - P(3), cell - P(3)), c)); x += cell; }
            MakeLabel("Recent", P(12), P(372));
        }

        public void SetColor(int argb)
        {
            original = argb | unchecked((int)0xFF000000);
            SetCurrent(original, true, true);
        }

        public int Current { get { return current; } }

        void SetCurrent(int argb, bool updateHsv, bool updateBoxes, TextBox except = null)
        {
            current = argb | unchecked((int)0xFF000000);
            if (updateHsv) { Color c = Color.FromArgb(current); ToHsv(c, out hue, out sat, out val); RebuildSV(); }
            if (updateBoxes) SyncBoxes(except);
            Invalidate();
            var ev = ColorChanged;
            if (ev != null) ev(current);
        }

        void SyncBoxes(TextBox except)
        {
            updating = true;
            Color c = Color.FromArgb(current);
            if (except != hex) hex.Text = "#" + (current & 0xFFFFFF).ToString("X6");
            if (except != r) r.Text = c.R.ToString(); if (except != g) g.Text = c.G.ToString(); if (except != b) b.Text = c.B.ToString();
            if (except != h) h.Text = ((int)Math.Round(hue)).ToString(); if (except != s) s.Text = ((int)Math.Round(sat * 100)).ToString();
            if (except != v) v.Text = ((int)Math.Round(val * 100)).ToString();
            updating = false;
        }

        void FromRgbBoxes()
        {
            if (updating) return;
            int rr, gg, bb;
            if (!int.TryParse(r.Text, out rr) || !int.TryParse(g.Text, out gg) || !int.TryParse(b.Text, out bb)) return;
            if (rr < 0 || rr > 255 || gg < 0 || gg > 255 || bb < 0 || bb > 255) return;
            SetCurrent(Color.FromArgb(rr, gg, bb).ToArgb(), true, true, ActiveControl as TextBox);
        }

        void FromHsvBoxes()
        {
            if (updating) return;
            int hh, ss, vv;
            if (!int.TryParse(h.Text, out hh) || !int.TryParse(s.Text, out ss) || !int.TryParse(v.Text, out vv)) return;
            if (hh < 0 || hh > 360 || ss < 0 || ss > 100 || vv < 0 || vv > 100) return;
            hue = hh; sat = ss / 100f; val = vv / 100f;
            RebuildSV();
            SetCurrent(FromHsv(hue, sat, val), false, true, ActiveControl as TextBox);
        }

        void FromHex()
        {
            if (updating) return;
            string t = hex.Text.Trim().TrimStart('#');
            int rgb;
            if (t.Length == 3) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
            if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return;
            SetCurrent(rgb, true, true, hex);
        }

        // ------------------------------------------------------------------ painting

        void RebuildSV()
        {
            if (svBitmap == null) svBitmap = new Bitmap(svRect.Width, svRect.Height, PixelFormat.Format32bppArgb);
            var data = svBitmap.LockBits(new Rectangle(0, 0, svRect.Width, svRect.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[svRect.Width];
                for (int y = 0; y < svRect.Height; y++)
                {
                    float vv = 1 - y / (float)(svRect.Height - 1);
                    for (int x = 0; x < svRect.Width; x++) row[x] = FromHsv(hue, x / (float)(svRect.Width - 1), vv);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
            finally { svBitmap.UnlockBits(data); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var gr = e.Graphics;
            if (svBitmap != null) gr.DrawImageUnscaled(svBitmap, svRect.Location);
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            float kx = svRect.Left + sat * (svRect.Width - 1), ky = svRect.Top + (1 - val) * (svRect.Height - 1), k = P(12);
            using (var p = new Pen(Color.White, P(2))) gr.DrawEllipse(p, kx - k / 2, ky - k / 2, k, k);
            using (var p = new Pen(Color.Black, 1)) gr.DrawEllipse(p, kx - k / 2 - 1, ky - k / 2 - 1, k + 2, k + 2);

            using (var lg = new LinearGradientBrush(hueRect, Color.Red, Color.Red, 0f))
            {
                var blend = new ColorBlend(7);
                blend.Colors = new[] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red };
                blend.Positions = new[] { 0f, 1 / 6f, 2 / 6f, 3 / 6f, 4 / 6f, 5 / 6f, 1f };
                lg.InterpolationColors = blend;
                gr.FillRectangle(lg, hueRect);
            }
            float hx = hueRect.Left + hue / 360f * hueRect.Width;
            using (var p = new Pen(Color.White, P(3))) gr.DrawRectangle(p, hx - P(3), hueRect.Top - P(2), P(6), hueRect.Height + P(4));

            using (var bn = new SolidBrush(Color.FromArgb(current))) gr.FillRectangle(bn, newRect);
            using (var bo = new SolidBrush(Color.FromArgb(original))) gr.FillRectangle(bo, oldRect);
            foreach (var sw in swatches) Swatch(gr, sw.Key, sw.Value);
            int x = P(66);
            foreach (int c in recent.Take(9)) { Swatch(gr, new Rectangle(x, P(372), P(20), P(20)), c); x += P(23); }
        }

        void Swatch(Graphics gr, Rectangle rc, int argb)
        {
            using (var br = new SolidBrush(Color.FromArgb(argb))) gr.FillEllipse(br, rc);
            using (var p = new Pen((argb | unchecked((int)0xFF000000)) == current ? Color.White : Color.FromArgb(90, 255, 255, 255), (argb | unchecked((int)0xFF000000)) == current ? P(2) : 1))
                gr.DrawEllipse(p, rc);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (svRect.Contains(e.Location)) { dragSV = true; Capture = true; PickSV(e.Location); return; }
            if (Rectangle.Inflate(hueRect, 0, P(4)).Contains(e.Location)) { dragHue = true; Capture = true; PickHue(e.X); return; }
            if (oldRect.Contains(e.Location)) { SetCurrent(original, true, true); return; }
            foreach (var sw in swatches) if (sw.Key.Contains(e.Location)) { SetCurrent(sw.Value, true, true); return; }
            int x = P(66);
            foreach (int c in recent.Take(9)) { if (new Rectangle(x, P(372), P(20), P(20)).Contains(e.Location)) { SetCurrent(c, true, true); return; } x += P(23); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragSV) PickSV(e.Location);
            else if (dragHue) PickHue(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragSV = dragHue = false;
            Capture = false;
        }

        void PickSV(Point p)
        {
            sat = Math.Max(0, Math.Min(1, (p.X - svRect.Left) / (float)(svRect.Width - 1)));
            val = Math.Max(0, Math.Min(1, 1 - (p.Y - svRect.Top) / (float)(svRect.Height - 1)));
            SetCurrent(FromHsv(hue, sat, val), false, true);
        }

        void PickHue(int x)
        {
            hue = Math.Max(0, Math.Min(360, (x - hueRect.Left) / (float)hueRect.Width * 360));
            RebuildSV();
            SetCurrent(FromHsv(hue, sat, val), false, true);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { SetCurrent(original, true, true); Hide(); return true; }
            if (keyData == Keys.Enter) { Hide(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible) Hide();
        }

        // ------------------------------------------------------------------ color models

        public static void ToHsv(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        public static int FromHsv(float h, float s, float v)
        {
            h = ((h % 360) + 360) % 360;
            float c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            float r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return Color.FromArgb(255, (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255)).ToArgb();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && svBitmap != null) { svBitmap.Dispose(); svBitmap = null; }
            base.Dispose(disposing);
        }
    }

    // Text, emoji, kaomoji and symbols: what to write and how it looks. The editor shows it live on the drawing.
    internal sealed class TextPanel : InkPopup
    {
        public event Action Changed, Commit, Cancel, ColorRequested;

        static readonly string[] FontChoices =
        {
            "Segoe UI", "Segoe Print", "Segoe Script", "Ink Free", "Comic Sans MS", "Arial", "Georgia", "Times New Roman",
            "Consolas", "Cascadia Code", "Bahnschrift", "Impact", "Gabriola", "Lucida Handwriting"
        };

        readonly TextBox box;
        readonly ComboBox font;
        readonly CheckBox bold, italic, plain, outline, boxed;
        readonly CheckBox[] tabs;
        readonly Panel gridHost;
        readonly SymbolGrid grid;
        readonly Label sizeLabel;
        readonly Button colorButton;
        readonly DarkSlider size;
        readonly float minSize, maxSize;
        int colorArgb;
        bool loading;

        public TextPanel(float scale, float minSize, float maxSize) : base(scale)
        {
            this.minSize = minSize;
            this.maxSize = maxSize;
            ClientSize = new Size(P(432), P(478));
            MakeLabel("Text  (Enter = new line, Ctrl+Enter = add, Esc = cancel)", P(12), P(10));
            box = new TextBox
            {
                Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Location = new Point(P(12), P(32)),
                Size = new Size(P(408), P(74)), BackColor = Field, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 16 * S, GraphicsUnit.Pixel)
            };
            box.TextChanged += (o, e) => Raise(Changed);
            Controls.Add(box);

            font = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(P(12), P(116)), Width = P(250), FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Color.White, Font = UiFont };
            var installed = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (string f in FontChoices) if (installed.Contains(f)) font.Items.Add(f);
            font.SelectedIndexChanged += (o, e) => Raise(Changed);
            Controls.Add(font);
            bold = MakeToggle("B", P(272), P(114), P(34), P(28), new Font("Segoe UI", 14 * S, FontStyle.Bold, GraphicsUnit.Pixel));
            italic = MakeToggle("I", P(310), P(114), P(34), P(28), new Font("Georgia", 14 * S, FontStyle.Italic, GraphicsUnit.Pixel));
            bold.CheckedChanged += (o, e) => Raise(Changed);
            italic.CheckedChanged += (o, e) => Raise(Changed);
            colorButton = MakeButton("", P(354), P(114), P(66), P(28));
            colorButton.Paint += (o, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                InkToolbar.DrawColorWheelIcon(e.Graphics, new RectangleF(colorButton.Width / 2f - P(10), colorButton.Height / 2f - P(10), P(20), P(20)), Color.FromArgb(colorArgb));
            };
            colorButton.Click += (o, e) => Raise(ColorRequested);
            new ToolTip().SetToolTip(colorButton, "Color");

            MakeLabel("Size", P(12), P(158));
            size = new DarkSlider { Location = new Point(P(52), P(152)), Size = new Size(P(300), P(30)) };
            size.ValueChanged += () => { UpdateSizeLabel(); Raise(Changed); };
            Controls.Add(size);
            sizeLabel = MakeLabel("", P(360), P(158), P(60));

            MakeLabel("Style", P(12), P(198));
            plain = MakeToggle("Plain", P(52), P(192), P(90), P(30));
            outline = MakeToggle("Outline", P(146), P(192), P(90), P(30));
            boxed = MakeToggle("Box", P(240), P(192), P(90), P(30));
            foreach (var c in new[] { plain, outline, boxed })
            {
                var me = c;
                me.Click += (o, e) => { SetEffect(me == outline ? InkText.Outline : me == boxed ? InkText.Box : InkText.Plain); Raise(Changed); };
            }

            tabs = new[] { MakeToggle("Emoji", P(12), P(236), P(90), P(28)), MakeToggle("Kaomoji", P(106), P(236), P(90), P(28)),
                           MakeToggle("Symbols", P(200), P(236), P(90), P(28)) };
            for (int i = 0; i < tabs.Length; i++) { int idx = i; tabs[i].Click += (o, e) => ShowTab(idx); }
            var more = MakeButton("More (Win+.)", P(294), P(236), P(126), P(28));
            more.Click += (o, e) => OpenWindowsEmojiPanel();
            new ToolTip().SetToolTip(more, "Windows' own emoji, kaomoji and symbol panel");

            gridHost = new Panel { Location = new Point(P(12), P(270)), Size = new Size(P(408), P(158)), AutoScroll = true, BackColor = Field };
            grid = new SymbolGrid(S) { Location = Point.Empty, Width = P(408) - SystemInformation.VerticalScrollBarWidth };
            grid.Picked += Insert;
            gridHost.Controls.Add(grid);
            Controls.Add(gridHost);

            var add = MakeButton("Add", P(236), P(438), P(90), P(30), true);
            add.Click += (o, e) => Raise(Commit);
            var cancel = MakeButton("Cancel", P(330), P(438), P(90), P(30));
            cancel.Click += (o, e) => Raise(Cancel);
        }

        // ------------------------------------------------------------------ values

        public string Value { get { return box.Text; } }
        public string FontName { get { return font.SelectedItem as string ?? "Segoe UI"; } }
        public bool IsBold { get { return bold.Checked; } }
        public bool IsItalic { get { return italic.Checked; } }
        public string Effect { get { return outline.Checked ? InkText.Outline : boxed.Checked ? InkText.Box : InkText.Plain; } }
        public float SizeValue { get { return (float)(minSize * Math.Pow(maxSize / minSize, size.Value)); } }
        public int Tab { get { return Array.FindIndex(tabs, t => t.Checked); } }

        public void Reset(string text, string fontName, float sizeValue, bool isBold, bool isItalic, string effect, int argb, int tab)
        {
            loading = true;
            box.Text = text ?? "";
            box.SelectionStart = box.Text.Length;
            int fi = font.Items.IndexOf(fontName);
            font.SelectedIndex = fi >= 0 ? fi : (font.Items.Count > 0 ? 0 : -1);
            bold.Checked = isBold;
            italic.Checked = isItalic;
            SetEffect(effect);
            SetSize(sizeValue);
            colorArgb = argb;
            colorButton.Invalidate();
            loading = false;
            ShowTab(tab);
        }

        public void SetSize(float value)
        {
            bool was = loading;
            loading = true;
            size.Value = (float)(Math.Log(Math.Max(minSize, Math.Min(maxSize, value)) / minSize) / Math.Log(maxSize / minSize));
            UpdateSizeLabel();
            loading = was;
        }

        public void SetColor(int argb) { colorArgb = argb; colorButton.Invalidate(); }

        public void FocusText() { box.Focus(); }

        void UpdateSizeLabel() { sizeLabel.Text = ((int)Math.Round(SizeValue)).ToString() + " px"; }

        void SetEffect(string fx)
        {
            plain.Checked = fx != InkText.Outline && fx != InkText.Box;
            outline.Checked = fx == InkText.Outline;
            boxed.Checked = fx == InkText.Box;
        }

        void ShowTab(int i)
        {
            for (int t = 0; t < tabs.Length; t++) tabs[t].Checked = t == i;
            grid.SetItems(i == 0 ? InkSymbols.Emoji : i == 1 ? InkSymbols.Kaomoji : InkSymbols.Symbols, i == 0, colorArgb);
            gridHost.AutoScrollPosition = Point.Empty;
        }

        void Insert(string s)
        {
            int at = box.SelectionStart;
            box.SelectedText = s;
            box.SelectionStart = at + s.Length;
            box.Focus();
        }

        void Raise(Action a) { if (!loading && a != null) a(); }

        void OpenWindowsEmojiPanel()
        {
            box.Focus();
            keybd_event(0x5B, 0, 0, IntPtr.Zero);       // Win
            keybd_event(0xBE, 0, 0, IntPtr.Zero);       // .
            keybd_event(0xBE, 0, 2, IntPtr.Zero);
            keybd_event(0x5B, 0, 2, IntPtr.Zero);
        }

        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Raise(Cancel); return true; }
            if (keyData == (Keys.Control | Keys.Enter)) { Raise(Commit); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            box.Focus();
        }
    }

    // A grid of emoji / kaomoji / symbols (drawn with the same renderer as the drawing, so emoji are in color).
    internal sealed class SymbolGrid : Control
    {
        public event Action<string> Picked;
        readonly float s;
        readonly Dictionary<string, InkText.Raster> cache = new Dictionary<string, InkText.Raster>();
        readonly List<KeyValuePair<Rectangle, string>> cells = new List<KeyValuePair<Rectangle, string>>();
        string[] items = new string[0];
        bool colorful;
        int color = unchecked((int)0xFFFFFFFF), hover = -1;
        readonly ToolTip tip = new ToolTip();

        public SymbolGrid(float scale)
        {
            s = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(48, 49, 53);
        }

        public void SetItems(string[] list, bool emoji, int argb)
        {
            items = list;
            colorful = emoji;
            ClearCache();
            cells.Clear();
            int cell = (int)(38 * s), x = 0, y = 0, width = Width;
            using (var f = new Font("Segoe UI", 15 * s, GraphicsUnit.Pixel))
            {
                foreach (string it in items)
                {
                    int w = cell;
                    if (!emoji)
                    {
                        int tw = TextRenderer.MeasureText(it, f).Width + (int)(10 * s);
                        w = (int)Math.Ceiling(tw / (float)cell) * cell;
                    }
                    if (x + w > width && x > 0) { x = 0; y += cell; }
                    cells.Add(new KeyValuePair<Rectangle, string>(new Rectangle(x, y, Math.Min(w, width), cell), it));
                    x += w;
                }
            }
            Height = y + cell;
            Invalidate();
        }

        InkText.Raster RasterFor(string it)
        {
            InkText.Raster r;
            if (cache.TryGetValue(it, out r)) return r;
            r = InkText.Render(it, "Segoe UI", (colorful ? 24 : 16) * s, false, false, color, InkText.Plain);
            cache[it] = r;
            return r;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < cells.Count; i++)
            {
                Rectangle rc = cells[i].Key;
                if (!rc.IntersectsWith(e.ClipRectangle)) continue;
                if (i == hover) using (var b = new SolidBrush(Color.FromArgb(70, 74, 80))) g.FillRectangle(b, Rectangle.Inflate(rc, -1, -1));
                var r = RasterFor(cells[i].Value);
                if (r == null) continue;
                float scaleDown = Math.Min(1, Math.Min((rc.Width - 4) / (float)r.Image.Width, (rc.Height - 4) / (float)r.Image.Height));
                float w = r.Image.Width * scaleDown, h = r.Image.Height * scaleDown;
                g.DrawImage(r.Image, rc.Left + (rc.Width - w) / 2, rc.Top + (rc.Height - h) / 2, w, h);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = cells.FindIndex(c => c.Key.Contains(e.Location));
            if (h != hover)
            {
                hover = h;
                Invalidate();
                if (h >= 0) tip.SetToolTip(this, cells[h].Value); else tip.SetToolTip(this, null);
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int h = cells.FindIndex(c => c.Key.Contains(e.Location));
            if (h >= 0 && Picked != null) Picked(cells[h].Value);
        }

        void ClearCache()
        {
            foreach (var r in cache.Values) if (r != null && r.Image != null) r.Image.Dispose();
            cache.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ClearCache(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // A simple dark slider (0..1).
    internal sealed class DarkSlider : Control
    {
        public event Action ValueChanged;
        float value;
        bool drag;

        public DarkSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(32, 33, 36);
        }

        public float Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pad = Height / 2f, cy = Height / 2f, x = pad + (Width - pad * 2) * value, th = Math.Max(2, Height / 10f);
            using (var p = new Pen(Color.FromArgb(90, 255, 255, 255), th) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, pad, cy, Width - pad, cy);
            using (var p = new Pen(Color.FromArgb(26, 115, 232), th) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, pad, cy, x, cy);
            float k = Height * 0.5f;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - k / 2, cy - k / 2, k, k);
        }

        void SetFrom(int x)
        {
            float pad = Height / 2f;
            Value = (x - pad) / (Width - pad * 2);
            var h = ValueChanged;
            if (h != null) h();
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); drag = true; Capture = true; SetFrom(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) SetFrom(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Capture = false; }
    }
}
