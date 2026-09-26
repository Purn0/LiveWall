using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiveWall.Interop;

namespace LiveWall
{
    // Tracks power source, Battery/Energy Saver, display on/off and session lock via push notifications
    // (no polling). Windows sends the current value of each power setting right after registration.
    internal sealed class PowerState : IDisposable
    {
        public bool OnBattery { get; private set; }
        public bool SaverOn { get { return batterySaver || energySaver; } }
        public bool DisplayOff { get; private set; }
        public bool SessionLocked { get; private set; }
        public bool Suspending { get; private set; }

        bool batterySaver, energySaver, locked, disconnected;
        readonly IntPtr window;
        readonly List<IntPtr> registrations = new List<IntPtr>();
        bool sessionRegistered;

        public PowerState(IntPtr notifyWindow)
        {
            window = notifyWindow;
            ReadPowerStatus();
            Register(Native.GUID_CONSOLE_DISPLAY_STATE);
            Register(Native.GUID_POWER_SAVING_STATUS);
            Register(Native.GUID_ENERGY_SAVER_STATUS);
            Register(Native.GUID_ACDC_POWER_SOURCE);
            sessionRegistered = Native.WTSRegisterSessionNotification(window, 0 /* NOTIFY_FOR_THIS_SESSION */);
        }

        void Register(Guid g)
        {
            IntPtr h = Native.RegisterPowerSettingNotification(window, ref g, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            if (h != IntPtr.Zero) registrations.Add(h);
        }

        void ReadPowerStatus()
        {
            SYSTEM_POWER_STATUS s;
            if (Native.GetSystemPowerStatus(out s))
            {
                OnBattery = s.ACLineStatus == 0;
                batterySaver = s.SystemStatusFlag == 1;
            }
        }

        // Returns true if the message changed any state.
        public bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_POWERBROADCAST)
            {
                uint evt = (uint)wParam.ToInt64();
                if (evt == Native.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
                {
                    var ps = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(lParam, typeof(POWERBROADCAST_SETTING));
                    if (ps.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE) DisplayOff = ps.Data == 0;          // 0 off, 1 on, 2 dimmed
                    else if (ps.PowerSetting == Native.GUID_POWER_SAVING_STATUS) batterySaver = ps.Data != 0;
                    else if (ps.PowerSetting == Native.GUID_ENERGY_SAVER_STATUS) energySaver = ps.Data != 0;     // 0 off, 1 standard, 2 high
                    else if (ps.PowerSetting == Native.GUID_ACDC_POWER_SOURCE) OnBattery = ps.Data != 0;         // 0 AC, 1 DC, 2 short-term DC
                    return true;
                }
                if (evt == Native.PBT_APMPOWERSTATUSCHANGE) { ReadPowerStatus(); return true; }
                if (evt == Native.PBT_APMSUSPEND) { Suspending = true; return true; }
                if (evt == Native.PBT_APMRESUMEAUTOMATIC || evt == Native.PBT_APMRESUMESUSPEND) { Suspending = false; ReadPowerStatus(); return true; }
                return false;
            }
            if (msg == Native.WM_WTSSESSION_CHANGE)
            {
                int code = (int)wParam.ToInt64();
                if (code == Native.WTS_SESSION_LOCK) locked = true;
                else if (code == Native.WTS_SESSION_UNLOCK) locked = false;
                else if (code == Native.WTS_CONSOLE_DISCONNECT || code == Native.WTS_REMOTE_DISCONNECT) disconnected = true;
                else if (code == Native.WTS_CONSOLE_CONNECT || code == Native.WTS_REMOTE_CONNECT) disconnected = false;
                else return false;
                SessionLocked = locked || disconnected;
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            return string.Format("battery={0} saver={1} displayOff={2} locked={3}", OnBattery, SaverOn, DisplayOff, SessionLocked);
        }

        public void Dispose()
        {
            foreach (IntPtr h in registrations) Native.UnregisterPowerSettingNotification(h);
            registrations.Clear();
            if (sessionRegistered) Native.WTSUnRegisterSessionNotification(window);
            sessionRegistered = false;
        }
    }
}
