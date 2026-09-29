using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LiveWall.Ink
{
    // The selection tool: select part of the drawing with a rectangle or a lasso, then move, resize, rotate, flip, cut,
    // copy or delete it; paste pictures (also from other apps).
    //
    // Selecting lifts the pixels off the ink layer into a floating picture right away (the box then fits the ink).
    // Nothing is written until it is put down (Enter, Esc, clicking outside, another tool) - and only if it was changed:
    // then an "erase area" element (where it came from) and an image element (where it is now), as one undo step.
    // A pasted picture has no source area.
    internal sealed partial class InkEditor
    {
        enum SelectShape { Rectangle, Lasso }
        enum DragKind { None, Marquee, Move, Resize, Rotate }

        sealed class Floating
        {
            public Bitmap Image;                     // premultiplied, screen resolution
            public PointF Center;                    // screen pixels (client)
            public float Sx = 1, Sy = 1, Angle;      // scale, rotation in degrees
            public bool FlipX, FlipY, Changed;
            public InkPoint[] Source;                // canvas outline it was lifted from; null when pasted

            public float W { get { return Image.Width * Sx; } }
            public float H { get { return Image.Height * Sy; } }

            public Floating CloneState() { return (Floating)MemberwiseClone(); }

            public PointF ToScreen(float lx, float ly)
            {
                double a = Angle * Math.PI / 180;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                return new PointF(Center.X + lx * c - ly * s, Center.Y + lx * s + ly * c);
            }

            public PointF ToLocal(PointF p)
            {
                double a = -Angle * Math.PI / 180;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a), dx = p.X - Center.X, dy = p.Y - Center.Y;
                return new PointF(dx * c - dy * s, dx * s + dy * c);
            }

            // The box: top-left, top-right, bottom-right, bottom-left.
            public PointF[] Box()
            {
                float hw = W / 2, hh = H / 2;
                return new[] { ToScreen(-hw, -hh), ToScreen(hw, -hh), ToScreen(hw, hh), ToScreen(-hw, hh) };
            }

            // Where the image's own top-left, top-right and bottom-left go (flips included).
            public PointF[] ImageCorners()
            {
                float hw = W / 2 * (FlipX ? -1 : 1), hh = H / 2 * (FlipY ? -1 : 1);
                return new[] { ToScreen(-hw, -hh), ToScreen(hw, -hh), ToScreen(-hw, hh) };
            }
        }

        static SelectShape selectShape = SelectShape.Rectangle;

        Floating floating;
        List<PointF> marquee;                  // screen pixels while dragging out a selection
        DragKind drag;
        int dragHx, dragHy;
        PointF dragFrom;
        Floating dragStart;
        Rectangle overlay;                     // screen area last drawn for the selection
        InkToolbar selectionBar;

        bool Selecting { get { return floating != null || marquee != null; } }

        float HandleSize { get { return 9 * dpiScale; } }
        float RotateReach { get { return 30 * dpiScale; } }

        // ------------------------------------------------------------------ input (select tool)

        void SelectBegin(Point p)
        {
            HideSelectionBar();
            if (floating != null)
            {
                int hx, hy;
                DragKind kind = HitSelection(p, out hx, out hy);
                if (kind != DragKind.None)
                {
                    drag = kind;
                    dragHx = hx;
                    dragHy = hy;
                    dragFrom = p;
                    dragStart = floating.CloneState();
                    active = true;
                    return;
                }
                PutDown();
            }
            marquee = new List<PointF> { p };
            drag = DragKind.Marquee;
            dragFrom = p;
            active = true;
            DrawSelection();
        }

        void SelectExtend(Point p)
        {
            switch (drag)
            {
                case DragKind.Marquee:
                    if (selectShape == SelectShape.Lasso)
                    {
                        PointF last = marquee[marquee.Count - 1];
                        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 2) marquee.Add(p);
                    }
                    else marquee = new List<PointF> { dragFrom, new PointF(p.X, dragFrom.Y), p, new PointF(dragFrom.X, p.Y) };
                    break;
                case DragKind.Move:
                    floating.Center = new PointF(dragStart.Center.X + p.X - dragFrom.X, dragStart.Center.Y + p.Y - dragFrom.Y);
                    floating.Changed = true;
                    break;
                case DragKind.Rotate:
                {
                    double a0 = Math.Atan2(dragFrom.Y - dragStart.Center.Y, dragFrom.X - dragStart.Center.X);
                    double a1 = Math.Atan2(p.Y - dragStart.Center.Y, p.X - dragStart.Center.X);
                    float angle = dragStart.Angle + (float)((a1 - a0) * 180 / Math.PI);
                    if ((ModifierKeys & Keys.Shift) != 0) angle = (float)Math.Round(angle / 15) * 15;
                    floating.Angle = angle;
                    floating.Changed = true;
                    break;
                }
                case DragKind.Resize: ResizeSelection(p); break;
            }
            DrawSelection();
        }

        // Drags a handle: the opposite side stays put; corners keep the proportions unless Shift is held.
        void ResizeSelection(PointF p)
        {
            var s0 = dragStart;
            float w0 = s0.W, h0 = s0.H;
            PointF anchor = s0.ToScreen(-dragHx * w0 / 2, -dragHy * h0 / 2);
            double a = -s0.Angle * Math.PI / 180;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a), dx = p.X - anchor.X, dy = p.Y - anchor.Y;
            float lx = dx * c - dy * s, ly = dx * s + dy * c;
            float min = 6 * dpiScale;
            float w = dragHx != 0 ? Math.Max(min, lx * dragHx) : w0;
            float h = dragHy != 0 ? Math.Max(min, ly * dragHy) : h0;
            if (dragHx != 0 && dragHy != 0 && (ModifierKeys & Keys.Shift) == 0)
            {
                float k = Math.Max(w / w0, h / h0);
                w = w0 * k;
                h = h0 * k;
            }
            floating.Sx = w / floating.Image.Width;
            floating.Sy = h / floating.Image.Height;
            // New center: from the anchor, half the new size towards the dragged handle.
            double b = s0.Angle * Math.PI / 180;
            float cb = (float)Math.Cos(b), sb = (float)Math.Sin(b), ox = dragHx * w / 2, oy = dragHy * h / 2;
            floating.Center = new PointF(anchor.X + ox * cb - oy * sb, anchor.Y + ox * sb + oy * cb);

            floating.Changed = true;
        }

        void SelectEnd()
        {
            DragKind kind = drag;
            drag = DragKind.None;
            if (kind == DragKind.Marquee)
            {
                var outline = marquee;
                marquee = null;
                RectangleF b = OutlineBounds(outline);
                if (outline.Count >= 3 && b.Width >= 3 && b.Height >= 3) Lift(outline.ToArray());
                else DrawSelection();
                if (floating == null && b.Width >= 3 && b.Height >= 3 && toolbar != null && selectItem != null)
                    toolbar.ShowMessage(selectItem, "Nothing drawn there to select");
            }
            ShowSelectionBar();
        }

        DragKind HitSelection(PointF p, out int hx, out int hy)
        {
            hx = hy = 0;
            PointF l = floating.ToLocal(p);
            float hw = floating.W / 2, hh = floating.H / 2, tol = HandleSize;
            if (Math.Abs(l.X) <= tol && Math.Abs(l.Y + hh + RotateReach) <= tol) return DragKind.Rotate;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0) continue;
                    if (Math.Abs(l.X - x * hw) <= tol && Math.Abs(l.Y - y * hh) <= tol) { hx = x; hy = y; return DragKind.Resize; }
                }
            if (Math.Abs(l.X) <= hw && Math.Abs(l.Y) <= hh) return DragKind.Move;
            return DragKind.None;
        }

        Cursor SelectionCursor(Point p)
        {
            if (floating == null || drag != DragKind.None && drag != DragKind.Move) return null;
            int hx, hy;
            switch (HitSelection(p, out hx, out hy))
            {
                case DragKind.Move: return Cursors.SizeAll;
                case DragKind.Rotate: return Cursors.Hand;
                case DragKind.Resize:
                {
                    // The handle's direction on screen picks the arrow.
                    PointF d = floating.ToScreen(hx, hy), c = floating.Center;
                    double ang = (Math.Atan2(d.Y - c.Y, d.X - c.X) * 180 / Math.PI + 360) % 180;
                    return ang < 22.5 || ang >= 157.5 ? Cursors.SizeWE : ang < 67.5 ? Cursors.SizeNWSE : ang < 112.5 ? Cursors.SizeNS : Cursors.SizeNESW;
                }
            }
            return null;
        }

        static RectangleF OutlineBounds(IList<PointF> pts)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var q in pts) { x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); }
            return pts.Count == 0 ? RectangleF.Empty : RectangleF.FromLTRB(x0, y0, x1, y1);
        }

        // ------------------------------------------------------------------ lift, put down, cancel

        // Takes the ink inside the outline (screen pixels) off the ink layer into a floating picture.
        void Lift(PointF[] outline)
        {
            Rectangle box = Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(OutlineBounds(outline)), 1, 1), ClientArea);
            if (box.Width <= 0 || box.Height <= 0) { DrawSelection(); return; }
            Bitmap cut;
            using (var bmp = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(outline.Select(q => new PointF(q.X - box.X, q.Y - box.Y)).ToArray());
                    g.SetClip(path);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(ink, new Rectangle(0, 0, box.Width, box.Height), box, GraphicsUnit.Pixel);
                }
                Rectangle used = InkBounds(bmp);
                if (used.IsEmpty) { DrawSelection(); return; }
                cut = bmp.Clone(used, PixelFormat.Format32bppPArgb);
                box = new Rectangle(box.X + used.X, box.Y + used.Y, used.Width, used.Height);
            }
            floating = new Floating
            {
                Image = cut,
                Center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f),
                Source = outline.Select(q => { PointF c = map.ToCanvas(q.X, q.Y); return new InkPoint(c.X, c.Y, 128); }).ToArray()
            };
            ClearSourceOnInk(Rectangle.Round(OutlineBounds(outline)));
            DrawSelection();
        }

        // While a lifted selection floats, its source area stays empty on the ink layer.
        void ClearSourceOnInk(Rectangle area)
        {
            if (floating == null || floating.Source == null) return;
            using (var g = Graphics.FromImage(ink))
                InkRenderer.DrawEraseArea(g, floating.Source.Select(q => map.ToTarget(q.X, q.Y)).ToArray());
            Compose(Rectangle.Inflate(area, 2, 2), false);
        }

        static Rectangle InkBounds(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new int[bmp.Width];
                int x0 = bmp.Width, y0 = bmp.Height, x1 = -1, y1 = -1;
                for (int y = 0; y < bmp.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bmp.Width);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        if (((row[x] >> 24) & 0xFF) == 0) continue;
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
                }
                return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            }
            finally { bmp.UnlockBits(data); }
        }

        // Writes a changed selection into the drawing (one undo step); an unchanged one just goes back.
        void PutDown()
        {
            if (drag != DragKind.None) { drag = DragKind.None; active = false; }
            marquee = null;
            HideSelectionBar();
            var f = floating;
            if (f == null) { DrawSelection(); return; }
            if (!f.Changed) { CancelSelection(); return; }
            floating = null;
            var action = new UndoAction();
            RectangleF dirty = RectangleF.Empty;
            if (f.Source != null)
            {
                var erase = InkStroke.EraseArea(NewId(), author, DateTime.UtcNow.Ticks, f.Source);
                Document.Add(erase);
                action.Added.Add(erase.Id);
                dirty = erase.Bounds;
            }
            var item = MakeImageItem(f);
            Document.Add(item);
            action.Added.Add(item.Id);
            dirty = dirty.IsEmpty ? item.Bounds : RectangleF.Union(dirty, item.Bounds);
            undo.Push(action);
            redo.Clear();
            f.Image.Dispose();
            RenderRegion(Rectangle.Union(TargetRect(dirty, 4), overlay));
            overlay = Rectangle.Empty;
            RefreshToolbar();
        }

        InkStroke MakeImageItem(Floating f)
        {
            // Shrunk a lot: keep only the pixels that show (smaller file, same look).
            Bitmap img = f.Image;
            bool resampled = false;
            if (f.Sx < 0.75f && f.Sy < 0.75f)
            {
                int w = Math.Max(1, (int)Math.Ceiling(f.W)), h = Math.Max(1, (int)Math.Ceiling(f.H));
                img = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(img))
                using (var ia = new ImageAttributes())
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    ia.SetWrapMode(WrapMode.TileFlipXY);   // no faded border
                    g.DrawImage(f.Image, new Rectangle(0, 0, w, h), 0, 0, f.Image.Width, f.Image.Height, GraphicsUnit.Pixel, ia);
                }
                resampled = true;
            }
            PointF[] c = f.ImageCorners();
            PointF tl = map.ToCanvas(c[0].X, c[0].Y), tr = map.ToCanvas(c[1].X, c[1].Y), bl = map.ToCanvas(c[2].X, c[2].Y);
            string data = InkImage.Encode(img);
            if (resampled) img.Dispose();
            return InkStroke.ImageItem(NewId(), author, DateTime.UtcNow.Ticks, tl, tr, bl, data);
        }

        // Drops the selection without changing the drawing (a lifted one goes back where it was).
        void CancelSelection()
        {
            if (drag != DragKind.None) { drag = DragKind.None; active = false; }
            marquee = null;
            HideSelectionBar();
            var f = floating;
            floating = null;
            Rectangle r = overlay;
            overlay = Rectangle.Empty;
            if (f != null)
            {
                if (f.Source != null) r = Rectangle.Union(r, TargetRect(OutlineBounds(f.Source.Select(q => new PointF(q.X, q.Y)).ToList()), 4));
                f.Image.Dispose();
            }
            if (!r.IsEmpty) RenderRegion(r);
        }

        // ------------------------------------------------------------------ commands

        void SelectAll()
        {
            if (active) End();
            PutDown();
            SetTool(EditorTool.Select);
            var c = new[] { map.ToTarget(0, 0), map.ToTarget(Document.CanvasWidth, 0), map.ToTarget(Document.CanvasWidth, Document.CanvasHeight), map.ToTarget(0, Document.CanvasHeight) };
            Lift(c.Select(q => new PointF(Math.Max(0, Math.Min(Monitor.Width, q.X)), Math.Max(0, Math.Min(Monitor.Height, q.Y)))).ToArray());
            ShowSelectionBar();
        }

        void DeleteSelection()
        {
            var f = floating;
            if (f == null) return;
            HideSelectionBar();
            floating = null;
            Rectangle r = overlay;
            overlay = Rectangle.Empty;
            if (f.Source != null)
            {
                var erase = InkStroke.EraseArea(NewId(), author, DateTime.UtcNow.Ticks, f.Source);
                Document.Add(erase);
                var action = new UndoAction();
                action.Added.Add(erase.Id);
                undo.Push(action);
                redo.Clear();
                r = Rectangle.Union(r, TargetRect(erase.Bounds, 4));
            }
            f.Image.Dispose();
            RenderRegion(r);
            RefreshToolbar();
        }

        void CopySelection()
        {
            if (floating == null) return;
            PointF[] box = floating.Box();
            Rectangle area = Rectangle.Ceiling(OutlineBounds(box));
            using (var bmp = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    InkImage.DrawOn(g, floating.Image, floating.ImageCorners().Select(q => new PointF(q.X - area.X, q.Y - area.Y)).ToArray(), true);
                PutOnClipboard(bmp);
            }
            if (toolbar != null && selectItem != null) toolbar.ShowMessage(selectItem, "Copied: paste with Ctrl+V (here or in other apps)");
        }

        // PNG (keeps transparency, for apps that read it) plus a plain bitmap on white for the rest.
        static void PutOnClipboard(Bitmap bmp)
        {
            try
            {
                var data = new DataObject();
                using (var ms = new MemoryStream())
                using (var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                {
                    bmp.Save(ms, ImageFormat.Png);
                    data.SetData("PNG", false, ms);
                    using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImageUnscaled(bmp, 0, 0); }
                    data.SetData(DataFormats.Bitmap, true, flat);
                    Clipboard.SetDataObject(data, true, 5, 100);   // copied now: nothing of ours stays referenced
                }
            }
            catch (Exception ex) { Log.Warn("Copy to the clipboard failed: " + ex.Message); }
        }

        void CutSelection()
        {
            if (floating == null) return;
            CopySelection();
            DeleteSelection();
        }

        // A picture from the clipboard (copied here, in another app, or a picture file in File Explorer) floats in the
        // middle, ready to place. Ctrl+V works with every tool.
        void Paste()
        {
            // Up to twice the screen: sharp when enlarged a little, without holding a whole camera photo in memory.
            string problem;
            Bitmap img = InkImage.FromClipboard(Monitor.Width * 2, Monitor.Height * 2, out problem);
            if (img == null)
            {
                if (toolbar != null && selectItem != null)
                    toolbar.ShowMessage(selectItem, problem ?? "Nothing to paste: copy a picture, a picture file or part of a drawing first");
                return;
            }
            if (active) End();
            PutDown();
            SetTool(EditorTool.Select);
            float k = Math.Min(1, Math.Min(Monitor.Width * 0.7f / img.Width, Monitor.Height * 0.7f / img.Height));
            floating = new Floating { Image = img, Sx = k, Sy = k, Changed = true, Center = new PointF(Monitor.Width / 2f, Monitor.Height / 2f) };
            DrawSelection();
            ShowSelectionBar();
        }

        void RotateSelection(float degrees)
        {
            if (floating == null) return;
            floating.Angle = (float)Math.Round((floating.Angle + degrees) / 90) * 90;
            floating.Changed = true;
            DrawSelection();
            ShowSelectionBar();
        }

        void FlipSelection(bool horizontal)
        {
            if (floating == null) return;
            if (horizontal) floating.FlipX = !floating.FlipX; else floating.FlipY = !floating.FlipY;
            floating.Changed = true;
            DrawSelection();
        }

        void Nudge(int dx, int dy)
        {
            if (floating == null) return;
            floating.Center = new PointF(floating.Center.X + dx, floating.Center.Y + dy);
            floating.Changed = true;
            HideSelectionBar();
            DrawSelection();
            ShowSelectionBar();
        }

        // ------------------------------------------------------------------ drawing the selection

        // The floating picture, its box and handles (or the outline being dragged), over the composed drawing.
        void DrawSelection()
        {
            Rectangle now = Rectangle.Empty;
            if (floating != null)
            {
                var pts = new List<PointF>(floating.Box());
                pts.Add(floating.ToScreen(0, -floating.H / 2 - RotateReach));
                now = Rectangle.Inflate(Rectangle.Round(OutlineBounds(pts)), (int)(HandleSize * 2) + 4, (int)(HandleSize * 2) + 4);
            }
            else if (marquee != null) now = Rectangle.Inflate(Rectangle.Round(OutlineBounds(marquee)), 4, 4);
            Rectangle dirty = Rectangle.Intersect(now.IsEmpty ? overlay : overlay.IsEmpty ? now : Rectangle.Union(overlay, now), ClientArea);
            overlay = now;
            if (dirty.Width <= 0 || dirty.Height <= 0) return;
            using (var g = frame.CreateGraphics())
            {
                CopyRect(baseLayer, g, dirty);
                g.SetClip(dirty);
                if (floating != null)
                {
                    InkImage.DrawOn(g, floating.Image, floating.ImageCorners(), drag == DragKind.None);
                    PointF[] box = floating.Box();
                    DashedPolygon(g, box);
                    PointF top = floating.ToScreen(0, -floating.H / 2), knob = floating.ToScreen(0, -floating.H / 2 - RotateReach);
                    using (var p = new Pen(Color.FromArgb(230, 26, 115, 232), Math.Max(1, dpiScale))) g.DrawLine(p, top, knob);
                    for (int y = -1; y <= 1; y++)
                        for (int x = -1; x <= 1; x++)
                            if (x != 0 || y != 0) DrawHandle(g, floating.ToScreen(x * floating.W / 2, y * floating.H / 2), false);
                    DrawHandle(g, knob, true);
                }
                else if (marquee != null && marquee.Count > 1)
                {
                    if (selectShape == SelectShape.Lasso && drag == DragKind.Marquee) DashedLine(g, marquee.ToArray());
                    else DashedPolygon(g, marquee.ToArray());
                }
            }
            frame.Present(Handle, Monitor.Location, dirty);
        }

        void DashedPolygon(Graphics g, PointF[] pts)
        {
            using (var white = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1, 1.5f * dpiScale)))
            using (var dark = new Pen(Color.FromArgb(230, 32, 33, 36), Math.Max(1, 1.5f * dpiScale)) { DashPattern = new[] { 4f, 4f } })
            {
                g.DrawPolygon(white, pts);
                g.DrawPolygon(dark, pts);
            }
        }

        void DashedLine(Graphics g, PointF[] pts)
        {
            using (var white = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1, 1.5f * dpiScale)))
            using (var dark = new Pen(Color.FromArgb(230, 32, 33, 36), Math.Max(1, 1.5f * dpiScale)) { DashPattern = new[] { 4f, 4f } })
            {
                g.DrawLines(white, pts);
                g.DrawLines(dark, pts);
            }
        }

        void DrawHandle(Graphics g, PointF c, bool round)
        {
            float s = HandleSize;
            var r = new RectangleF(c.X - s / 2, c.Y - s / 2, s, s);
            using (var b = new SolidBrush(Color.White))
            using (var p = new Pen(Color.FromArgb(255, 26, 115, 232), Math.Max(1, 1.5f * dpiScale)))
            {
                if (round) { g.FillEllipse(b, r); g.DrawEllipse(p, r); }
                else { g.FillRectangle(b, r); g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height); }
            }
        }

        // ------------------------------------------------------------------ the bar under a selection

        void ShowSelectionBar()
        {
            HideSelectionBar();
            if (floating == null || toolbar == null) return;
            var items = new List<InkToolbar.Item>
            {
                InkToolbar.Item.Button("\uE8C6", "X", "Cut (Ctrl+X)", CutSelection, null),
                InkToolbar.Item.Button("\uE8C8", "C", "Copy (Ctrl+C)", CopySelection, null),
                InkToolbar.Item.Button("\uE74D", "D", "Delete (Delete)", DeleteSelection, null),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Button("\uE80C", "L", "Rotate left", () => RotateSelection(-90), null),
                InkToolbar.Item.Button("\uE7AD", "R", "Rotate right (or drag the round handle; Shift snaps)", () => RotateSelection(90), null),
                InkToolbar.Item.Button("\uE8AB", "H", "Flip left-right", () => FlipSelection(true), null),
                InkToolbar.Item.Button("\uE8CB", "V", "Flip upside down", () => FlipSelection(false), null),
                InkToolbar.Item.Separator(),
                InkToolbar.Item.Accent("\uE73E", "OK", "Put it down (Enter)", PutDown)
            };
            selectionBar = new InkToolbar(this, Monitor, items, dpiScale, Rectangle.Empty, true);
            RectangleF b = OutlineBounds(floating.Box());
            int gap = (int)(14 * dpiScale);
            int x = (int)(b.Left + b.Width / 2 - selectionBar.Width / 2), y = (int)b.Bottom + gap + (int)HandleSize;
            if (y + selectionBar.Height > Monitor.Height - gap) y = (int)(b.Top - RotateReach - HandleSize - gap - selectionBar.Height);
            x = Math.Max(gap, Math.Min(Monitor.Width - selectionBar.Width - gap, x));
            y = Math.Max(gap, Math.Min(Monitor.Height - selectionBar.Height - gap, y));
            selectionBar.Location = new Point(Monitor.Left + x, Monitor.Top + y);
            selectionBar.Show(this);
        }

        void HideSelectionBar()
        {
            if (selectionBar != null && !selectionBar.IsDisposed) selectionBar.Close();
            selectionBar = null;
        }

        // Toolbar icon: a dashed rectangle or a dashed loop.
        static void DrawSelectIcon(Graphics g, RectangleF r, Color fg, bool lasso)
        {
            using (var p = new Pen(fg, Math.Max(1.4f, r.Width / 12)) { DashPattern = new[] { 2f, 1.6f } })
            {
                if (!lasso) { g.DrawRectangle(p, r.X + 1, r.Y + 2, r.Width - 2, r.Height - 4); return; }
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.25f, r.Left + r.Width * 0.6f, r.Top - r.Height * 0.1f,
                                   r.Right + r.Width * 0.05f, r.Top + r.Height * 0.3f, r.Right - r.Width * 0.1f, r.Top + r.Height * 0.6f);
                    path.AddBezier(r.Right - r.Width * 0.1f, r.Top + r.Height * 0.6f, r.Left + r.Width * 0.7f, r.Bottom + r.Height * 0.1f,
                                   r.Left - r.Width * 0.05f, r.Bottom - r.Height * 0.1f, r.Left + r.Width * 0.2f, r.Top + r.Height * 0.25f);
                    g.DrawPath(p, path);
                }
            }
        }
    }
}
