using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    // The floating toolbar (and its flyouts): owner-drawn, never takes focus away from the drawing, so shortcuts keep
    // working.
    internal sealed class InkToolbar : Form
    {
        internal enum Kind { Button, Swatch, Slider, Segment, Separator, Accent, Hint, Label }

        internal sealed class Item
        {
            public Kind Kind;
            public string Glyph, Fallback, Tip;
            public Action Click;
            public Func<bool> Selected, Enabled;
            public Color Swatch;
            public Action<Graphics, RectangleF, Color> Icon;     // custom-drawn icon instead of a glyph
            public Func<bool> IconWhen;                           // with a glyph too: the icon only while this is true
            public Func<string> Text;                             // label: its text now
            public bool HasFlyout;                                // small corner mark: more choices on click
            public Func<float> Value;                             // slider 0..1
            public Action<float> SetValue;
            public Func<float> DotSize;                           // slider preview, device pixels
            public string[] Labels;                               // segment
            public Func<int> SegmentSelected;
            public Action<int> SegmentClick;
            public Rectangle Rect;
            public int[] SegmentEdges;

            public static Item Button(string glyph, string fallback, string tip, Action click, Func<bool> selected, Func<bool> enabled = null)
            { return new Item { Kind = Kind.Button, Glyph = glyph, Fallback = fallback, Tip = tip, Click = click, Selected = selected, Enabled = enabled }; }
            public static Item Custom(Action<Graphics, RectangleF, Color> icon, string tip, Action click, Func<bool> selected)
            { return new Item { Kind = Kind.Button, Icon = icon, Tip = tip, Click = click, Selected = selected }; }
            public static Item ColorSwatch(Color c, string tip, Action click, Func<bool> selected)
            { return new Item { Kind = Kind.Swatch, Swatch = c, Tip = tip, Click = click, Selected = selected }; }
            public static Item Slider(string tip, Func<float> value, Action<float> set, Func<float> dot, Func<bool> enabled)
            { return new Item { Kind = Kind.Slider, Tip = tip, Value = value, SetValue = set, DotSize = dot, Enabled = enabled }; }
            public static Item Segment(string[] labels, string tip, Func<int> selected, Action<int> click)
            { return new Item { Kind = Kind.Segment, Labels = labels, Tip = tip, SegmentSelected = selected, SegmentClick = click }; }
            public static Item Separator() { return new Item { Kind = Kind.Separator }; }
            public static Item Hint(string text) { return new Item { Kind = Kind.Hint, Fallback = text }; }   // small dim text, not clickable
            public static Item Label(Func<string> text, string tip, Action click) { return new Item { Kind = Kind.Label, Text = text, Tip = tip, Click = click }; }
            public static Item Accent(string glyph, string fallback, string tip, Action click)
            { return new Item { Kind = Kind.Accent, Glyph = glyph, Fallback = fallback, Tip = tip, Click = click }; }
        }

        static readonly Color Back = Color.FromArgb(32, 33, 36), AccentColor = Color.FromArgb(26, 115, 232);

        readonly Form owner;
        readonly Rectangle monitor;
        readonly List<Item> items;
        readonly float scale;
        readonly Font glyphFont, textFont;
        readonly bool haveGlyphs, isFlyout;
        readonly ToolTip tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
        int hover = -1, grip;
        string shownTip;
        Item dragging;
        InkToolbar flyout;

        // `avoid`: a screen rectangle to keep clear (the board's date). Flyouts are placed by the caller.
        public InkToolbar(Form owner, Rectangle monitor, List<Item> items, float dpiScale, Rectangle avoid, bool isFlyout = false)
        {
            this.owner = owner;
            this.monitor = monitor;
            this.items = items;
            this.isFlyout = isFlyout;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Back;
            DoubleBuffered = true;
            haveGlyphs = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons" || f.Name == "Segoe MDL2 Assets");
            string glyphFamily = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

            // Shrink on narrow screens so the whole bar fits.
            scale = dpiScale;
            int natural = LayoutItems(scale, null);
            int room = monitor.Width - (int)(24 * dpiScale);
            if (natural > room) scale = dpiScale * room / (float)natural;
            glyphFont = haveGlyphs ? new Font(glyphFamily, 16 * scale, GraphicsUnit.Pixel) : null;
            textFont = new Font("Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);
            int width = LayoutItems(scale, textFont);
            int s = (int)(38 * scale), pad = (int)(6 * scale);
            ClientSize = new Size(width, s + pad * 2);

            if (isFlyout) return;
            // Top centre; moved left of the board's date if it would cover it, or below the date if there's no room.
            int gap = (int)(18 * scale);
            var bar = new Rectangle(monitor.Left + (monitor.Width - ClientSize.Width) / 2, monitor.Top + gap, ClientSize.Width, ClientSize.Height);
            if (!avoid.IsEmpty && bar.IntersectsWith(Rectangle.Inflate(avoid, gap / 2, gap / 2)))
            {
                if (avoid.Left - gap - bar.Width >= monitor.Left + gap) bar.X = avoid.Left - gap - bar.Width;
                else bar.Y = avoid.Bottom + gap;
            }
            Location = bar.Location;
        }

        // Positions the items; returns the bar's width. Without a font, segment labels are estimated.
        int LayoutItems(float sc, Font font)
        {
            int s = (int)(38 * sc), pad = (int)(6 * sc);
            grip = isFlyout ? 0 : (int)(16 * sc);
            int x = pad + grip;
            foreach (var it in items)
            {
                int w;
                switch (it.Kind)
                {
                    case Kind.Separator: w = (int)(11 * sc); break;
                    case Kind.Hint:
                        w = (int)((font != null ? TextRenderer.MeasureText(it.Fallback, font).Width * 0.85f : it.Fallback.Length * 7 * sc) + 14 * sc);
                        break;
                    case Kind.Swatch: w = (int)(30 * sc); break;
                    case Kind.Label: w = (int)(128 * sc); break;
                    case Kind.Slider: w = (int)(150 * sc); break;
                    case Kind.Segment:
                    {
                        it.SegmentEdges = new int[it.Labels.Length + 1];
                        w = (int)(4 * sc);
                        it.SegmentEdges[0] = w;
                        for (int i = 0; i < it.Labels.Length; i++)
                        {
                            float tw = font != null ? TextRenderer.MeasureText(it.Labels[i], font).Width : it.Labels[i].Length * 8 * sc;
                            w += (int)(tw + 22 * sc);
                            it.SegmentEdges[i + 1] = w;
                        }
                        w += (int)(4 * sc);
                        break;
                    }
                    default: w = s; break;
                }
                it.Rect = new Rectangle(x, pad, w, s);
                x += w;
            }
            return x + pad;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
                return cp;
            }
        }

        // Clicks never activate the bar: the drawing keeps the keyboard.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021 /*WM_MOUSEACTIVATE*/) { m.Result = new IntPtr(3 /*MA_NOACTIVATE*/); return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // Windows 11: rounded corners (ignored elsewhere)
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ------------------------------------------------------------------ flyouts

        public void ShowFlyout(Item under, List<Item> flyoutItems)
        {
            CloseFlyout();
            tip.Hide(this);
            shownTip = null;
            flyout = new InkToolbar(owner, monitor, flyoutItems, scale, Rectangle.Empty, true);
            flyout.Location = new Point(Left + under.Rect.Left - (int)(6 * scale), Bottom + (int)(6 * scale));
            flyout.Show(owner);
        }

        public bool FlyoutOpen { get { return flyout != null && !flyout.IsDisposed; } }

        // A short note under a button (e.g. where a picture was saved).
        public void ShowMessage(Item under, string text)
        {
            shownTip = text;
            tip.Show(text, this, under.Rect.Left, under.Rect.Bottom + (int)(8 * scale), 5000);
        }

        public void CloseFlyout()
        {
            if (flyout != null && !flyout.IsDisposed) flyout.Close();
            flyout = null;
        }

        public void RefreshAll()
        {
            Invalidate();
            if (FlyoutOpen) flyout.Invalidate();
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (grip > 0)
                using (var b = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                {
                    float d = 3 * scale, cx = (6 * scale + grip) / 2f, cy = ClientSize.Height / 2f;
                    for (int i = -1; i <= 1; i++) { g.FillEllipse(b, cx - d * 1.2f, cy + i * d * 2 - d / 2, d, d); g.FillEllipse(b, cx + d * 0.2f, cy + i * d * 2 - d / 2, d, d); }
                }
            for (int i = 0; i < items.Count; i++) PaintItem(g, items[i], i == hover);
        }

        void PaintItem(Graphics g, Item it, bool hot)
        {
            Rectangle r = it.Rect;
            bool enabled = it.Enabled == null || it.Enabled();
            bool selected = it.Selected != null && it.Selected();
            var inner = Rectangle.Inflate(r, -(int)(2 * scale), -(int)(2 * scale));
            Color fg = enabled ? Color.White : Color.FromArgb(90, 255, 255, 255);
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            switch (it.Kind)
            {
                case Kind.Separator:
                    using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1, scale)))
                        g.DrawLine(p, cx, r.Top + 8 * scale, cx, r.Bottom - 8 * scale);
                    return;
                case Kind.Hint:
                    using (var f = new Font(textFont.FontFamily, textFont.Size * 0.85f, FontStyle.Regular, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, it.Fallback, f, r, Color.FromArgb(160, 255, 255, 255),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    return;
                case Kind.Label:
                {
                    if (hot) FillRound(g, inner, Color.FromArgb(255, 60, 64, 67));
                    float s = 16 * scale;
                    var icon = new RectangleF(r.Left + 8 * scale, cy - s / 2, s, s);
                    DrawLayersIcon(g, icon, fg);
                    var tr = Rectangle.FromLTRB((int)(icon.Right + 6 * scale), r.Top, r.Right - (int)(6 * scale), r.Bottom);
                    TextRenderer.DrawText(g, it.Text(), textFont, tr, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    return;
                }
                case Kind.Swatch:
                {
                    if (hot) FillRound(g, inner, Color.FromArgb(255, 60, 64, 67));
                    float d = (selected ? 22 : 18) * scale;
                    using (var b = new SolidBrush(it.Swatch)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                    using (var p = new Pen(selected ? Color.White : Color.FromArgb(120, 255, 255, 255), Math.Max(1, (selected ? 2 : 1) * scale)))
                        g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
                    return;
                }
                case Kind.Slider:
                {
                    float left = r.Left + 34 * scale, right = r.Right - 10 * scale, t = Math.Max(0, Math.Min(1, it.Value()));
                    float dot = Math.Max(3 * scale, Math.Min(26 * scale, it.DotSize()));
                    using (var b = new SolidBrush(fg)) g.FillEllipse(b, r.Left + 17 * scale - dot / 2, cy - dot / 2, dot, dot);
                    using (var p = new Pen(Color.FromArgb(enabled ? 90 : 40, 255, 255, 255), 3 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(p, left, cy, right, cy);
                    float kx = left + (right - left) * t;
                    if (enabled)
                    {
                        using (var p = new Pen(AccentColor, 3 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, left, cy, kx, cy);
                        float k = 14 * scale;
                        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - k / 2, cy - k / 2, k, k);
                    }
                    return;
                }
                case Kind.Segment:
                {
                    int sel = it.SegmentSelected();
                    var outer = Rectangle.Inflate(r, -(int)(2 * scale), -(int)(3 * scale));
                    using (var path = InkText.RoundRect(outer, 8 * scale))
                    using (var p = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1, 1.5f * scale)))
                        g.DrawPath(p, path);
                    for (int i = 0; i < it.Labels.Length; i++)
                    {
                        var seg = Rectangle.FromLTRB(r.Left + it.SegmentEdges[i], outer.Top + (int)(3 * scale), r.Left + it.SegmentEdges[i + 1], outer.Bottom - (int)(3 * scale));
                        if (i == sel) FillRound(g, seg, AccentColor);
                        else if (hot && hoverSegment == i) FillRound(g, seg, Color.FromArgb(255, 60, 64, 67));
                        TextRenderer.DrawText(g, it.Labels[i], textFont, seg, i == sel ? Color.White : Color.FromArgb(215, 255, 255, 255),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    return;
                }
            }
            if (it.Kind == Kind.Accent) FillRound(g, inner, Color.FromArgb(hot ? 255 : 230, AccentColor));
            else if (selected) FillRound(g, inner, Color.FromArgb(255, 80, 84, 90));
            else if (hot && enabled) FillRound(g, inner, Color.FromArgb(255, 60, 64, 67));

            if (it.Icon != null && (it.IconWhen == null || it.IconWhen()))
                it.Icon(g, new RectangleF(r.Left + 10 * scale, r.Top + 10 * scale, r.Width - 20 * scale, r.Height - 20 * scale), fg);
            else
            {
                Font f = haveGlyphs ? glyphFont : textFont;
                string t = haveGlyphs ? it.Glyph : it.Fallback;
                using (var b = new SolidBrush(fg))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(t, f, b, new RectangleF(r.Left, r.Top, r.Width, r.Height), sf);
            }
            if (it.HasFlyout)
                using (var b = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
                {
                    float x = r.Right - 7 * scale, y = r.Bottom - 7 * scale, s = 4 * scale;
                    g.FillPolygon(b, new[] { new PointF(x, y), new PointF(x - s, y), new PointF(x, y - s) });
                }
        }

        void FillRound(Graphics g, Rectangle r, Color c)
        {
            using (var path = InkText.RoundRect(r, 6 * scale))
            using (var b = new SolidBrush(c)) g.FillPath(b, path);
        }

        // Icons for the shape tools (line, arrow, rectangle, ellipse), filled or not.
        public static void DrawShapeIcon(Graphics g, RectangleF r, InkTool shape, bool filled, Color fg)
        {
            using (var p = new Pen(fg, Math.Max(1.5f, r.Width / 10)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(fg))
            {
                switch (shape)
                {
                    case InkTool.Line: g.DrawLine(p, r.Left + 1, r.Bottom - 1, r.Right - 1, r.Top + 1); break;
                    case InkTool.Arrow:
                    {
                        var head = InkRenderer.ArrowHeadPoints(new InkPoint(r.Left + 1, r.Bottom - 1, 0), new InkPoint(r.Right - 1, r.Top + 1, 0), r.Width / 10);
                        g.DrawLine(p, r.Left + 1, r.Bottom - 1, (head[1].X + head[2].X) / 2, (head[1].Y + head[2].Y) / 2);
                        g.FillPolygon(b, head);
                        break;
                    }
                    case InkTool.Rectangle:
                    {
                        var rr = RectangleF.Inflate(r, -1, -r.Height * 0.12f);
                        if (filled) g.FillRectangle(b, rr);
                        g.DrawRectangle(p, rr.X, rr.Y, rr.Width, rr.Height);
                        break;
                    }
                    case InkTool.Ellipse:
                    {
                        var rr = RectangleF.Inflate(r, -1, -1);
                        if (filled) g.FillEllipse(b, rr);
                        g.DrawEllipse(p, rr);
                        break;
                    }
                }
            }
        }

        // Three stacked sheets.
        public static void DrawLayersIcon(Graphics g, RectangleF r, Color fg)
        {
            float cx = r.Left + r.Width / 2, w = r.Width / 2, h = r.Height / 4.2f;
            using (var p = new Pen(fg, Math.Max(1.2f, r.Width / 12)) { LineJoin = LineJoin.Round })
                for (int i = 2; i >= 0; i--)
                {
                    float cy = r.Top + h + i * h * 0.95f;
                    g.DrawPolygon(p, new[] { new PointF(cx, cy - h), new PointF(cx + w, cy), new PointF(cx, cy + h), new PointF(cx - w, cy) });
                }
        }

        // The custom color button: current color inside a rainbow ring.
        public static void DrawColorWheelIcon(Graphics g, RectangleF r, Color current)
        {
            float d = Math.Min(r.Width, r.Height) + 4;
            var ring = new RectangleF(r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
            Color[] hues = { Color.Red, Color.Orange, Color.Yellow, Color.LimeGreen, Color.DeepSkyBlue, Color.RoyalBlue, Color.MediumOrchid, Color.DeepPink };
            float w = Math.Max(2, d / 7);
            for (int i = 0; i < hues.Length; i++)
                using (var p = new Pen(hues[i], w))
                    g.DrawArc(p, RectangleF.Inflate(ring, -w / 2, -w / 2), i * 45 - 90, 46);
            float c = d - w * 2 - 4;
            using (var b = new SolidBrush(current)) g.FillEllipse(b, ring.Left + (d - c) / 2, ring.Top + (d - c) / 2, c, c);
        }

        // ------------------------------------------------------------------ input

        int hoverSegment = -1;

        int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Kind != Kind.Separator && items[i].Kind != Kind.Hint && items[i].Rect.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging != null) { SlideTo(dragging, e.X); return; }
            int h = HitTest(e.Location);
            int seg = h >= 0 && items[h].Kind == Kind.Segment ? SegmentAt(items[h], e.X) : -1;
            if (h != hover || seg != hoverSegment) { hover = h; hoverSegment = seg; Invalidate(); }
            string t = h >= 0 ? items[h].Tip : null;
            if (t != shownTip)
            {
                shownTip = t;
                if (t == null) tip.Hide(this);
                else tip.Show(t, this, items[h].Rect.Left, items[h].Rect.Bottom + (int)(8 * scale), 4000);
            }
            Cursor = grip > 0 && e.X < grip + 6 * scale ? Cursors.SizeAll : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            hoverSegment = -1;
            shownTip = null;
            tip.Hide(this);
            Invalidate();
        }

        int SegmentAt(Item it, int x)
        {
            for (int i = 0; i < it.Labels.Length; i++) if (x < it.Rect.Left + it.SegmentEdges[i + 1]) return i;
            return it.Labels.Length - 1;
        }

        void SlideTo(Item it, int x)
        {
            float left = it.Rect.Left + 34 * scale, right = it.Rect.Right - 10 * scale;
            it.SetValue(Math.Max(0, Math.Min(1, (x - left) / (right - left))));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (grip > 0 && e.X < grip + 6 * scale)
            {
                // Drag the toolbar by its grip.
                CloseFlyout();
                InkNative.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, new IntPtr(2 /*HTCAPTION*/), IntPtr.Zero);
                return;
            }
            int h = HitTest(e.Location);
            if (h >= 0 && items[h].Kind == Kind.Slider && (items[h].Enabled == null || items[h].Enabled()))
            {
                dragging = items[h];
                Capture = true;
                SlideTo(dragging, e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging != null) { dragging = null; Capture = false; return; }
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h < 0) return;
            var it = items[h];
            if (it.Enabled != null && !it.Enabled()) return;
            if (it.Kind == Kind.Segment) { it.SegmentClick(SegmentAt(it, e.X)); }
            else if (it.Click != null)
            {
                if (!it.HasFlyout) CloseFlyout();
                it.Click();
            }
            if (!IsDisposed) Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int h = HitTest(e.Location);
            if (h < 0 || items[h].Kind != Kind.Slider) return;
            items[h].SetValue(Math.Max(0, Math.Min(1, items[h].Value() + (e.Delta > 0 ? 0.04f : -0.04f))));
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseFlyout();
                tip.Dispose();
                if (glyphFont != null) glyphFont.Dispose();
                textFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
