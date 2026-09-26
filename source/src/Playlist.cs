using System;
using System.Collections.Generic;
using System.IO;

namespace LiveWall
{
    internal enum MediaKind { Image, Video, Gif }

    internal sealed class MediaItem
    {
        public readonly string Path;
        public readonly MediaKind Kind;
        public bool Failed;          // could not be played this session; skipped by the slideshow
        public bool StaticGif;       // a .gif with a single frame: shown like a picture
        string cacheKey;

        public MediaItem(string path, MediaKind kind) { Path = path; Kind = kind; }

        public string Name { get { return System.IO.Path.GetFileName(Path); } }

        // Changes whenever the file is replaced or edited, so cached conversions stay correct.
        public string CacheKey
        {
            get
            {
                if (cacheKey == null)
                {
                    var fi = new FileInfo(Path);
                    string id = "v2|" + Path.ToLowerInvariant() + "|" + (fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "missing");
                    cacheKey = AppPaths.ShortHash(id);
                }
                return cacheKey;
            }
        }

        public string ConvertedVideoPath { get { return System.IO.Path.Combine(AppPaths.CacheDir, CacheKey + ".mp4"); } }
        public string SnapshotPath { get { return System.IO.Path.Combine(AppPaths.CacheDir, CacheKey + ".jpg"); } }
    }

    internal static class MediaTypes
    {
        static readonly HashSet<string> images = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".bmp", ".dib", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp" };
        static readonly HashSet<string> videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".asf" };

        public const string DialogFilter =
            "Wallpapers (images, GIFs, videos)|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.tif;*.tiff;*.webp;*.heic;*.avif;*.gif;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg;*.ts;*.m2ts|" +
            "Videos|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg;*.ts;*.m2ts|GIFs|*.gif|" +
            "Images|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.tif;*.tiff;*.webp;*.heic;*.avif|All files|*.*";

        public static bool TryClassify(string path, out MediaKind kind)
        {
            string ext = Path.GetExtension(path);
            kind = MediaKind.Image;
            if (string.IsNullOrEmpty(ext)) return false;
            if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)) { kind = MediaKind.Gif; return true; }
            if (videos.Contains(ext)) { kind = MediaKind.Video; return true; }
            if (images.Contains(ext)) { kind = MediaKind.Image; return true; }
            return false;
        }

        public static string Describe(MediaKind k)
        {
            switch (k)
            {
                case MediaKind.Video: return "Video";
                case MediaKind.Gif: return "GIF";
                default: return "Image";
            }
        }
    }

    internal static class Playlist
    {
        const int MaxFilesPerFolder = 5000;

        // Expands folders (including subfolders) into media files, keeping the configured order.
        public static List<MediaItem> Resolve(IList<string> sources)
        {
            var result = new List<MediaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string src in sources)
            {
                try
                {
                    if (Directory.Exists(src))
                    {
                        var files = new List<string>();
                        CollectFiles(src, files);
                        files.Sort(NaturalCompare);
                        foreach (string f in files) Add(result, seen, f);
                    }
                    else if (File.Exists(src)) Add(result, seen, src);
                }
                catch (Exception ex) { Log.Warn("Could not read source " + src + ": " + ex.Message); }
            }
            return result;
        }

        static void CollectFiles(string dir, List<string> files)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && files.Count < MaxFilesPerFolder)
            {
                string d = stack.Pop();
                try
                {
                    foreach (string f in Directory.GetFiles(d))
                    {
                        MediaKind k;
                        if (MediaTypes.TryClassify(f, out k)) files.Add(f);
                    }
                    foreach (string sub in Directory.GetDirectories(d))
                    {
                        var attrs = File.GetAttributes(sub);
                        if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) == 0) stack.Push(sub);
                    }
                }
                catch { }
            }
        }

        static void Add(List<MediaItem> list, HashSet<string> seen, string path)
        {
            MediaKind kind;
            if (!MediaTypes.TryClassify(path, out kind)) return;
            string full = Path.GetFullPath(path);
            if (seen.Add(full)) list.Add(new MediaItem(full, kind));
        }

        // "clip2" sorts before "clip10".
        public static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }
}
