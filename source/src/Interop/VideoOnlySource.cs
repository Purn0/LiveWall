// A video file's media source without its audio streams, handed to the video host's Media Engine through
// MF_MEDIA_ENGINE_EXTENSION. The engine keeps an audio stream running (silent, as its clock) whenever the source has
// audio - even force-muted and with the stream deselected - which keeps the sound device awake. Seeing no audio, it
// plays like a file without a soundtrack: no audio decoding, no running audio stream.
// Everything else is passed straight through to the real source (vtable order as in mfidl.h / mfobjects.h).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LiveWall.Interop
{
    internal static partial class MF
    {
        public static readonly Guid MF_MEDIA_ENGINE_EXTENSION = new Guid("3109fd46-060d-4b62-8dcf-faff811318d2");
        public const uint MF_OBJECT_MEDIASOURCE = 0, MF_RESOLUTION_MEDIASOURCE = 0x1, MF_RESOLUTION_READ = 0x10000;
        public const int MF_E_UNSUPPORTED_BYTESTREAM_TYPE = unchecked((int)0xC00D36C4), MF_E_UNSUPPORTED_SERVICE = unchecked((int)0xC00D36BA),
            E_FAIL = unchecked((int)0x80004005);

        [DllImport("mfplat.dll")] public static extern int MFCreateSourceResolver(out IMFSourceResolver resolver);
        [DllImport("mfplat.dll")] public static extern int MFCreatePresentationDescriptor(uint count, IntPtr[] descriptors, out IntPtr pd);
        [DllImport("mfplat.dll")] public static extern int MFCreateAsyncResult(IntPtr obj, IntPtr callback, IntPtr state, out IntPtr result);
        [DllImport("mfplat.dll")] public static extern int MFInvokeCallback(IntPtr result);
    }

    // Creates the real source for the engine's byte stream and wraps it when it has audio.
    public sealed class VideoOnlyExtension : IMFMediaEngineExtension
    {
        int IMFMediaEngineExtension.CanPlayType(int audioOnly, string mimeType, out int answer) { answer = 0; return 0; }

        int IMFMediaEngineExtension.BeginCreateObject(string url, IntPtr byteStream, uint type, out IntPtr cancelCookie, IntPtr callback, IntPtr state)
        {
            cancelCookie = IntPtr.Zero;
            if (type != MF.MF_OBJECT_MEDIASOURCE || byteStream == IntPtr.Zero) return MF.MF_E_UNSUPPORTED_BYTESTREAM_TYPE;
            IntPtr source = IntPtr.Zero;
            try
            {
                IMFSourceResolver resolver;
                int hr = MF.MFCreateSourceResolver(out resolver);
                if (hr < 0) return hr;
                uint objType;
                hr = resolver.CreateObjectFromByteStream(byteStream, url, MF.MF_RESOLUTION_MEDIASOURCE | MF.MF_RESOLUTION_READ, IntPtr.Zero, out objType, out source);
                Marshal.ReleaseComObject(resolver);
                if (hr < 0) return hr;

                IntPtr result = source;
                try
                {
                    var inner = (IMFMediaSource)Marshal.GetObjectForIUnknown(source);
                    HashSet<uint> audio = VideoOnlySource.AudioStreams(inner);
                    if (audio.Count > 0)
                    {
                        result = Marshal.GetIUnknownForObject(new VideoOnlySource(inner, audio));
                        Marshal.Release(source);
                    }
                    else Marshal.ReleaseComObject(inner);
                }
                catch (Exception ex) { Log.Warn("Video without audio: using the file as it is (" + ex.Message + ")"); result = source; }
                source = IntPtr.Zero;

                IntPtr asyncResult;
                hr = MF.MFCreateAsyncResult(result, callback, state, out asyncResult);
                Marshal.Release(result);   // the async result holds it now
                if (hr < 0) return hr;
                hr = MF.MFInvokeCallback(asyncResult);
                Marshal.Release(asyncResult);
                return hr;
            }
            catch (Exception ex)
            {
                if (source != IntPtr.Zero) Marshal.Release(source);
                Log.Warn("Video source: " + ex.Message);
                return MF.E_FAIL;
            }
        }

        int IMFMediaEngineExtension.CancelObjectCreation(IntPtr cancelCookie) { return 0; }

        int IMFMediaEngineExtension.EndCreateObject(IntPtr result, out IntPtr obj)
        {
            obj = IntPtr.Zero;
            var ar = (IMFAsyncResult)Marshal.GetObjectForIUnknown(result);
            try { return ar.GetObject(out obj); }
            finally { Marshal.ReleaseComObject(ar); }
        }
    }

    // Presentation descriptors without the audio streams; Start() maps the engine's selection back to the real source.
    public sealed class VideoOnlySource : IMFMediaSource, IMFMediaEventGenerator, IMFGetService
    {
        readonly IMFMediaSource inner;
        readonly HashSet<uint> audio;

        internal VideoOnlySource(IMFMediaSource inner, HashSet<uint> audioStreamIds) { this.inner = inner; audio = audioStreamIds; }

        internal static HashSet<uint> AudioStreams(IMFMediaSource source)
        {
            var ids = new HashSet<uint>();
            IntPtr pdPtr;
            if (source.CreatePresentationDescriptor(out pdPtr) < 0) return ids;
            var pd = (IMFPresentationDescriptor)Marshal.GetObjectForIUnknown(pdPtr);
            try
            {
                uint count;
                pd.GetStreamDescriptorCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    int selected;
                    IntPtr sdPtr;
                    if (pd.GetStreamDescriptorByIndex(i, out selected, out sdPtr) < 0) continue;
                    var sd = (IMFStreamDescriptor)Marshal.GetObjectForIUnknown(sdPtr);
                    try
                    {
                        uint id;
                        IMFMediaTypeHandler handler;
                        Guid major;
                        if (sd.GetStreamIdentifier(out id) >= 0 && sd.GetMediaTypeHandler(out handler) >= 0)
                        {
                            if (handler.GetMajorType(out major) >= 0 && major == MF.MFMediaType_Audio) ids.Add(id);
                            Marshal.ReleaseComObject(handler);
                        }
                    }
                    finally { Marshal.ReleaseComObject(sd); Marshal.Release(sdPtr); }
                }
            }
            finally { Marshal.ReleaseComObject(pd); Marshal.Release(pdPtr); }
            return ids;
        }

        static uint StreamId(IntPtr sdPtr)
        {
            var sd = (IMFStreamDescriptor)Marshal.GetObjectForIUnknown(sdPtr);
            try { uint id; sd.GetStreamIdentifier(out id); return id; }
            finally { Marshal.ReleaseComObject(sd); }
        }

        int CreatePresentationDescriptor(out IntPtr result)
        {
            result = IntPtr.Zero;
            IntPtr innerPtr;
            int hr = inner.CreatePresentationDescriptor(out innerPtr);
            if (hr < 0) return hr;
            var innerPd = (IMFPresentationDescriptor)Marshal.GetObjectForIUnknown(innerPtr);
            var keep = new List<IntPtr>();
            var selected = new List<bool>();
            try
            {
                uint count;
                innerPd.GetStreamDescriptorCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    int sel;
                    IntPtr sd;
                    if (innerPd.GetStreamDescriptorByIndex(i, out sel, out sd) < 0) continue;
                    if (audio.Contains(StreamId(sd))) { Marshal.Release(sd); continue; }
                    keep.Add(sd);
                    selected.Add(sel != 0);
                }
                IntPtr pdPtr;
                hr = MF.MFCreatePresentationDescriptor((uint)keep.Count, keep.ToArray(), out pdPtr);   // same stream descriptor objects
                if (hr < 0) return hr;
                var pd = (IMFPresentationDescriptor)Marshal.GetObjectForIUnknown(pdPtr);
                ((IMFAttributes)innerPd).CopyAllItems((IMFAttributes)pd);   // duration and the rest
                for (int i = 0; i < selected.Count; i++) { if (selected[i]) pd.SelectStream((uint)i); else pd.DeselectStream((uint)i); }
                Marshal.ReleaseComObject(pd);
                result = pdPtr;
                return 0;
            }
            finally
            {
                foreach (IntPtr sd in keep) Marshal.Release(sd);
                Marshal.ReleaseComObject(innerPd);
                Marshal.Release(innerPtr);
            }
        }

        // The engine's descriptor lists only the video; select the same streams (by id) on the real source, audio off.
        int Start(IntPtr pd, IntPtr timeFormat, IntPtr position)
        {
            var wanted = new HashSet<uint>();
            if (pd != IntPtr.Zero)
            {
                var outer = (IMFPresentationDescriptor)Marshal.GetObjectForIUnknown(pd);
                try
                {
                    uint count;
                    outer.GetStreamDescriptorCount(out count);
                    for (uint i = 0; i < count; i++)
                    {
                        int sel;
                        IntPtr sd;
                        if (outer.GetStreamDescriptorByIndex(i, out sel, out sd) < 0) continue;
                        if (sel != 0) wanted.Add(StreamId(sd));
                        Marshal.Release(sd);
                    }
                }
                finally { Marshal.ReleaseComObject(outer); }
            }
            IntPtr innerPtr;
            int hr = inner.CreatePresentationDescriptor(out innerPtr);
            if (hr < 0) return hr;
            var innerPd = (IMFPresentationDescriptor)Marshal.GetObjectForIUnknown(innerPtr);
            try
            {
                uint count;
                innerPd.GetStreamDescriptorCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    int sel;
                    IntPtr sd;
                    if (innerPd.GetStreamDescriptorByIndex(i, out sel, out sd) < 0) continue;
                    bool on = wanted.Contains(StreamId(sd));
                    Marshal.Release(sd);
                    if (on) innerPd.SelectStream(i); else innerPd.DeselectStream(i);
                }
                return inner.Start(innerPtr, timeFormat, position);
            }
            finally { Marshal.ReleaseComObject(innerPd); Marshal.Release(innerPtr); }
        }

        static int Safe(Func<int> call)
        {
            try { return call(); }
            catch (Exception ex) { return ex is COMException ? ((COMException)ex).ErrorCode : MF.E_FAIL; }
        }

        // ---- IMFMediaEventGenerator (straight through: events carry the real source's streams)
        int IMFMediaEventGenerator.GetEvent(uint flags, out IntPtr evt) { return inner.GetEvent(flags, out evt); }
        int IMFMediaEventGenerator.BeginGetEvent(IntPtr callback, IntPtr state) { return inner.BeginGetEvent(callback, state); }
        int IMFMediaEventGenerator.EndGetEvent(IntPtr result, out IntPtr evt) { return inner.EndGetEvent(result, out evt); }
        int IMFMediaEventGenerator.QueueEvent(uint type, IntPtr ext, int status, IntPtr value) { return inner.QueueEvent(type, ext, status, value); }

        // ---- IMFMediaSource
        int IMFMediaSource.GetEvent(uint flags, out IntPtr evt) { return inner.GetEvent(flags, out evt); }
        int IMFMediaSource.BeginGetEvent(IntPtr callback, IntPtr state) { return inner.BeginGetEvent(callback, state); }
        int IMFMediaSource.EndGetEvent(IntPtr result, out IntPtr evt) { return inner.EndGetEvent(result, out evt); }
        int IMFMediaSource.QueueEvent(uint type, IntPtr ext, int status, IntPtr value) { return inner.QueueEvent(type, ext, status, value); }
        int IMFMediaSource.GetCharacteristics(out uint characteristics) { return inner.GetCharacteristics(out characteristics); }
        int IMFMediaSource.CreatePresentationDescriptor(out IntPtr pd)
        {
            IntPtr r = IntPtr.Zero;
            int hr = Safe(() => CreatePresentationDescriptor(out r));
            pd = r;
            return hr;
        }
        int IMFMediaSource.Start(IntPtr pd, IntPtr timeFormat, IntPtr position) { return Safe(() => Start(pd, timeFormat, position)); }
        int IMFMediaSource.Stop() { return inner.Stop(); }
        int IMFMediaSource.Pause() { return inner.Pause(); }
        int IMFMediaSource.Shutdown() { return inner.Shutdown(); }

        // ---- IMFGetService (rate control and the like come from the real source)
        int IMFGetService.GetService(IntPtr service, IntPtr riid, out IntPtr obj)
        {
            obj = IntPtr.Zero;
            var gs = inner as IMFGetService;
            return gs == null ? MF.MF_E_UNSUPPORTED_SERVICE : gs.GetService(service, riid, out obj);
        }
    }

    [ComImport, Guid("2f69d622-20b5-41e9-afdf-89ced1dda04e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEngineExtension
    {
        [PreserveSig] int CanPlayType(int audioOnly, [MarshalAs(UnmanagedType.BStr)] string mimeType, out int answer);
        [PreserveSig] int BeginCreateObject([MarshalAs(UnmanagedType.BStr)] string url, IntPtr byteStream, uint type, out IntPtr cancelCookie, IntPtr callback, IntPtr state);
        [PreserveSig] int CancelObjectCreation(IntPtr cancelCookie);
        [PreserveSig] int EndCreateObject(IntPtr result, out IntPtr obj);
    }

    [ComImport, Guid("FBE5A32D-A497-4B61-BB85-97B1A848A6E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceResolver
    {
        [PreserveSig] int CreateObjectFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, uint flags, IntPtr props, out uint objectType, out IntPtr obj);
        [PreserveSig] int CreateObjectFromByteStream(IntPtr byteStream, [MarshalAs(UnmanagedType.LPWStr)] string url, uint flags, IntPtr props, out uint objectType, out IntPtr obj);
    }

    [ComImport, Guid("AC6B7889-0740-4D51-8619-905994A55CC6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAsyncResult
    {
        [PreserveSig] int GetState(out IntPtr state);
        [PreserveSig] int GetStatus();
        [PreserveSig] int SetStatus(int status);
        [PreserveSig] int GetObject(out IntPtr obj);
        [PreserveSig] IntPtr GetStateNoAddRef();
    }

    [ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEventGenerator
    {
        [PreserveSig] int GetEvent(uint flags, out IntPtr evt);
        [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
        [PreserveSig] int EndGetEvent(IntPtr result, out IntPtr evt);
        [PreserveSig] int QueueEvent(uint type, IntPtr extendedType, int status, IntPtr value);
    }

    // IMFMediaSource : IMFMediaEventGenerator (base methods declared in order).
    [ComImport, Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaSource
    {
        [PreserveSig] int GetEvent(uint flags, out IntPtr evt);
        [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
        [PreserveSig] int EndGetEvent(IntPtr result, out IntPtr evt);
        [PreserveSig] int QueueEvent(uint type, IntPtr extendedType, int status, IntPtr value);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
        [PreserveSig] int CreatePresentationDescriptor(out IntPtr pd);
        [PreserveSig] int Start(IntPtr pd, IntPtr timeFormat, IntPtr startPosition);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Shutdown();
    }

    [ComImport, Guid("FA993888-4383-415A-A930-DD472A8CF6F7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFGetService
    {
        [PreserveSig] int GetService(IntPtr service, IntPtr riid, out IntPtr obj);
    }

    // IMFPresentationDescriptor : IMFAttributes. The 30 inherited slots are placeholders; cast to IMFAttributes for them.
    [ComImport, Guid("03CB2711-24D7-4DB6-A17F-F3A7A479A536"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFPresentationDescriptor
    {
        void _a01(); void _a02(); void _a03(); void _a04(); void _a05(); void _a06(); void _a07(); void _a08(); void _a09(); void _a10();
        void _a11(); void _a12(); void _a13(); void _a14(); void _a15(); void _a16(); void _a17(); void _a18(); void _a19(); void _a20();
        void _a21(); void _a22(); void _a23(); void _a24(); void _a25(); void _a26(); void _a27(); void _a28(); void _a29(); void _a30();
        [PreserveSig] int GetStreamDescriptorCount(out uint count);
        [PreserveSig] int GetStreamDescriptorByIndex(uint index, out int selected, out IntPtr descriptor);
        [PreserveSig] int SelectStream(uint index);
        [PreserveSig] int DeselectStream(uint index);
        [PreserveSig] int Clone(out IntPtr pd);
    }

    // IMFStreamDescriptor : IMFAttributes
    [ComImport, Guid("56C03D9C-9DBB-45F5-AB4B-D80F47C05938"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFStreamDescriptor
    {
        void _a01(); void _a02(); void _a03(); void _a04(); void _a05(); void _a06(); void _a07(); void _a08(); void _a09(); void _a10();
        void _a11(); void _a12(); void _a13(); void _a14(); void _a15(); void _a16(); void _a17(); void _a18(); void _a19(); void _a20();
        void _a21(); void _a22(); void _a23(); void _a24(); void _a25(); void _a26(); void _a27(); void _a28(); void _a29(); void _a30();
        [PreserveSig] int GetStreamIdentifier(out uint id);
        [PreserveSig] int GetMediaTypeHandler(out IMFMediaTypeHandler handler);
    }

    [ComImport, Guid("E93DCF6C-4B07-4E1E-8123-AA16ED6EADF5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaTypeHandler
    {
        [PreserveSig] int IsMediaTypeSupported(IntPtr type, IntPtr closest);
        [PreserveSig] int GetMediaTypeCount(out uint count);
        [PreserveSig] int GetMediaTypeByIndex(uint index, out IntPtr type);
        [PreserveSig] int SetCurrentMediaType(IntPtr type);
        [PreserveSig] int GetCurrentMediaType(out IntPtr type);
        [PreserveSig] int GetMajorType(out Guid major);
    }
}
