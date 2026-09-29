using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiveWall.Ink
{
    // The Layers panel of a board: the layers top first, each with an eye (show / hide) and a lock; click one to draw on
    // it, double-click to rename it. Below: the active layer's opacity, then New / Delete / Up / Down / Rename. Every
    // change goes through the editor (one undo step each).
    internal sealed class LayersPanel : InkPopup
    {
        const int Width96 = 340, RowH = 34;

        readonly InkEditor editor;
        readonly Button newButton, deleteButton, upButton, downButton, renameButton;
        readonly TextBox nameBox;
        List<InkLayerInfo> rows = new List<InkLayerInfo>();
        Rectangle listRect, sliderRect;
        int hover = -1, opacity = 100;
        bool dragging;
        string renaming;

        public LayersPanel(InkEditor editor, float scale) : base(scale)
        {
            this.editor = editor;
            newButton = MakeButton("New", 0, 0, 10, 10);
            deleteButton = MakeButton("Delete", 0, 0, 10, 10);
            upButton = MakeButton("Up", 0, 0, 10, 10);
            downButton = MakeButton("Down", 0, 0, 10, 10);
            renameButton = MakeButton("Rename", 0, 0, 10, 10);
            foreach (var b in new[] { newButton, deleteButton, upButton, downButton, renameButton }) b.Font = SmallFont;
            newButton.Click += (s, e) => editor.NewLayer();
            deleteButton.Click += (s, e) => { if (Active != null) editor.DeleteLayer(Active.Id); };
            upButton.Click += (s, e) => { if (Active != null) editor.MoveLayer(Active.Id, 1); };
            downButton.Click += (s, e) => { if (Active != null) editor.MoveLayer(Active.Id, -1); };
            renameButton.Click += (s, e) => { if (Active != null) StartRename(Active.Id); };
            nameBox = MakeBox(0, 0, 10);
            nameBox.Visible = false;
            nameBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndRename(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndRename(false); }
            };
            nameBox.Leave += (s, e) => EndRename(true);
            RefreshLayers();
        }

        InkLayerInfo Active { get { string id = editor.ActiveLayerId; return rows.Find(l => l.Id == id); } }

        // Again after every change (the editor calls it).
        public void RefreshLayers()
        {
            if (dragging) return;
            rows = editor.LayersTopFirst;
            int w = P(Width96), pad = P(10);
            listRect = new Rectangle(pad, P(38), w - pad * 2, P(RowH) * rows.Count);
            int y = listRect.Bottom + P(12);
            sliderRect = new Rectangle(pad + P(64), y, w - pad * 2 - P(64) - P(46), P(24));
            y += P(36);
            int gap = P(5), bw = (w - pad * 2 - gap * 4) / 5, bh = P(30);
            var buttons = new[] { newButton, deleteButton, upButton, downButton, renameButton };
            for (int i = 0; i < buttons.Length; i++) buttons[i].Bounds = new Rectangle(pad + i * (bw + gap), y, bw, bh);
            ClientSize = new Size(w, y + bh + pad);
            var a = Active;
            int at = a == null ? -1 : rows.IndexOf(a);
            newButton.Enabled = rows.Count < InkDocument.MaxLayers;
            deleteButton.Enabled = a != null && rows.Count > 1;
            upButton.Enabled = at > 0;
            downButton.Enabled = at >= 0 && at < rows.Count - 1;
            renameButton.Enabled = a != null;
            opacity = a == null ? 100 : a.Opacity;
            Invalidate();
        }

        Rectangle Row(int i) { return new Rectangle(listRect.Left, listRect.Top + i * P(RowH), listRect.Width, P(RowH)); }
        Rectangle Eye(int i) { var r = Row(i); return new Rectangle(r.Left + P(4), r.Top, P(30), r.Height); }
        Rectangle Lock(int i) { var r = Row(i); return new Rectangle(r.Left + P(34), r.Top, P(26), r.Height); }
        Rectangle NameArea(int i) { var r = Row(i); return Rectangle.FromLTRB(r.Left + P(64), r.Top, r.Right - P(48), r.Bottom); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, "Layers", UiFont, new Rectangle(P(12), P(8), P(120), P(24)), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "top", SmallFont, new Rectangle(ClientSize.Width - P(92), P(8), P(80), P(24)), Dim,
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            string active = editor.ActiveLayerId;
            for (int i = 0; i < rows.Count; i++)
            {
                var l = rows[i];
                var r = Row(i);
                bool isActive = l.Id == active;
                if (isActive) using (var b = new SolidBrush(Color.FromArgb(255, 38, 72, 120))) g.FillRectangle(b, r);
                else if (i == hover) using (var b = new SolidBrush(Color.FromArgb(255, 48, 49, 53))) g.FillRectangle(b, r);
                DrawEye(g, Eye(i), l.Visible);
                DrawLock(g, Lock(i), l.Locked);
                if (renaming != l.Id)
                    TextRenderer.DrawText(g, l.Name, UiFont, NameArea(i), l.Visible ? Color.White : Dim,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, l.Opacity + "%", SmallFont, Rectangle.FromLTRB(r.Right - P(48), r.Top, r.Right - P(6), r.Bottom), Dim,
                                      TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            // The active layer's opacity.
            TextRenderer.DrawText(g, "Opacity", SmallFont, new Rectangle(P(10), sliderRect.Top, P(64), sliderRect.Height), Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            float cy = sliderRect.Top + sliderRect.Height / 2f, kx = sliderRect.Left + sliderRect.Width * opacity / 100f;
            using (var p = new Pen(Color.FromArgb(90, 255, 255, 255), 3 * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, sliderRect.Left, cy, sliderRect.Right, cy);
            using (var p = new Pen(Accent, 3 * S) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, sliderRect.Left, cy, kx, cy);
            float k = 14 * S;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - k / 2, cy - k / 2, k, k);
            TextRenderer.DrawText(g, opacity + "%", SmallFont, Rectangle.FromLTRB(sliderRect.Right + P(6), sliderRect.Top, ClientSize.Width - P(10), sliderRect.Bottom),
                                  Color.White, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        void DrawEye(Graphics g, Rectangle r, bool open)
        {
            float w = 16 * S, h = 9 * S, cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            Color c = open ? Color.White : Color.FromArgb(110, 255, 255, 255);
            using (var p = new Pen(c, Math.Max(1, 1.4f * S)))
            using (var path = new GraphicsPath())
            {
                path.AddBezier(cx - w / 2, cy, cx - w / 4, cy - h, cx + w / 4, cy - h, cx + w / 2, cy);
                path.AddBezier(cx + w / 2, cy, cx + w / 4, cy + h, cx - w / 4, cy + h, cx - w / 2, cy);
                g.DrawPath(p, path);
                if (open) using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 2.6f * S, cy - 2.6f * S, 5.2f * S, 5.2f * S);
                else g.DrawLine(p, cx - w / 2, cy + h / 1.4f, cx + w / 2, cy - h / 1.4f);
            }
        }

        void DrawLock(Graphics g, Rectangle r, bool locked)
        {
            float w = 10 * S, h = 8 * S, cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f + 2 * S;
            Color c = locked ? Color.White : Color.FromArgb(70, 255, 255, 255);
            using (var p = new Pen(c, Math.Max(1, 1.4f * S)))
            {
                if (locked) using (var b = new SolidBrush(c)) g.FillRectangle(b, cx - w / 2, cy - h / 2, w, h);
                else g.DrawRectangle(p, cx - w / 2, cy - h / 2, w, h);
                float a = 3.5f * S;
                g.DrawArc(p, cx - a, cy - h / 2 - a * 1.6f, a * 2, a * 2.4f, 180, locked ? 180 : 150);
            }
        }

        // ------------------------------------------------------------------ input

        int RowAt(Point p)
        {
            if (!listRect.Contains(p)) return -1;
            int i = (p.Y - listRect.Top) / P(RowH);
            return i >= 0 && i < rows.Count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { SlideTo(e.X); return; }
            int h = RowAt(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (Rectangle.Inflate(sliderRect, 0, P(6)).Contains(e.Location) && Active != null)
            {
                dragging = true;
                Capture = true;
                SlideTo(e.X);
                return;
            }
            int i = RowAt(e.Location);
            if (i < 0) return;
            var l = rows[i];
            if (Eye(i).Contains(e.Location)) editor.ShowLayer(l.Id, !l.Visible);
            else if (Lock(i).Contains(e.Location)) editor.LockLayer(l.Id, !l.Locked);
            else if (l.Id != editor.ActiveLayerId) editor.SelectLayer(l.Id);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Capture = false;
            if (Active != null) editor.SetLayerOpacity(Active.Id, opacity);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = RowAt(e.Location);
            if (i >= 0 && NameArea(i).Contains(e.Location)) StartRename(rows[i].Id);
        }

        void SlideTo(int x)
        {
            int v = (int)Math.Round(Math.Max(0, Math.Min(1, (x - sliderRect.Left) / (float)sliderRect.Width)) * 100);
            if (v == opacity) return;
            opacity = v;
            editor.PreviewLayerOpacity(v);
            Invalidate();
        }

        void StartRename(string id)
        {
            int i = rows.FindIndex(l => l.Id == id);
            if (i < 0) return;
            renaming = id;
            var r = NameArea(i);
            nameBox.Bounds = new Rectangle(r.Left, r.Top + (r.Height - nameBox.Height) / 2, r.Width + P(40), nameBox.Height);
            nameBox.Text = rows[i].Name;
            nameBox.Visible = true;
            nameBox.Focus();
            nameBox.SelectAll();
            Invalidate();
        }

        void EndRename(bool keep)
        {
            if (renaming == null) return;
            string id = renaming, name = nameBox.Text.Trim();
            renaming = null;
            nameBox.Visible = false;
            if (keep && name.Length > 0) editor.RenameLayer(id, name);
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (renaming != null) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Escape || keyData == Keys.Enter) { Hide(); return true; }
            if (keyData == Keys.F2 && Active != null) { StartRename(Active.Id); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            EndRename(true);
            if (Visible) Hide();
        }
    }
}
