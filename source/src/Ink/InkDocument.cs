using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveWall.Ink
{
    internal enum InkTool { Pen, Highlighter }

    internal struct InkPoint
    {
        public float X, Y;   // canvas units
        public byte P;       // pressure 0..255 (128 = "normal", used for mouse)
        public InkPoint(float x, float y, byte p) { X = x; Y = y; P = p; }
    }

    // One finished stroke. Strokes never change after they are created; erasing is a separate operation. That makes the
    // file an append-only log and lets several people edit the same board later without conflicts (see InkDocument).
    internal sealed class InkStroke
    {
        public readonly string Id;
        public readonly string Author;
        public readonly long Ticks;          // UTC
        public readonly InkTool Tool;
        public readonly int Argb;
        public readonly float Width;         // canvas units at pressure 128
        public readonly InkPoint[] Points;
        public readonly RectangleF Bounds;   // canvas units, including the stroke's thickness
        public readonly bool HasPressure;

        public InkStroke(string id, string author, long ticks, InkTool tool, int argb, float width, InkPoint[] points)
        {
            Id = id; Author = author; Ticks = ticks; Tool = tool; Argb = argb; Width = Math.Max(0.5f, width);
            Points = points ?? new InkPoint[0];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            byte first = Points.Length > 0 ? Points[0].P : (byte)128;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                if (p.P != first) HasPressure = true;
            }
            if (Points.Length == 0) { minX = minY = maxX = maxY = 0; }
            float pad = InkRenderer.MaxWidth(this) / 2 + 1;
            Bounds = RectangleF.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
        }

        // True when a circle of radius r (canvas units) at (x, y) touches the stroke.
        public bool HitTest(float x, float y, float r)
        {
            if (x < Bounds.Left - r || x > Bounds.Right + r || y < Bounds.Top - r || y > Bounds.Bottom + r) return false;
            float reach = r + InkRenderer.MaxWidth(this) / 2;
            float reach2 = reach * reach;
            if (Points.Length == 1) return Dist2(x, y, Points[0].X, Points[0].Y) <= reach2;
            for (int i = 1; i < Points.Length; i++)
                if (SegmentDist2(x, y, Points[i - 1], Points[i]) <= reach2) return true;
            return false;
        }

        static float Dist2(float ax, float ay, float bx, float by) { float dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }

        static float SegmentDist2(float px, float py, InkPoint a, InkPoint b)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 0.0001f ? 0 : ((px - a.X) * vx + (py - a.Y) * vy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return Dist2(px, py, a.X + vx * t, a.Y + vy * t);
        }

        internal string Serialize()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(32 + Points.Length * 12);
            sb.Append("+ ").Append(Id).Append(' ').Append(Author).Append(' ').Append(Ticks.ToString(ci)).Append(' ')
              .Append(Tool == InkTool.Highlighter ? "hl" : "pen").Append(' ').Append(Argb.ToString("X8", ci)).Append(' ')
              .Append(Width.ToString("0.##", ci)).Append(' ');
            for (int i = 0; i < Points.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(((int)Math.Round(Points[i].X)).ToString(ci)).Append(',')
                  .Append(((int)Math.Round(Points[i].Y)).ToString(ci)).Append(',')
                  .Append(Points[i].P.ToString(ci));
            }
            return sb.ToString();
        }

        internal static InkStroke Parse(string[] f)
        {
            // + id author ticks tool argb width points
            if (f.Length < 8) return null;
            var ci = CultureInfo.InvariantCulture;
            long ticks; int argb; float width;
            if (!long.TryParse(f[3], NumberStyles.Integer, ci, out ticks)) return null;
            if (!int.TryParse(f[5], NumberStyles.HexNumber, ci, out argb)) return null;
            if (!float.TryParse(f[6], NumberStyles.Float, ci, out width)) return null;
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
            return new InkStroke(f[1], f[2], ticks, f[4] == "hl" ? InkTool.Highlighter : InkTool.Pen, argb, width, pts.ToArray());
        }
    }

    // A drawing: a board, or the drawings on one wallpaper.
    //
    // Stored as a UTF-8 text file that is only ever appended to, one operation per line:
    //   LWINK 1                         header
    //   canvas <w> <h>                  size of the canvas the coordinates refer to
    //   title <text>                    informational (the wallpaper's path, or the board's name)
    //   bg <style> <author> <ticks>     background style (last one wins)
    //   + <id> <author> <ticks> <tool> <argb> <width> <x,y,p;x,y,p;...>    add a stroke
    //   - <id> <author> <ticks>         erase a stroke
    //   ~ <id> <author> <ticks>         restore an erased stroke (undo of an erase)
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
        bool onDisk;

        InkDocument(string path, int w, int h, string background, string title)
        {
            FilePath = path;
            CanvasWidth = Math.Max(16, w);
            CanvasHeight = Math.Max(16, h);
            Background = background ?? NoBackground;
            Title = title ?? "";
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
                            if (s != null && !doc.byId.ContainsKey(s.Id)) { doc.strokes.Add(s); doc.byId[s.Id] = s; }
                            break;
                        }
                        case "-": if (f.Length >= 2) doc.erased.Add(f[1]); break;
                        case "~": if (f.Length >= 2) doc.erased.Remove(f[1]); break;
                    }
                }
                doc.onDisk = true;
            }
            catch (Exception ex) { Log.Error("Could not read drawing " + path, ex); }
            return doc;
        }

        public List<InkStroke> VisibleStrokes()
        {
            var list = new List<InkStroke>(strokes.Count);
            foreach (var s in strokes) if (!erased.Contains(s.Id)) list.Add(s);
            return list;
        }

        public int VisibleCount { get { return strokes.Count(s => !erased.Contains(s.Id)); } }

        public bool IsVisible(string id) { return byId.ContainsKey(id) && !erased.Contains(id); }

        public void Add(InkStroke s)
        {
            if (byId.ContainsKey(s.Id)) return;
            strokes.Add(s);
            byId[s.Id] = s;
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
