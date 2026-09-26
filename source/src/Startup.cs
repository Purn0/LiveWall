using System;
using Microsoft.Win32;

namespace LiveWall
{
    // "Start with Windows" = a per-user Run entry (no admin rights, no service, no scheduled task).
    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "LiveWall";

        static string Command { get { return "\"" + AppPaths.ExePath + "\" --autostart"; } }

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                string v = k == null ? null : k.GetValue(ValueName) as string;
                if (v == null || v.IndexOf(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            // Task Manager's "Startup apps" can disable the entry (first byte odd = disabled).
            using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            {
                var data = k == null ? null : k.GetValue(ValueName) as byte[];
                if (data != null && data.Length > 0 && (data[0] & 1) == 1) return false;
            }
            return true;
        }

        public static void Set(bool enable)
        {
            if (enable == IsEnabled()) return;
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) k.SetValue(ValueName, Command);
                else k.DeleteValue(ValueName, false);
            }
            if (enable)
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (k != null) k.DeleteValue(ValueName, false);
            }
            Log.Info("Start with Windows: " + enable);
        }
    }
}
