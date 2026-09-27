using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveWall
{
    internal enum FitMode { Fill, Fit, Stretch, Center }

    // A named set of wallpapers (files and/or folders), optionally played every day between two times.
    internal sealed class WallpaperCollection
    {
        public string Name = "";
        public List<string> Sources = new List<string>();
        public bool Scheduled;
        public int StartMinute = 7 * 60, EndMinute = 19 * 60;   // minutes after midnight; End < Start wraps past midnight

        public WallpaperCollection Clone()
        {
            var c = (WallpaperCollection)MemberwiseClone();
            c.Sources = new List<string>(Sources);
            return c;
        }

        public bool Covers(int minuteOfDay)
        {
            if (!Scheduled || StartMinute == EndMinute) return false;
            return StartMinute < EndMinute ? minuteOfDay >= StartMinute && minuteOfDay < EndMinute
                                           : minuteOfDay >= StartMinute || minuteOfDay < EndMinute;
        }

        public string ScheduleText { get { return Clock(StartMinute) + "-" + Clock(EndMinute); } }

        public static string Clock(int minute)
        {
            return (minute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minute % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public static bool TryParseSchedule(string v, out int start, out int end)
        {
            start = end = 0;
            string[] p = v.Split('-');
            return p.Length == 2 && TryClock(p[0], out start) && TryClock(p[1], out end);
        }

        static bool TryClock(string s, out int minute)
        {
            minute = 0;
            string[] p = s.Trim().Split(':');
            int h, m;
            if (p.Length != 2 || !int.TryParse(p[0], out h) || !int.TryParse(p[1], out m) || h < 0 || h > 23 || m < 0 || m > 59) return false;
            minute = h * 60 + m;
            return true;
        }
    }

    // Music: a source spec per wallpaper ("music.<hash>=...", see MusicSpec), a global default, and when to go silent.
    // Edited only in the Music window and the tray (the Settings window keeps whatever is current).
    internal sealed class MusicSettings
    {
        public string Default = MusicSpec.None;
        public string Folder = "";                           // "" = the user's Music folder
        public int Volume = 50;                              // 0-100
        public bool Muted;                                   // the user paused the music (tray Play/Pause)
        public bool SilenceForOtherAudio = true;
        public bool PauseOnFullscreen = true;
        public bool PauseOnBattery = false;
        public bool PauseOnEnergySaver = true;
        public int GraceSeconds = 8;                         // silent this long: the music process ends
        public int ResumeSeconds = 4;                        // other apps quiet this long: the music comes back
        public bool AskAi = false;                           // online mood tagging (off by default)
        public string AiKey = "";                            // DPAPI-protected, base64
        public string AiModel = "claude-opus-5";
        public Dictionary<string, string> Overrides = new Dictionary<string, string>();   // wallpaper hash -> spec
        public Dictionary<string, string> Moods = new Dictionary<string, string>();       // wallpaper hash -> "calm" or "calm ai"

        public MusicSettings Clone()
        {
            var c = (MusicSettings)MemberwiseClone();
            c.Overrides = new Dictionary<string, string>(Overrides);
            c.Moods = new Dictionary<string, string>(Moods);
            return c;
        }

        public string EffectiveFolder
        {
            get { return Folder.Length > 0 ? Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); }
        }

        public static string KeyFor(string wallpaper) { return AppPaths.ShortHash((wallpaper ?? "").ToLowerInvariant()); }

        // The wallpaper's own choice, or null (= the default).
        public string OverrideFor(string wallpaper)
        {
            string v;
            return wallpaper != null && Overrides.TryGetValue(KeyFor(wallpaper), out v) ? v : null;
        }
    }

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

        // Collections: "" = all wallpapers (Sources above). A scheduled collection takes over during its hours.
        public List<WallpaperCollection> Collections = new List<WallpaperCollection>();
        public string ActiveCollection = "";
        public string HotkeyCollection = "Ctrl+Alt+W";      // next collection

        // Boards & drawing
        public string HotkeyBoard = "Ctrl+Alt+B";            // show today's board and draw on it / back to the wallpaper
        public string HotkeyDraw = "Ctrl+Alt+D";             // draw on whatever is shown (board or wallpaper)
        public bool ShowWallpaperInk = true;                 // show drawings made on wallpapers
        public string BoardStyle = "whiteboard";             // background of new boards
        public string BoardMode = "";                        // "", "daily", "daily:yyyy-MM-dd" or "permanent": board shown instead of the wallpaper
        public string LastBoard = "daily";                   // "daily" or "permanent": what the board shortcut shows
        public string UserId = "";                           // author id stored with each stroke (for shared boards later)

        public MusicSettings Music = new MusicSettings();

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
                    if (k.StartsWith("music.")) { if (v.Length > 0) s.Music.Overrides[k.Substring(6)] = v; continue; }
                    if (k.StartsWith("mood.")) { if (v.Length > 0) s.Music.Moods[k.Substring(5)] = v; continue; }
                    switch (k)
                    {
                        case "source": if (v.Length > 0) s.Sources.Add(v); break;
                        // A collection's lines follow its "collection=" line.
                        case "collection": if (v.Length > 0) s.Collections.Add(new WallpaperCollection { Name = v }); break;
                        case "collectionSource": if (v.Length > 0 && s.Collections.Count > 0) s.Collections[s.Collections.Count - 1].Sources.Add(v); break;
                        case "collectionSchedule":
                        {
                            int a, b;
                            if (s.Collections.Count > 0 && WallpaperCollection.TryParseSchedule(v, out a, out b))
                            {
                                var c = s.Collections[s.Collections.Count - 1];
                                c.Scheduled = true; c.StartMinute = a; c.EndMinute = b;
                            }
                            break;
                        }
                        case "activeCollection": s.ActiveCollection = v; break;
                        case "hotkeyCollection": s.HotkeyCollection = v; break;
                        case "lastBoard": s.LastBoard = v == "permanent" ? "permanent" : "daily"; break;
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
                        case "musicDefault": s.Music.Default = v.Length > 0 ? v : MusicSpec.None; break;
                        case "musicFolder": s.Music.Folder = v; break;
                        case "musicVolume": s.Music.Volume = Math.Max(0, Math.Min(100, ParseInt(v, 50))); break;
                        case "musicMuted": s.Music.Muted = v == "1"; break;
                        case "musicSilenceForOtherAudio": s.Music.SilenceForOtherAudio = v == "1"; break;
                        case "musicPauseOnFullscreen": s.Music.PauseOnFullscreen = v == "1"; break;
                        case "musicPauseOnBattery": s.Music.PauseOnBattery = v == "1"; break;
                        case "musicPauseOnEnergySaver": s.Music.PauseOnEnergySaver = v == "1"; break;
                        case "musicGraceSeconds": s.Music.GraceSeconds = Math.Max(1, Math.Min(600, ParseInt(v, 8))); break;
                        case "musicResumeSeconds": s.Music.ResumeSeconds = Math.Max(1, Math.Min(600, ParseInt(v, 4))); break;
                        case "musicAskAi": s.Music.AskAi = v == "1"; break;
                        case "musicAiKey": s.Music.AiKey = v; break;
                        case "musicAiModel": if (v.Length > 0) s.Music.AiModel = v; break;
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
            foreach (var c in Collections)
            {
                sb.AppendLine("collection=" + c.Name);
                if (c.Scheduled) sb.AppendLine("collectionSchedule=" + c.ScheduleText);
                foreach (string src in c.Sources) sb.AppendLine("collectionSource=" + src);
            }
            sb.AppendLine("activeCollection=" + ActiveCollection);
            sb.AppendLine("hotkeyCollection=" + HotkeyCollection);
            sb.AppendLine("lastBoard=" + LastBoard);
            sb.AppendLine("hotkeyBoard=" + HotkeyBoard);
            sb.AppendLine("hotkeyDraw=" + HotkeyDraw);
            sb.AppendLine("showWallpaperInk=" + B(ShowWallpaperInk));
            sb.AppendLine("boardStyle=" + BoardStyle);
            sb.AppendLine("boardMode=" + BoardMode);
            sb.AppendLine("userId=" + UserId);
            sb.AppendLine("originalCaptured=" + B(OriginalCaptured));
            sb.AppendLine("originalWallpaper=" + OriginalWallpaper);
            sb.AppendLine("originalPosition=" + OriginalPosition.ToString(CultureInfo.InvariantCulture));
            var m = Music;
            sb.AppendLine("musicDefault=" + m.Default);
            sb.AppendLine("musicFolder=" + m.Folder);
            sb.AppendLine("musicVolume=" + m.Volume.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("musicMuted=" + B(m.Muted));
            sb.AppendLine("musicSilenceForOtherAudio=" + B(m.SilenceForOtherAudio));
            sb.AppendLine("musicPauseOnFullscreen=" + B(m.PauseOnFullscreen));
            sb.AppendLine("musicPauseOnBattery=" + B(m.PauseOnBattery));
            sb.AppendLine("musicPauseOnEnergySaver=" + B(m.PauseOnEnergySaver));
            sb.AppendLine("musicGraceSeconds=" + m.GraceSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("musicResumeSeconds=" + m.ResumeSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("musicAskAi=" + B(m.AskAi));
            sb.AppendLine("musicAiKey=" + m.AiKey);
            sb.AppendLine("musicAiModel=" + m.AiModel);
            foreach (var kv in m.Overrides.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.AppendLine("music." + kv.Key + "=" + kv.Value);
            foreach (var kv in m.Moods.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.AppendLine("mood." + kv.Key + "=" + kv.Value);
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
            c.Collections = Collections.Select(x => x.Clone()).ToList();
            c.Music = Music.Clone();
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
