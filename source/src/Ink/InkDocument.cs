using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveWall.Ink
{
    // Pen and Highlighter are freehand strokes; Line/Arrow/Rectangle/Ellipse are shapes between two points (optionally
    // filled); Fill is a paint-bucket region; Text is text, emoji, kaomoji or symbols at a point; Erase is an eraser path
    // that clears whatever was drawn before it (the partial eraser; filled = an area, e.g. where a selection was lifted
    // from); Image is a picture (a moved, resized, rotated or pasted part of a drawing) placed on a parallelogram.
    internal enum InkTool { Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Fill, Text, Erase, Image }

    internal struct InkPoint
    {
        public float X, Y;   // canvas units
        public byte P;       // pressure 0..255 (128 = "normal", used for mouse)
        public InkPoint(float x, float y, byte p) { X = x; Y = y; P = p; }
    }

    // One finished element (stroke, shape, fill or text). Elements never change after they are created; erasing is a
    // separate operation and a change (recolor, edited text) is an erase plus a new element. That makes the file an
    // append-only log and lets several people edit the same board later without conflicts (see InkDocument).
    internal sealed class InkStroke
    {
        public readonly string Id;
        public readonly string Author;
        public readonly long Ticks;          // UTC
        public readonly InkTool Tool;
        public readonly int Argb;
        public readonly float Width;         // canvas units at pressure 128; the font size for text
        public readonly InkPoint[] Points;   // freehand: the path; shapes: two corners; text: its top-left
        public readonly bool HasPressure;
        public readonly bool Filled;         // shapes
        public readonly InkFill.Mask Mask;   // fills
        public readonly string Text, Font, Effect;
        public readonly bool Bold, Italic;
        public readonly string Base;         // element this one replaces: it takes that one's place in the drawing order
        public readonly string Under;        // element this one is drawn just below (a fill under later highlighters and pens)
        public readonly string Brush;        // pens: InkBrush kind (null = the round pen)
        public readonly string Layer;        // layer id (null = the base layer)
        public readonly string Data;         // images: PNG, base64
        RectangleF bounds;
        bool boundsKnown;

        public InkStroke(string id, string author, long ticks, InkTool tool, int argb, float width, InkPoint[] points)
            : this(id, author, ticks, tool, argb, width, points, false, null, null, null, false, false, null, null, null) { }

        InkStroke(string id, string author, long ticks, InkTool tool, int argb, float width, InkPoint[] points, bool filled,
                  InkFill.Mask mask, string text, string font, bool bold, bool italic, string effect, string baseId, string data,
                  string under = null, string brush = null, string layer = null)
        {
            Id = id; Author = author; Ticks = ticks; Tool = tool; Argb = argb; Width = Math.Max(0.5f, width);
            Points = points ?? new InkPoint[0];
            Filled = filled; Mask = mask; Text = text; Font = font; Bold = bold; Italic = italic; Effect = effect; Base = baseId; Data = data;
            Under = under;
            Brush = tool == InkTool.Pen && !string.IsNullOrEmpty(brush) ? brush : null;
            Layer = string.IsNullOrEmpty(layer) || layer == InkDocument.BaseLayer ? null : layer;
            if (tool == InkTool.Pen || tool == InkTool.Highlighter)
            {
                byte first = Points.Length > 0 ? Points[0].P : (byte)128;
                foreach (var p in Points) if (p.P != first) HasPressure = true;
            }
        }

        // A freehand pen (with a brush, or null for the round pen) or highlighter stroke.
        public static InkStroke Freehand(string id, string author, long ticks, InkTool tool, int argb, float width, InkPoint[] points, string brush)
        {
            return new InkStroke(id, author, ticks, tool, argb, width, points, false, null, null, null, false, false, null, null, null, null, brush);
        }

        public static InkStroke Shape(string id, string author, long ticks, InkTool tool, int argb, float width, InkPoint a, InkPoint b, bool filled)
        {
            return new InkStroke(id, author, ticks, tool, argb, width, new[] { a, b }, filled, null, null, null, false, false, null, null, null);
        }

        // `under`: an element to draw it just below (null = on top).
        public static InkStroke FillRegion(string id, string author, long ticks, int argb, InkFill.Mask mask, string under = null)
        {
            return new InkStroke(id, author, ticks, InkTool.Fill, argb, 1, new[] { new InkPoint(mask.Left, mask.Top, 128) }, false, mask,
                                 null, null, false, false, null, null, null, under);
        }

        // `replaces`: the text this edits (the new one takes its place in the drawing order).
        public static InkStroke TextItem(string id, string author, long ticks, int argb, float size, PointF at, string text, string font,
                                         bool bold, bool italic, string effect, string replaces = null)
        {
            return new InkStroke(id, author, ticks, InkTool.Text, argb, size, new[] { new InkPoint(at.X, at.Y, 128) }, false, null, text,
                                 font, bold, italic, effect, replaces, null);
        }

        // The same element in another color (a new element in the same place: see the class comment).
        public InkStroke Recolored(string id, string author, long ticks, int argb)
        {
            return new InkStroke(id, author, ticks, Tool, argb, Width, Points, Filled, Mask, Text, Font, Bold, Italic, Effect, Id, Data, null, Brush, Layer);
        }

        InkStroke With(string baseId, string under, string brush, string layer)
        {
            return baseId == null && under == null && brush == null && layer == null ? this
                : new InkStroke(Id, Author, Ticks, Tool, Argb, Width, Points, Filled, Mask, Text, Font, Bold, Italic, Effect, baseId, Data, under, brush, layer);
        }

        // The same element on a layer (the editor puts new elements on the active one).
        public InkStroke InLayer(string layer)
        {
            string l = string.IsNullOrEmpty(layer) || layer == InkDocument.BaseLayer ? null : layer;
            return l == Layer ? this : new InkStroke(Id, Author, Ticks, Tool, Argb, Width, Points, Filled, Mask, Text, Font, Bold, Italic, Effect, Base, Data, Under, Brush, l);
        }

        // Clears the area inside the outline (canvas units).
        public static InkStroke EraseArea(string id, string author, long ticks, InkPoint[] outline)
        {
            return new InkStroke(id, author, ticks, InkTool.Erase, 0, 1, outline, true, null, null, null, false, false, null, null, null);
        }

        // A picture drawn onto the parallelogram topLeft, topRight, bottomLeft (canvas units).
        public static InkStroke ImageItem(string id, string author, long ticks, PointF topLeft, PointF topRight, PointF bottomLeft, string pngBase64)
        {
            var pts = new[] { new InkPoint(topLeft.X, topLeft.Y, 128), new InkPoint(topRight.X, topRight.Y, 128), new InkPoint(bottomLeft.X, bottomLeft.Y, 128) };
            return new InkStroke(id, author, ticks, InkTool.Image, 0, 1, pts, false, null, null, null, false, false, null, null, pngBase64);
        }

        // Images: the fourth corner.
        public PointF BottomRight
        {
            get { return Points.Length < 3 ? PointF.Empty : new PointF(Points[1].X + Points[2].X - Points[0].X, Points[1].Y + Points[2].Y - Points[0].Y); }
        }

        public bool IsShape { get { return Tool == InkTool.Line || Tool == InkTool.Arrow || Tool == InkTool.Rectangle || Tool == InkTool.Ellipse; } }

        // Canvas units, including the stroke's thickness (text: its ink, measured once).
        public RectangleF Bounds
        {
            get
            {
                if (!boundsKnown) { bounds = ComputeBounds(); boundsKnown = true; }
                return bounds;
            }
        }

        RectangleF ComputeBounds()
        {
            if (Tool == InkTool.Fill) return Mask == null ? RectangleF.Empty : new RectangleF(Mask.Left, Mask.Top, Mask.Width, Mask.Height);
            if (Tool == InkTool.Text) return RectangleF.Inflate(InkText.Measure(this), 2, 2);
            if (Tool == InkTool.Image && Points.Length >= 3)
            {
                PointF d = BottomRight;
                float x0 = Math.Min(Math.Min(Points[0].X, Points[1].X), Math.Min(Points[2].X, d.X)), x1 = Math.Max(Math.Max(Points[0].X, Points[1].X), Math.Max(Points[2].X, d.X));
                float y0 = Math.Min(Math.Min(Points[0].Y, Points[1].Y), Math.Min(Points[2].Y, d.Y)), y1 = Math.Max(Math.Max(Points[0].Y, Points[1].Y), Math.Max(Points[2].Y, d.Y));
                return RectangleF.FromLTRB(x0 - 2, y0 - 2, x1 + 2, y1 + 2);
            }
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            if (Points.Length == 0) { minX = minY = maxX = maxY = 0; }
            float pad = (Tool == InkTool.Arrow ? InkRenderer.ArrowHead(Width) : InkRenderer.MaxWidth(this) / 2) + 1;
            return RectangleF.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
        }

        // True when a circle of radius r (canvas units) at (x, y) touches the element.
        public bool HitTest(float x, float y, float r)
        {
            RectangleF b = Bounds;
            if (x < b.Left - r || x > b.Right + r || y < b.Top - r || y > b.Bottom + r) return false;
            float reach = r + InkRenderer.MaxWidth(this) / 2;
            float reach2 = reach * reach;
            switch (Tool)
            {
                case InkTool.Erase: return false;   // not something to erase, recolor or pick
                case InkTool.Image:
                {
                    if (Points.Length < 3) return false;
                    // In the parallelogram (grown by r): the point's coordinates along its two sides.
                    float ux = Points[1].X - Points[0].X, uy = Points[1].Y - Points[0].Y, vx = Points[2].X - Points[0].X, vy = Points[2].Y - Points[0].Y;
                    float det = ux * vy - uy * vx;
                    if (Math.Abs(det) < 0.001f) return false;
                    float px = x - Points[0].X, py = y - Points[0].Y;
                    float along = (px * vy - py * vx) / det, down = (ux * py - uy * px) / det;
                    float ea = r / Math.Max(1, (float)Math.Sqrt(ux * ux + uy * uy)), eb = r / Math.Max(1, (float)Math.Sqrt(vx * vx + vy * vy));
                    return along >= -ea && along <= 1 + ea && down >= -eb && down <= 1 + eb;
                }
                case InkTool.Text: return true;
                case InkTool.Fill:
                    if (Mask.Contains(x, y)) return true;
                    for (int i = 0; i < 8; i++)
                        if (Mask.Contains(x + r * (float)Math.Cos(i * Math.PI / 4), y + r * (float)Math.Sin(i * Math.PI / 4))) return true;
                    return false;
                case InkTool.Rectangle:
                {
                    RectangleF rc = ShapeRect;
                    if (Filled) return RectangleF.Inflate(rc, reach, reach).Contains(x, y);
                    var c = new[] { new InkPoint(rc.Left, rc.Top, 0), new InkPoint(rc.Right, rc.Top, 0), new InkPoint(rc.Right, rc.Bottom, 0),
                                    new InkPoint(rc.Left, rc.Bottom, 0), new InkPoint(rc.Left, rc.Top, 0) };
                    for (int i = 1; i < c.Length; i++) if (SegmentDist2(x, y, c[i - 1], c[i]) <= reach2) return true;
                    return false;
                }
                case InkTool.Ellipse:
                {
                    RectangleF rc = ShapeRect;
                    float rx = Math.Max(0.5f, rc.Width / 2), ry = Math.Max(0.5f, rc.Height / 2), cx = rc.Left + rx, cy = rc.Top + ry;
                    if (Filled)
                    {
                        float dx = (x - cx) / (rx + reach), dy = (y - cy) / (ry + reach);
                        return dx * dx + dy * dy <= 1;
                    }
                    InkPoint prev = new InkPoint(cx + rx, cy, 0);
                    for (int i = 1; i <= 64; i++)
                    {
                        double a = i * Math.PI / 32;
                        var pt = new InkPoint(cx + rx * (float)Math.Cos(a), cy + ry * (float)Math.Sin(a), 0);
                        if (SegmentDist2(x, y, prev, pt) <= reach2) return true;
                        prev = pt;
                    }
                    return false;
                }
                case InkTool.Arrow:
                {
                    if (Points.Length < 2) return false;
                    if (SegmentDist2(x, y, Points[0], Points[1]) <= reach2) return true;
                    PointF[] head = InkRenderer.ArrowHeadPoints(Points[0], Points[1], Width);
                    return SegmentDist2(x, y, Pt(head[0]), Pt(head[1])) <= reach2 || SegmentDist2(x, y, Pt(head[1]), Pt(head[2])) <= reach2
                        || SegmentDist2(x, y, Pt(head[2]), Pt(head[0])) <= reach2;
                }
            }
            if (Points.Length == 1) return Dist2(x, y, Points[0].X, Points[0].Y) <= reach2;
            for (int i = 1; i < Points.Length; i++)
                if (SegmentDist2(x, y, Points[i - 1], Points[i]) <= reach2) return true;
            return false;
        }

        // Shapes: the rectangle between the two corners.
        public RectangleF ShapeRect
        {
            get
            {
                if (Points.Length < 2) return RectangleF.Empty;
                return RectangleF.FromLTRB(Math.Min(Points[0].X, Points[1].X), Math.Min(Points[0].Y, Points[1].Y),
                                           Math.Max(Points[0].X, Points[1].X), Math.Max(Points[0].Y, Points[1].Y));
            }
        }

        static InkPoint Pt(PointF p) { return new InkPoint(p.X, p.Y, 0); }

        static float Dist2(float ax, float ay, float bx, float by) { float dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }

        static float SegmentDist2(float px, float py, InkPoint a, InkPoint b)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 0.0001f ? 0 : ((px - a.X) * vx + (py - a.Y) * vy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return Dist2(px, py, a.X + vx * t, a.Y + vy * t);
        }

        static readonly string[] ToolNames = { "pen", "hl", "line", "arrow", "rect", "ellipse", "fill", "text", "erase", "image" };

        internal string Serialize()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(32 + Points.Length * 12);
            sb.Append("+ ").Append(Id).Append(' ').Append(Author).Append(' ').Append(Ticks.ToString(ci)).Append(' ')
              .Append(ToolNames[(int)Tool]).Append(Filled ? "+fill" : "").Append(' ').Append(Argb.ToString("X8", ci)).Append(' ')
              .Append(Width.ToString("0.##", ci)).Append(' ');
            if (Tool == InkTool.Fill) sb.Append(Mask.Serialize());
            else
                for (int i = 0; i < Points.Length; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(((int)Math.Round(Points[i].X)).ToString(ci)).Append(',')
                      .Append(((int)Math.Round(Points[i].Y)).ToString(ci)).Append(',')
                      .Append(Points[i].P.ToString(ci));
                }
            if (Tool == InkTool.Text)
            {
                sb.Append(" font=").Append(Uri.EscapeDataString(Font ?? "Segoe UI"))
                  .Append(" b=").Append(Bold ? '1' : '0').Append(" i=").Append(Italic ? '1' : '0')
                  .Append(" fx=").Append(Effect ?? InkText.Plain)
                  .Append(" t=").Append(Uri.EscapeDataString(Text ?? ""));
            }
            if (Tool == InkTool.Image) sb.Append(" img=").Append(Data ?? "");
            if (Base != null) sb.Append(" z=").Append(Base);
            if (Under != null) sb.Append(" under=").Append(Under);
            if (Brush != null) sb.Append(" brush=").Append(Brush);
            if (Layer != null) sb.Append(" layer=").Append(Layer);
            return sb.ToString();
        }

        internal static InkStroke Parse(string[] f)
        {
            InkStroke s = ParseElement(f);
            if (s == null) return null;
            string baseId = null, under = null, brush = null, layer = null;
            for (int i = 8; i < f.Length; i++)
            {
                if (f[i].StartsWith("z=")) baseId = f[i].Substring(2);
                else if (f[i].StartsWith("under=")) under = f[i].Substring(6);
                else if (f[i].StartsWith("brush=")) brush = f[i].Substring(6);
                else if (f[i].StartsWith("layer=")) layer = f[i].Substring(6);
            }
            return s.With(baseId, under, brush, layer);
        }

        static InkStroke ParseElement(string[] f)
        {
            // + id author ticks tool argb width points [key=value ...]
            if (f.Length < 8) return null;
            var ci = CultureInfo.InvariantCulture;
            long ticks; int argb; float width;
            if (!long.TryParse(f[3], NumberStyles.Integer, ci, out ticks)) return null;
            if (!int.TryParse(f[5], NumberStyles.HexNumber, ci, out argb)) return null;
            if (!float.TryParse(f[6], NumberStyles.Float, ci, out width)) return null;
            bool filled = f[4].EndsWith("+fill");
            int ti = Array.IndexOf(ToolNames, filled ? f[4].Substring(0, f[4].Length - 5) : f[4]);
            if (ti < 0) return null;   // from a newer version
            var tool = (InkTool)ti;
            if (tool == InkTool.Fill)
            {
                var mask = InkFill.Mask.Parse(f[7]);
                return mask == null ? null : FillRegion(f[1], f[2], ticks, argb, mask);
            }
            var pts = new List<InkPoint>();
            foreach (string t in f[7].Split(';'))
            {
                string[] c = t.Split(',');
                int x, y, p;
                if (c.Length < 2 || !int.TryParse(c[0], NumberStyles.Integer, ci, out x) || !int.TryParse(c[1], NumberStyles.Integer, ci, out y)) continue;
                if (c.Length < 3 || !int.TryParse(c[2], NumberStyles.Integer, ci, out p)) p = 128;
                pts.Add(new InkPoint(x, y, (byte)Math.Max(0, Math.Min(255, p))));
            }
            if (pts.Count == 0) return null;
            if (tool == InkTool.Text)
            {
                string text = null, font = "Segoe UI", fx = InkText.Plain;
                bool bold = false, italic = false;
                for (int i = 8; i < f.Length; i++)
                {
                    int eq = f[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string k = f[i].Substring(0, eq), v = f[i].Substring(eq + 1);
                    if (k == "t") text = Uri.UnescapeDataString(v);
                    else if (k == "font") font = Uri.UnescapeDataString(v);
                    else if (k == "b") bold = v == "1";
                    else if (k == "i") italic = v == "1";
                    else if (k == "fx") fx = v;
                }
                if (string.IsNullOrEmpty(text)) return null;
                return TextItem(f[1], f[2], ticks, argb, width, new PointF(pts[0].X, pts[0].Y), text, font, bold, italic, fx);
            }
            if (tool == InkTool.Image)
            {
                string img = null;
                for (int i = 8; i < f.Length; i++) if (f[i].StartsWith("img=")) img = f[i].Substring(4);
                if (pts.Count < 3 || string.IsNullOrEmpty(img)) return null;
                return ImageItem(f[1], f[2], ticks, new PointF(pts[0].X, pts[0].Y), new PointF(pts[1].X, pts[1].Y), new PointF(pts[2].X, pts[2].Y), img);
            }
            if (tool == InkTool.Erase && filled) return pts.Count < 3 ? null : EraseArea(f[1], f[2], ticks, pts.ToArray());
            if (tool != InkTool.Pen && tool != InkTool.Highlighter && tool != InkTool.Erase)
                return pts.Count < 2 ? null : Shape(f[1], f[2], ticks, tool, argb, width, pts[0], pts[1], filled);
            return new InkStroke(f[1], f[2], ticks, tool, argb, width, pts.ToArray());
        }
    }

    // One layer of a board (see InkDocument's L records).
    internal sealed class InkLayerInfo
    {
        public string Id, Name;
        public double Order;          // bottom to top
        public bool Visible = true, Locked, Deleted;
        public int Opacity = 100;     // percent

        public InkLayerInfo Clone() { return (InkLayerInfo)MemberwiseClone(); }
    }

    // A drawing: a board, or the drawings on one wallpaper.
    //
    // Stored as a UTF-8 text file that is only ever appended to, one operation per line:
    //   LWINK 1                         header
    //   canvas <w> <h>                  size of the canvas the coordinates refer to
    //   title <text>                    informational (the wallpaper's path, or the board's name)
    //   bg <style> <author> <ticks>     background style (last one wins)
    //   + <id> <author> <ticks> <tool> <argb> <width> <x,y,p;x,y,p;...>    add a stroke
    //       tool: pen, hl; line, arrow, rect, ellipse (two points, "+fill" = filled); fill (<width> unused, points =
    //       the region, see InkFill.Mask); text (width = font size, one point, then font= b= i= fx= t= escaped fields);
    //       erase (width = eraser diameter; clears everything drawn before it along its path; "+fill" = the area inside);
    //       image (three corners: top-left, top-right, bottom-left; img=<PNG in base64>)
    //       optional z=<id>: replaces that element and takes its place in the drawing order (recolor, edited text)
    //       optional brush=<kind>: pens only, see InkBrush (soft, spray, pencil, marker, calligraphy, chalk, crayon, neon,
    //       dashed); older versions ignore it and draw a round pen
    //       optional under=<id>: drawn just below that element (a fill goes under the highlighters and pens drawn after
    //       the last fill or eraser it overlaps); older versions ignore it and draw the element on top
    //       optional layer=<id>: the layer it is on (none = the base layer)
    //   - <id> <author> <ticks>         erase a stroke
    //   ~ <id> <author> <ticks>         restore an erased stroke (undo of an erase)
    //   L <id> <author> <ticks> <what> [value]    a layer (boards): new <name>, name <name> (escaped), order <number>
    //       (bottom to top), show 0|1, opacity <0-100>, lock 0|1, delete, restore. The last record of each kind wins.
    //       The base layer ("base") always exists unless deleted; older versions ignore these lines and elements'
    //       layer=, and show every layer flattened.
    // Each stroke has a globally unique id and never changes, so logs from several people can simply be merged.
    internal sealed class InkDocument
    {
        public const string NoBackground = "none";

        public readonly string FilePath;
        public int CanvasWidth { get; private set; }
        public int CanvasHeight { get; private set; }
        public string Background { get; private set; }
        public string Title { get; private set; }
        public int Revision { get; private set; }     // changes whenever the visible content changes

        readonly List<InkStroke> strokes = new List<InkStroke>();
        readonly Dictionary<string, InkStroke> byId = new Dictionary<string, InkStroke>();
        readonly HashSet<string> erased = new HashSet<string>();
        readonly Dictionary<string, InkLayerInfo> layers = new Dictionary<string, InkLayerInfo>();
        bool onDisk;

        public const string BaseLayer = "base";
        public const int MaxLayers = 8;

        InkDocument(string path, int w, int h, string background, string title)
        {
            FilePath = path;
            CanvasWidth = Math.Max(16, w);
            CanvasHeight = Math.Max(16, h);
            Background = background ?? NoBackground;
            Title = title ?? "";
            layers[BaseLayer] = new InkLayerInfo { Id = BaseLayer, Name = "Layer 1" };
        }

        public bool Exists { get { return onDisk; } }

        // Opens the drawing at `path`, or starts a new (in-memory) one that is written on the first change.
        public static InkDocument Open(string path, int canvasW, int canvasH, string background, string title)
        {
            var doc = new InkDocument(path, canvasW, canvasH, background, title);
            if (!File.Exists(path)) return doc;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string[] f = line.Split(' ');
                    switch (f[0])
                    {
                        case "canvas":
                        {
                            int w, h;
                            if (f.Length >= 3 && int.TryParse(f[1], out w) && int.TryParse(f[2], out h) && w > 0 && h > 0)
                            { doc.CanvasWidth = w; doc.CanvasHeight = h; }
                            break;
                        }
                        case "title": doc.Title = line.Length > 6 ? line.Substring(6) : ""; break;
                        case "bg": if (f.Length >= 2) doc.Background = f[1]; break;
                        case "+":
                        {
                            var s = InkStroke.Parse(f);
                            if (s != null && !doc.byId.ContainsKey(s.Id)) doc.Insert(s);
                            break;
                        }
                        case "-": if (f.Length >= 2) doc.erased.Add(f[1]); break;
                        case "~": if (f.Length >= 2) doc.erased.Remove(f[1]); break;
                        case "L": if (f.Length >= 5) doc.ApplyLayerRecord(f[1], f[4], f.Length > 5 ? f[5] : ""); break;
                    }
                }
                doc.onDisk = true;
            }
            catch (Exception ex) { Log.Error("Could not read drawing " + path, ex); }
            return doc;
        }

        // In drawing order: after the element it replaces (so erasing drawn later still applies to it), just below the one
        // it goes under, else on top.
        void Insert(InkStroke s)
        {
            InkStroke other;
            int at = strokes.Count;
            if (s.Base != null && byId.TryGetValue(s.Base, out other)) at = strokes.IndexOf(other) + 1;
            else if (s.Under != null && byId.TryGetValue(s.Under, out other)) at = strokes.IndexOf(other);
            strokes.Insert(at, s);
            if (s.Layer != null) LayerFor(s.Layer);
            byId[s.Id] = s;
        }

        // Not erased and not on a deleted layer (hidden layers included), in drawing order.
        public List<InkStroke> VisibleStrokes()
        {
            var list = new List<InkStroke>(strokes.Count);
            foreach (var s in strokes) if (!erased.Contains(s.Id) && !OnDeletedLayer(s)) list.Add(s);
            return list;
        }

        public int VisibleCount { get { return strokes.Count(s => !erased.Contains(s.Id) && !OnDeletedLayer(s)); } }

        bool OnDeletedLayer(InkStroke s)
        {
            InkLayerInfo l;
            return layers.TryGetValue(s.Layer ?? BaseLayer, out l) && l.Deleted;
        }

        // ------------------------------------------------------------------ layers

        public static string LayerOf(InkStroke s) { return s.Layer ?? BaseLayer; }

        // Bottom to top, without deleted ones.
        public List<InkLayerInfo> Layers
        {
            get { return layers.Values.Where(l => !l.Deleted).OrderBy(l => l.Order).ThenBy(l => l.Id, StringComparer.Ordinal).ToList(); }
        }

        public InkLayerInfo Layer(string id)
        {
            InkLayerInfo l;
            return id != null && layers.TryGetValue(id, out l) ? l : null;
        }

        // A single visible layer at full strength: drawn exactly as before there were layers.
        public bool IsFlat
        {
            get { var ls = Layers; return ls.Count == 1 && ls[0].Visible && ls[0].Opacity >= 100; }
        }

        // What a picture of the drawing shows: the shown layers, bottom to top, each with its elements.
        public List<KeyValuePair<InkLayerInfo, List<InkStroke>>> Snapshot()
        {
            var all = VisibleStrokes();
            var list = new List<KeyValuePair<InkLayerInfo, List<InkStroke>>>();
            foreach (var l in Layers)
                if (l.Visible && l.Opacity > 0) list.Add(new KeyValuePair<InkLayerInfo, List<InkStroke>>(l.Clone(), all.Where(s => LayerOf(s) == l.Id).ToList()));
            return list;
        }

        // The elements on shown layers, flattened (drawings on wallpapers, which don't use layers).
        public List<InkStroke> ShownStrokes()
        {
            return VisibleStrokes().Where(s => { var l = Layer(LayerOf(s)); return l == null || l.Visible; }).ToList();
        }

        public string AddLayer(string name, string author)
        {
            string id = Guid.NewGuid().ToString("N");
            var ci = CultureInfo.InvariantCulture;
            double order = layers.Values.Where(l => !l.Deleted).Select(l => l.Order).DefaultIfEmpty(0).Max() + 1;
            layers[id] = new InkLayerInfo { Id = id, Name = name, Order = order };
            Revision++;
            string stamp = " " + Safe(author) + " " + DateTime.UtcNow.Ticks.ToString(ci);
            Append(new[] { "L " + id + stamp + " new " + Uri.EscapeDataString(name), "L " + id + stamp + " order " + order.ToString("R", ci) });
            return id;
        }

        // `what`: name, order, show, opacity, lock, deleted (values as LayerValue gives them). Nothing is written when the
        // value doesn't change it.
        public void SetLayer(string id, string what, string value, string author)
        {
            var l = Layer(id);
            if (l == null || LayerValue(id, what) == value) return;
            string record = what == "deleted" ? (value == "1" ? "delete" : "restore") : what + " " + (what == "name" ? Uri.EscapeDataString(value) : value);
            ApplyLayerRecord(id, record.Split(' ')[0], record.IndexOf(' ') > 0 ? record.Substring(record.IndexOf(' ') + 1) : "");
            Revision++;
            Append(new[] { "L " + id + " " + Safe(author) + " " + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + " " + record });
        }

        public string LayerValue(string id, string what)
        {
            var l = Layer(id);
            if (l == null) return null;
            switch (what)
            {
                case "name": return l.Name;
                case "order": return l.Order.ToString("R", CultureInfo.InvariantCulture);
                case "show": return l.Visible ? "1" : "0";
                case "opacity": return l.Opacity.ToString(CultureInfo.InvariantCulture);
                case "lock": return l.Locked ? "1" : "0";
                case "deleted": return l.Deleted ? "1" : "0";
                default: return null;
            }
        }

        void ApplyLayerRecord(string id, string what, string value)
        {
            var ci = CultureInfo.InvariantCulture;
            InkLayerInfo l = LayerFor(id);
            switch (what)
            {
                case "new": case "name": l.Name = Uri.UnescapeDataString(value); break;
                case "order": { double o; if (double.TryParse(value, NumberStyles.Float, ci, out o)) l.Order = o; break; }
                case "show": l.Visible = value != "0"; break;
                case "opacity": { int o; if (int.TryParse(value, NumberStyles.Integer, ci, out o)) l.Opacity = Math.Max(0, Math.Min(100, o)); break; }
                case "lock": l.Locked = value == "1"; break;
                case "delete": l.Deleted = true; break;
                case "restore": l.Deleted = false; break;
            }
        }

        // A layer named by a record or an element before (or without) its "new" record: made up, on top.
        InkLayerInfo LayerFor(string id)
        {
            InkLayerInfo l;
            if (layers.TryGetValue(id, out l)) return l;
            l = new InkLayerInfo { Id = id, Name = "Layer", Order = layers.Values.Select(x => x.Order).DefaultIfEmpty(0).Max() + 1 };
            layers[id] = l;
            return l;
        }

        public bool IsVisible(string id) { return byId.ContainsKey(id) && !erased.Contains(id); }

        public void Add(InkStroke s)
        {
            if (byId.ContainsKey(s.Id)) return;
            Insert(s);
            Revision++;
            Append(new[] { s.Serialize() });
        }

        // Returns the ids that were actually visible and are now erased.
        public List<string> Erase(IEnumerable<string> ids, string author)
        {
            var done = new List<string>();
            var lines = new List<string>();
            string stamp = " " + Safe(author) + " " + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            foreach (string id in ids)
            {
                if (!byId.ContainsKey(id) || !erased.Add(id)) continue;
                done.Add(id);
                lines.Add("- " + id + stamp);
            }
            if (done.Count > 0) { Revision++; Append(lines); }
            return done;
        }

        public void Restore(IEnumerable<string> ids, string author)
        {
            var lines = new List<string>();
            string stamp = " " + Safe(author) + " " + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            foreach (string id in ids)
                if (erased.Remove(id)) lines.Add("~ " + id + stamp);
            if (lines.Count > 0) { Revision++; Append(lines); }
        }

        public void SetBackground(string style, string author)
        {
            if (style == Background) return;
            Background = style;
            Revision++;
            Append(new[] { "bg " + style + " " + Safe(author) + " " + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) });
        }

        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "me";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsWhiteSpace(c) || c == ';' || c == ',' ? '_' : c);
            return sb.ToString();
        }

        void Append(IEnumerable<string> lines)
        {
            try
            {
                var sb = new StringBuilder();
                if (!onDisk)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    sb.Append("LWINK 1\n");
                    sb.Append("canvas ").Append(CanvasWidth.ToString(CultureInfo.InvariantCulture)).Append(' ')
                      .Append(CanvasHeight.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    if (Title.Length > 0) sb.Append("title ").Append(Title.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                    if (Background != NoBackground) sb.Append("bg ").Append(Background).Append(" me 0\n");
                }
                foreach (string l in lines) sb.Append(l).Append('\n');
                File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                onDisk = true;
            }
            catch (Exception ex) { Log.Error("Could not save drawing " + FilePath, ex); }
        }
    }
}
