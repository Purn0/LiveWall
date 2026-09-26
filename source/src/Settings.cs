using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LiveWall
{
    internal enum FitMode { Fill, Fit, Stretch, Center }

    // Persisted as simple UTF-8 "key=value" lines in %APPDATA%\LiveWall\settings.ini.
    internal sealed class Settings
    {
        public List<string> Sources = new List<string>();   // files and/or folders, in order
        public int IntervalMinutes = 15;                     // 0 = never change automatically
        public bool Shuffle = false;
        public FitMode Fit = FitMode.Fill;
        public bool PauseWhenCovered = true;                 // pause a screen whose desktop is fully covered
        public bool PauseOnFullscreen = true;                // pause every screen while a fullscreen app/game runs
        public bool PauseOnBattery = false;
        public bool PauseOnEnergySaver = true;
        public bool SyncWindowsWallpaper = true;             // keep the Windows wallpaper = first frame of the live one
        public bool ShowTrayIcon = true;
        public int DeepSleepSeconds = 60;                    // unload the decoder after this long out of sight
        public string LastItem = "";

        // Boards & drawing
        public string HotkeyBoard = "Ctrl+Alt+B";            // show today's board and draw on it / back to the wallpaper
        public string HotkeyDraw = "Ctrl+Alt+D";             // draw on whatever is shown (board or wallpaper)
        public bool ShowWallpaperInk = true;                 // show drawings made on wallpapers
        public string BoardStyle = "whiteboard";             // background of new boards
        public string BoardMode = "";                        // "", "daily", "daily:yyyy-MM-dd" or "permanent": board shown instead of the wallpaper
        public string UserId = "";                           // author id stored with each stroke (for shared boards later)

        // The user's Windows wallpaper before LiveWall first changed it (used by "restore").
        public bool OriginalCaptured;
        public string OriginalWallpaper = "";
        public int OriginalPosition = 4;

        public bool IsFirstRun;

        static string FilePath { get { return Path.Combine(AppPaths.DataDir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) { s.IsFirstRun = true; return s; }
            try
            {
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "source": if (v.Length > 0) s.Sources.Add(v); break;
                        case "interval": s.IntervalMinutes = Math.Max(0, ParseInt(v, 15)); break;
                        case "shuffle": s.Shuffle = v == "1"; break;
                        case "fit": { FitMode f; if (Enum.TryParse(v, true, out f)) s.Fit = f; break; }
                        case "pauseWhenCovered": s.PauseWhenCovered = v == "1"; break;
                        case "pauseOnFullscreen": s.PauseOnFullscreen = v == "1"; break;
                        case "pauseOnBattery": s.PauseOnBattery = v == "1"; break;
                        case "pauseOnEnergySaver": s.PauseOnEnergySaver = v == "1"; break;
                        case "syncWindowsWallpaper": s.SyncWindowsWallpaper = v == "1"; break;
                        case "showTrayIcon": s.ShowTrayIcon = v == "1"; break;
                        case "deepSleepSeconds": s.DeepSleepSeconds = Math.Max(5, ParseInt(v, 60)); break;
                        case "lastItem": s.LastItem = v; break;
                        case "hotkeyBoard": s.HotkeyBoard = v; break;
                        case "hotkeyDraw": s.HotkeyDraw = v; break;
                        case "showWallpaperInk": s.ShowWallpaperInk = v == "1"; break;
                        case "boardStyle": s.BoardStyle = v; break;
                        case "boardMode": s.BoardMode = v; break;
                        case "userId": s.UserId = v; break;
                        case "originalCaptured": s.OriginalCaptured = v == "1"; break;
                        case "originalWallpaper": s.OriginalWallpaper = v; break;
                        case "originalPosition": s.OriginalPosition = ParseInt(v, 4); break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Failed to read settings", ex); }
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# LiveWall settings");
            foreach (string src in Sources) sb.AppendLine("source=" + src);
            sb.AppendLine("interval=" + IntervalMinutes.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("shuffle=" + B(Shuffle));
            sb.AppendLine("fit=" + Fit);
            sb.AppendLine("pauseWhenCovered=" + B(PauseWhenCovered));
            sb.AppendLine("pauseOnFullscreen=" + B(PauseOnFullscreen));
            sb.AppendLine("pauseOnBattery=" + B(PauseOnBattery));
            sb.AppendLine("pauseOnEnergySaver=" + B(PauseOnEnergySaver));
            sb.AppendLine("syncWindowsWallpaper=" + B(SyncWindowsWallpaper));
            sb.AppendLine("showTrayIcon=" + B(ShowTrayIcon));
            sb.AppendLine("deepSleepSeconds=" + DeepSleepSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("lastItem=" + LastItem);
            sb.AppendLine("hotkeyBoard=" + HotkeyBoard);
            sb.AppendLine("hotkeyDraw=" + HotkeyDraw);
            sb.AppendLine("showWallpaperInk=" + B(ShowWallpaperInk));
            sb.AppendLine("boardStyle=" + BoardStyle);
            sb.AppendLine("boardMode=" + BoardMode);
            sb.AppendLine("userId=" + UserId);
            sb.AppendLine("originalCaptured=" + B(OriginalCaptured));
            sb.AppendLine("originalWallpaper=" + OriginalWallpaper);
            sb.AppendLine("originalPosition=" + OriginalPosition.ToString(CultureInfo.InvariantCulture));
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                IsFirstRun = false;
            }
            catch (Exception ex) { Log.Error("Failed to save settings", ex); }
        }

        public Settings Clone()
        {
            var c = (Settings)MemberwiseClone();
            c.Sources = new List<string>(Sources);
            return c;
        }

        static string B(bool b) { return b ? "1" : "0"; }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }
}
