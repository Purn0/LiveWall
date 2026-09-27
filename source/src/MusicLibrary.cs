using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiveWall
{
    // A music source as stored in settings.ini ("musicDefault=", "music.<wallpaper hash>="):
    //   none | random | theme | video | default (per wallpaper: use musicDefault) | custom|<file or folder>|<file or folder>...
    internal static class MusicSpec
    {
        public const string None = "none", Random = "random", Theme = "theme", Video = "video", Custom = "custom", Default = "default";
        public static readonly string[] Moods = { "calm", "energetic", "dark", "happy", "dreamy", "cozy" };

        public static string Kind(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return None;
            int bar = spec.IndexOf('|');
            string k = (bar < 0 ? spec : spec.Substring(0, bar)).Trim().ToLowerInvariant();
            return k == Random || k == Theme || k == Video || k == Custom || k == Default ? k : None;
        }

        // '|' cannot appear in Windows paths.
        public static List<string> CustomPaths(string spec)
        {
            return (spec ?? "").Split('|').Skip(1).Where(p => p.Trim().Length > 0).ToList();
        }

        public static string MakeCustom(IEnumerable<string> paths) { return Custom + "|" + string.Join("|", paths); }

        public static string Describe(string spec)
        {
            switch (Kind(spec))
            {
                case Random: return "Random from the music folder";
                case Theme: return "By theme (mood of the picture)";
                case Video: return "The video's own sound";
                case Default: return "Default";
                case Custom:
                {
                    var p = CustomPaths(spec);
                    if (p.Count == 0) return "Custom (nothing chosen)";
                    string first = Path.GetFileName(p[0].TrimEnd('\\'));
                    return "Custom: " + (first.Length > 0 ? first : p[0]) + (p.Count > 1 ? " (+" + (p.Count - 1) + ")" : "");
                }
                default: return "None";
            }
        }
    }

    // Finding music files (runs on the MediaWorker thread: folders can be large or on slow drives).
    internal static class MusicLibrary
    {
        const int MaxTracks = 20000;
        static readonly HashSet<string> audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus", ".aif", ".aiff", ".mka" };

        public const string DialogFilter =
            "Music|*.mp3;*.m4a;*.aac;*.wav;*.wma;*.flac;*.ogg;*.opus;*.aif;*.aiff;*.mka|Videos (their sound)|*.mp4;*.m4v;*.mov;*.wmv;*.mkv;*.webm|All files|*.*";

        public static bool IsAudio(string path) { return audio.Contains(Path.GetExtension(path) ?? ""); }

        // Audio files in a folder and its subfolders, sorted by path.
        public static List<string> Scan(string folder)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
            try
            {
                foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (IsAudio(f)) list.Add(f);
                    if (list.Count >= MaxTracks) break;
                }
            }
            catch (Exception ex) { Log.Warn("Music: could not read " + folder + ": " + ex.Message); }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Custom sources: files as chosen (any file Media Foundation can play), folders expanded.
        public static List<string> Expand(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) list.AddRange(Scan(p));
                else if (File.Exists(p)) list.Add(p);
            }
            return list;
        }

        // "<music folder>\<mood>" (any case), or null.
        public static string MoodFolder(string root, string mood)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                return Directory.EnumerateDirectories(root).FirstOrDefault(d => string.Equals(Path.GetFileName(d), mood, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        // Tracks for a mood: its subfolder of the music folder, or (none there) the whole folder.
        public static List<string> ForMood(string root, string mood)
        {
            string sub = MoodFolder(root, mood);
            var list = sub != null ? Scan(sub) : new List<string>();
            return list.Count > 0 ? list : Scan(root);
        }
    }
}
