// Core Audio (MMDevice + WASAPI session) interop, vtable order as in mmdeviceapi.h / audiopolicy.h / endpointvolume.h.
// Only what the music feature needs: the default render endpoint, its sessions, their state and peak meters.
using System;
using System.Runtime.InteropServices;

namespace LiveWall.Interop
{
    internal static class CoreAudio
    {
        public static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        public const int eRender = 0, eMultimedia = 1;
        public const uint CLSCTX_ALL = 0x17;
        public const int AudioSessionStateInactive = 0, AudioSessionStateActive = 1, AudioSessionStateExpired = 2;

        public static string TakeString(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    // Implemented by LiveWall. Called on Core Audio worker threads: only post a message from here.
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged(IntPtr deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded(IntPtr deviceId);
        [PreserveSig] int OnDeviceRemoved(IntPtr deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, IntPtr defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged(IntPtr deviceId, PROPERTYKEY key);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // IAudioSessionManager2 : IAudioSessionManager
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    // IAudioSessionControl2 : IAudioSessionControl (base methods declared in order).
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // ---- IAudioSessionControl ----
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
        // ---- IAudioSessionControl2 ----
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();   // S_OK = yes, S_FALSE = no
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    // Implemented by LiveWall; called on Core Audio worker threads.
    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IntPtr newSession);
    }

    // Implemented by LiveWall; called on Core Audio worker threads.
    [ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged(IntPtr newName, IntPtr eventContext);
        [PreserveSig] int OnIconPathChanged(IntPtr newPath, IntPtr eventContext);
        [PreserveSig] int OnSimpleVolumeChanged(float volume, int mute, IntPtr eventContext);
        [PreserveSig] int OnChannelVolumeChanged(uint channels, IntPtr volumes, uint changedChannel, IntPtr eventContext);
        [PreserveSig] int OnGroupingParamChanged(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int OnStateChanged(int newState);
        [PreserveSig] int OnSessionDisconnected(int reason);
    }

    // Queried from a session control: that session's peak meter.
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint count);
        [PreserveSig] int GetChannelsPeakValues(uint count, IntPtr peaks);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
    }
}
