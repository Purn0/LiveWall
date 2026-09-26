// Media Foundation Media Engine interop (vtable order matches mfmediaengine.h).
using System;
using System.Runtime.InteropServices;

namespace LiveWall.Interop
{
    internal static partial class MF
    {
        public static readonly Guid CLSID_MFMediaEngineClassFactory = new Guid("B44392DA-499B-446b-A4CB-005FEAD0E6D5");
        public static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9");
        public static readonly Guid MF_MEDIA_ENGINE_PLAYBACK_HWND = new Guid("d988879b-67c9-4d92-baa7-6eadd446039d");
        public static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new Guid("5066893c-8cf9-42bc-8b8a-472212e52726");
        public const uint MF_MEDIA_ENGINE_FORCEMUTE = 0x4;

        public const uint EVENT_ERROR = 5, EVENT_LOADEDMETADATA = 10, EVENT_ENDED = 19, EVENT_FORMATCHANGE = 1000,
            EVENT_FIRSTFRAMEREADY = 1009, EVENT_RESOURCELOST = 1012, EVENT_STREAMRENDERINGERROR = 1014;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFARGB { public byte B, G, R, A; }

    [ComImport, Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEngineClassFactory
    {
        [PreserveSig] int CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngineEx engine);
    }

    [ComImport, Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEngineNotify
    {
        [PreserveSig] int EventNotify(uint eventId, IntPtr param1, uint param2);
    }

    // IMFMediaEngineEx : IMFMediaEngine (base methods declared in full, in order; the Ex list is a prefix).
    [ComImport, Guid("83015ead-b1e6-40d0-a98a-37145ffe1ad1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEngineEx
    {
        // ---- IMFMediaEngine ----
        [PreserveSig] int GetError(out IntPtr error);
        [PreserveSig] int SetErrorCode(int error);
        [PreserveSig] int SetSourceElements(IntPtr elements);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
        [PreserveSig] int GetCurrentSource(out IntPtr url);
        [PreserveSig] ushort GetNetworkState();
        [PreserveSig] int GetPreload();
        [PreserveSig] int SetPreload(int preload);
        [PreserveSig] int GetBuffered(out IntPtr ranges);
        [PreserveSig] int Load();
        [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
        [PreserveSig] ushort GetReadyState();
        [PreserveSig] int IsSeeking();
        [PreserveSig] double GetCurrentTime();
        [PreserveSig] int SetCurrentTime(double seekTime);
        [PreserveSig] double GetStartTime();
        [PreserveSig] double GetDuration();
        [PreserveSig] int IsPaused();
        [PreserveSig] double GetDefaultPlaybackRate();
        [PreserveSig] int SetDefaultPlaybackRate(double rate);
        [PreserveSig] double GetPlaybackRate();
        [PreserveSig] int SetPlaybackRate(double rate);
        [PreserveSig] int GetPlayed(out IntPtr ranges);
        [PreserveSig] int GetSeekable(out IntPtr ranges);
        [PreserveSig] int IsEnded();
        [PreserveSig] int GetAutoPlay();
        [PreserveSig] int SetAutoPlay(int autoPlay);
        [PreserveSig] int GetLoop();
        [PreserveSig] int SetLoop(int loop);
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int GetMuted();
        [PreserveSig] int SetMuted(int muted);
        [PreserveSig] double GetVolume();
        [PreserveSig] int SetVolume(double volume);
        [PreserveSig] int HasVideo();
        [PreserveSig] int HasAudio();
        [PreserveSig] int GetNativeVideoSize(out uint cx, out uint cy);
        [PreserveSig] int GetVideoAspectRatio(out uint cx, out uint cy);
        [PreserveSig] int Shutdown();
        [PreserveSig] int TransferVideoFrame([MarshalAs(UnmanagedType.IUnknown)] object dstSurface, IntPtr src, [In] ref MFRect dst, IntPtr borderColor);
        [PreserveSig] int OnVideoStreamTick(out long pts);
        // ---- IMFMediaEngineEx ----
        [PreserveSig] int SetSourceFromByteStream(IntPtr byteStream, [MarshalAs(UnmanagedType.BStr)] string url);
        [PreserveSig] int GetStatistics(int statisticId, IntPtr value);
        [PreserveSig] int UpdateVideoStream([In] ref MFVideoNormalizedRect src, [In] ref MFRect dst, [In] ref MFARGB borderColor);
        [PreserveSig] double GetBalance();
        [PreserveSig] int SetBalance(double balance);
        [PreserveSig] int IsPlaybackRateSupported(double rate);
        [PreserveSig] int FrameStep(int forward);
        [PreserveSig] int GetResourceCharacteristics(out uint characteristics);
        [PreserveSig] int GetPresentationAttribute([In] ref Guid attribute, IntPtr value);
        [PreserveSig] int GetNumberOfStreams(out uint count);
        [PreserveSig] int GetStreamAttribute(uint streamIndex, [In] ref Guid attribute, IntPtr value);
        [PreserveSig] int GetStreamSelection(uint streamIndex, out int enabled);
        [PreserveSig] int SetStreamSelection(uint streamIndex, int enabled);
        [PreserveSig] int ApplyStreamSelections();
        [PreserveSig] int IsProtected(out int isProtected);
        [PreserveSig] int InsertVideoEffect(IntPtr effect, int optional);
        [PreserveSig] int InsertAudioEffect(IntPtr effect, int optional);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int SetTimelineMarkerTimer(double timeToFire);
        [PreserveSig] int GetTimelineMarkerTimer(out double timeToFire);
        [PreserveSig] int CancelTimelineMarkerTimer();
        [PreserveSig] int IsStereo3D();
        [PreserveSig] int GetStereo3DFramePackingMode(out int mode);
        [PreserveSig] int SetStereo3DFramePackingMode(int mode);
        [PreserveSig] int GetStereo3DRenderMode(out int outputType);
        [PreserveSig] int SetStereo3DRenderMode(int outputType);
        [PreserveSig] int EnableWindowlessSwapchainMode(int enable);
        [PreserveSig] int GetVideoSwapchainHandle(out IntPtr swapchain);
        [PreserveSig] int EnableHorizontalMirrorMode(int enable);
        [PreserveSig] int GetAudioStreamCategory(out uint category);
        [PreserveSig] int SetAudioStreamCategory(uint category);
        [PreserveSig] int GetAudioEndpointRole(out uint role);
        [PreserveSig] int SetAudioEndpointRole(uint role);
        [PreserveSig] int GetRealTimeMode(out int enabled);
        [PreserveSig] int SetRealTimeMode(int enable);
        [PreserveSig] int SetCurrentTimeEx(double seekTime, int seekMode);
        [PreserveSig] int EnableTimeUpdateTimer(int enable);
    }
}
