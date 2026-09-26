using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiveWall
{
    internal static class AppPaths
    {
        public static string DataDir { get; private set; }    // settings (roaming)
        public static string LocalDir { get; private set; }   // log + cache (local)
        public static string CacheDir { get; private set; }
        public static string InstanceSuffix { get; private set; }

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        public static void Init(string customDataDir)
        {
            if (!string.IsNullOrEmpty(customDataDir))
            {
                DataDir = Path.GetFullPath(customDataDir);
                LocalDir = DataDir;
                InstanceSuffix = "." + ShortHash(DataDir.ToLowerInvariant());
            }
            else
            {
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveWall");
                LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveWall");
                InstanceSuffix = "";
            }
            CacheDir = Path.Combine(LocalDir, "cache");
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CacheDir);
        }

        public static string ShortHash(string s)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    internal static class Log
    {
        static readonly object gate = new object();
        static string path;
        public static bool Verbose;

        public static string FilePath { get { return path; } }

        public static void Init(string dir)
        {
            path = Path.Combine(dir, "livewall.log");
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch { }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Debug(string msg) { if (Verbose) Write("DEBUG", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg, Exception ex) { Write("ERROR", ex == null ? msg : msg + ": " + ex); }

        static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg + Environment.NewLine;
            lock (gate)
            {
                if (path == null) return;
                try { File.AppendAllText(path, line, Encoding.UTF8); }
                catch { }
            }
        }
    }
}
