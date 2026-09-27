using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveWall
{
    // Tags a wallpaper picture with one of MusicSpec.Moods, used to pick music from "<music folder>\<mood>".
    // Runs on the MediaWorker thread, once per wallpaper (the tag is cached in settings.ini as "mood.<hash>=").
    internal interface IMoodAnalyzer
    {
        string Name { get; }
        string Analyze(string imagePath);   // a mood, or null when it cannot tell
    }

    // Offline: brightness, saturation, contrast and the dominant hue of a 64-pixel thumbnail.
    internal sealed class LocalMoodAnalyzer : IMoodAnalyzer
    {
        public string Name { get { return "local"; } }

        public string Analyze(string imagePath)
        {
            try
            {
                using (Bitmap bmp = MoodImage.Load(imagePath, 64))
                {
                    var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var px = new int[bmp.Width * bmp.Height];
                    for (int y = 0; y < bmp.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, px, y * bmp.Width, bmp.Width);
                    bmp.UnlockBits(bits);
                    return Classify(px);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Mood: could not read " + Path.GetFileName(imagePath) + ": " + ex.Message);
                return null;
            }
        }

        static string Classify(int[] px)
        {
            double sumL = 0, sumL2 = 0, sumSV = 0, sumV = 0, colored = 0, warm = 0, cool = 0, purple = 0, sunny = 0;
            foreach (int c in px)
            {
                double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, b = (c & 255) / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double v = max, s = max <= 0 ? 0 : (max - min) / max;
                // Luma alone calls saturated blues and reds dark; half luma, half value is closer to how bright they look.
                double l = 0.5 * (0.2126 * r + 0.7152 * g + 0.0722 * b) + 0.5 * v;
                sumL += l; sumL2 += l * l;
                sumSV += s * v; sumV += v;
                double w = s * v;   // how strongly colored this pixel is
                if (w < 0.08 || max == min) continue;
                double h = max == r ? 60 * (((g - b) / (max - min)) % 6) : max == g ? 60 * ((b - r) / (max - min) + 2) : 60 * ((r - g) / (max - min) + 4);
                if (h < 0) h += 360;
                colored += w;
                if (h < 50 || h >= 330) warm += w;          // reds, oranges, browns
                else if (h < 160) sunny += w;               // yellows, greens
                else if (h < 260) cool += w;                // cyans, blues
                else purple += w;                           // violets, pinks
            }
            double n = px.Length;
            double bright = sumL / n, contrast = Math.Sqrt(Math.Max(0, sumL2 / n - bright * bright));
            double sat = sumV > 0 ? sumSV / sumV : 0, colorful = colored / n;
            double total = Math.Max(colored, 1e-9);
            double warmShare = warm / total, coolShare = cool / total, purpleShare = purple / total, sunnyShare = sunny / total;
            if (colored < n * 0.02) warmShare = coolShare = purpleShare = sunnyShare = 0;   // grey picture: no hue to speak of

            if (bright < 0.18) return "dark";
            // Vivid and punchy: energetic in reds, oranges, yellows and greens; dreamy in blues and violets.
            if (colorful > 0.30 && contrast > 0.22) return warmShare + sunnyShare >= coolShare + purpleShare ? "energetic" : "dreamy";
            if (bright < 0.33)
            {
                if (warmShare > 0.45) return "cozy";                      // lamplight, fireplace, sunset indoors
                if (purpleShare > 0.35) return "dreamy";
                return "dark";
            }
            if (warmShare > 0.55 && bright < 0.55) return "cozy";
            if (purpleShare > 0.35 || (bright > 0.60 && sat > 0.06 && sat < 0.28)) return "dreamy";   // violet, pink or pastel
            if (bright > 0.50 && sat > 0.35 && warmShare + sunnyShare > 0.5) return "happy";
            return "calm";
        }
    }

    // Online (optional, off by default): asks Claude for the mood of the picture with the user's own API key.
    // Plain HTTPS (HttpWebRequest, in System.dll): the official C# SDK needs NuGet and a newer .NET than this build.
    internal sealed class ClaudeMoodAnalyzer : IMoodAnalyzer
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string Prompt = "This picture is a desktop wallpaper. Which one word best describes its mood, for choosing " +
                              "background music: calm, energetic, dark, happy, dreamy or cozy? Answer with just that word.";
        readonly string apiKey, model;

        public ClaudeMoodAnalyzer(string apiKey, string model) { this.apiKey = apiKey; this.model = model; }

        public string Name { get { return "Claude"; } }

        public string Analyze(string imagePath)
        {
            string image;
            try { image = Convert.ToBase64String(MoodImage.SmallJpeg(imagePath, 512)); }
            catch (Exception ex) { Log.Warn("Mood (online): could not read " + Path.GetFileName(imagePath) + ": " + ex.Message); return null; }

            // Newer models: low effort is plenty for one word; server-side fallbacks answer if the model declines.
            bool current = model.StartsWith("claude-opus-5") || model.StartsWith("claude-fable-5");
            bool effort = current || model.StartsWith("claude-sonnet-5") || model.StartsWith("claude-opus-4-");
            var body = new StringBuilder();
            body.Append("{\"model\":").Append(Json.Quote(model)).Append(",\"max_tokens\":1024");
            if (effort) body.Append(",\"output_config\":{\"effort\":\"low\"}");
            if (current) body.Append(",\"fallbacks\":\"default\"");
            body.Append(",\"messages\":[{\"role\":\"user\",\"content\":[");
            body.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"").Append(image).Append("\"}},");
            body.Append("{\"type\":\"text\",\"text\":").Append(Json.Quote(Prompt)).Append("}]}]}");

            string response;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2 (off by default for this runtime)
                var req = (HttpWebRequest)WebRequest.Create(Endpoint);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = req.ReadWriteTimeout = 30000;
                req.Headers["x-api-key"] = apiKey;
                req.Headers["anthropic-version"] = "2023-06-01";
                if (current) req.Headers["anthropic-beta"] = "server-side-fallback-2026-07-01";
                byte[] data = Encoding.UTF8.GetBytes(body.ToString());
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) response = r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                string detail = "";
                if (http != null) { try { using (var r = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) detail = r.ReadToEnd(); } catch { } }
                Log.Warn("Mood (online): request failed: " + (http != null ? (int)http.StatusCode + " " : "") + ex.Message +
                         (detail.Length > 0 ? " " + (detail.Length > 300 ? detail.Substring(0, 300) : detail) : ""));
                return null;
            }

            var msg = Json.Parse(response) as Dictionary<string, object>;
            if (msg == null) { Log.Warn("Mood (online): unreadable response"); return null; }
            object stop;
            if (msg.TryGetValue("stop_reason", out stop) && "refusal".Equals(stop)) { Log.Warn("Mood (online): the model declined"); return null; }
            var text = new StringBuilder();
            object content;
            if (msg.TryGetValue("content", out content) && content is List<object>)
                foreach (var block in ((List<object>)content).OfType<Dictionary<string, object>>())
                {
                    object type, t;
                    if (block.TryGetValue("type", out type) && "text".Equals(type) && block.TryGetValue("text", out t)) text.Append(t as string).Append(' ');
                }
            string answer = text.ToString().ToLowerInvariant();
            string mood = MusicSpec.Moods.Select(m => new { m, i = IndexOfWord(answer, m) }).Where(x => x.i >= 0).OrderBy(x => x.i).Select(x => x.m).FirstOrDefault();
            if (mood == null) Log.Warn("Mood (online): no mood in the answer '" + (answer.Length > 80 ? answer.Substring(0, 80) : answer).Trim() + "'");
            return mood;
        }

        static int IndexOfWord(string text, string word)
        {
            for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
            {
                bool start = i == 0 || !char.IsLetter(text[i - 1]), end = i + word.Length >= text.Length || !char.IsLetter(text[i + word.Length]);
                if (start && end) return i;
            }
            return -1;
        }
    }

    internal static class MoodImage
    {
        // The picture scaled so its longer side is `max` pixels.
        public static Bitmap Load(string path, int max)
        {
            using (var fs = File.OpenRead(path))
            using (var img = Image.FromStream(fs, false, false))
            {
                double scale = Math.Min(1.0, (double)max / Math.Max(img.Width, img.Height));
                int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;   // averages many pixels per thumbnail pixel
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, 0, 0, w, h);
                }
                return bmp;
            }
        }

        public static byte[] SmallJpeg(string path, int max)
        {
            using (Bitmap bmp = Load(path, max))
            using (var ms = new MemoryStream())
            {
                ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                using (var p = new EncoderParameters(1))
                {
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                    bmp.Save(ms, jpeg, p);
                }
                return ms.ToArray();
            }
        }
    }

    // The API key is stored encrypted for the current Windows user (DPAPI; crypt32 directly, no extra assembly).
    internal static class Dpapi
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveWall music mood key");

        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DATA_BLOB data, string description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB result);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DATA_BLOB data, IntPtr description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB result);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] r = Run(Encoding.UTF8.GetBytes(plain), true);
            return r == null ? "" : Convert.ToBase64String(r);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                byte[] r = Run(Convert.FromBase64String(stored), false);
                return r == null ? "" : Encoding.UTF8.GetString(r);
            }
            catch (FormatException) { return ""; }
        }

        static byte[] Run(byte[] input, bool protect)
        {
            GCHandle hIn = GCHandle.Alloc(input, GCHandleType.Pinned), hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                var inBlob = new DATA_BLOB { cbData = input.Length, pbData = hIn.AddrOfPinnedObject() };
                var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
                DATA_BLOB outBlob;
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "LiveWall", ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
                if (!ok) return null;
                try
                {
                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { hIn.Free(); hEnt.Free(); }
        }
    }

    // Just enough JSON for one API response: objects -> Dictionary, arrays -> List, strings, doubles, bools, null.
    internal static class Json
    {
        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public static object Parse(string s)
        {
            try { int i = 0; object v = Value(s, ref i); return v; }
            catch (Exception) { return null; }
        }

        static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Skip(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    string k = Str(s, ref i);
                    Skip(s, ref i);
                    if (s[i++] != ':') throw new FormatException();
                    d[k] = Value(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == '}') return d;
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Skip(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == ']') return l;
                    throw new FormatException();
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i++] != '"') throw new FormatException();
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }
    }
}
