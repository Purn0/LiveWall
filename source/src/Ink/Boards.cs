using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall.Ink
{
    internal enum BoardKind { Daily, Permanent }

    // Where drawings live:
    //   %APPDATA%\LiveWall\boards\daily\2026-09-27.lwink    one board per day, kept
    //   %APPDATA%\LiveWall\boards\permanent.lwink            never cleared automatically
    //   %APPDATA%\LiveWall\ink\<hash>.lwink                  drawings on a wallpaper (by its path)
    //   %LOCALAPPDATA%\LiveWall\boards\*.png                 board images handed to Windows (rebuilt any time)
    internal static class Boards
    {
        public static string Dir { get { return Path.Combine(AppPaths.DataDir, "boards"); } }
        public static string DailyDir { get { return Path.Combine(Dir, "daily"); } }
        public static string PermanentPath { get { return Path.Combine(Dir, "permanent.lwink"); } }
        public static string RenderDir { get { return Path.Combine(AppPaths.LocalDir, "boards"); } }

        public static string DailyPath(DateTime date)
        {
            return Path.Combine(DailyDir, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".lwink");
        }

        public static string WallpaperInkPath(string wallpaperPath)
        {
            return Path.Combine(AppPaths.DataDir, "ink", AppPaths.ShortHash("ink|" + wallpaperPath.ToLowerInvariant()) + ".lwink");
        }

        // Days that have a saved daily board, newest first.
        public static List<DateTime> DailyDates()
        {
            var list = new List<DateTime>();
            try
            {
                if (!Directory.Exists(DailyDir)) return list;
                foreach (string f in Directory.GetFiles(DailyDir, "*.lwink"))
                {
                    DateTime d;
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                        list.Add(d.Date);
                }
            }
            catch (Exception ex) { Log.Warn("Listing daily boards: " + ex.Message); }
            return list.OrderByDescending(d => d).ToList();
        }

        public static string Header(BoardKind kind, DateTime date)
        {
            return kind == BoardKind.Daily ? date.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture) : null;
        }

        public static int Seed(BoardKind kind, DateTime date)
        {
            return kind == BoardKind.Daily ? date.Year * 1000 + date.DayOfYear : 7;
        }

        public static string Title(BoardKind kind, DateTime date)
        {
            if (kind == BoardKind.Permanent) return "Permanent board";
            if (date == DateTime.Today) return "Today's board";
            if (date == DateTime.Today.AddDays(-1)) return "Yesterday's board";
            return "Board of " + date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        }

        // Deletes board images that are no longer shown (older than a couple of minutes, so nothing in flight).
        public static void CleanRenders(string keep)
        {
            try
            {
                if (!Directory.Exists(RenderDir)) return;
                foreach (var f in new DirectoryInfo(RenderDir).GetFiles())
                {
                    if (string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-2)) continue;
                    try { f.Delete(); } catch { }
                }
            }
            catch (Exception ex) { Log.Warn("Cleaning board images: " + ex.Message); }
        }
    }

    // Global keyboard shortcuts, written like "Ctrl+Alt+B" in the settings.
    internal static class Hotkeys
    {
        public static bool TryParse(string text, out uint modifiers, out uint vk)
        {
            modifiers = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= InkNative.MOD_CONTROL; break;
                    case "alt": modifiers |= InkNative.MOD_ALT; break;
                    case "shift": modifiers |= InkNative.MOD_SHIFT; break;
                    case "win": case "windows": modifiers |= InkNative.MOD_WIN; break;
                    default: return false;
                }
            }
            string key = parts[parts.Length - 1];
            Keys k;
            if (key.Length == 1 && char.IsDigit(key[0])) k = Keys.D0 + (key[0] - '0');
            else if (!Enum.TryParse(key, true, out k)) return false;
            vk = (uint)(k & Keys.KeyCode);
            return vk != 0;
        }

        public static string Format(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            var parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            parts.Add(k >= Keys.D0 && k <= Keys.D9 ? ((char)('0' + (k - Keys.D0))).ToString() : k.ToString());
            return string.Join("+", parts);
        }

        // An empty shortcut means "none" and counts as success.
        public static bool Register(IntPtr hwnd, int id, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            uint mods, vk;
            if (!TryParse(text, out mods, out vk)) { Log.Warn("Not a valid shortcut: '" + text + "'"); return false; }
            bool ok = InkNative.RegisterHotKey(hwnd, id, mods | InkNative.MOD_NOREPEAT, vk);
            if (!ok) Log.Warn("Shortcut " + text + " could not be registered (in use by another app?)");
            return ok;
        }

        public static void Unregister(IntPtr hwnd, int id) { InkNative.UnregisterHotKey(hwnd, id); }
    }
}
