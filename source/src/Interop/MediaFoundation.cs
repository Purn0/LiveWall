// Media Foundation COM interop (vtable order matches mfobjects.h / mfidl.h / mfreadwrite.h).
// Only the methods LiveWall calls are given real signatures; placeholders keep the vtable slots aligned.
using System;
using System.Runtime.InteropServices;

namespace LiveWall.Interop
{
    internal static partial class MF
    {
        public const uint MF_VERSION = 0x00020070;
        public const uint MFSTARTUP_FULL = 0;

        // Media types
        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");

        // Media type attributes
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new Guid("d7388766-18fe-48c6-a177-ee894867c8c4");
        public const uint MFVideoInterlace_Progressive = 2;
        public const uint eAVEncH264VProfile_Main = 77;
        public const uint eAVEncH264VProfile_High = 100;

        // Source reader / sink writer attributes
        public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
        public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
        public const uint MF_SOURCE_READERF_ERROR = 0x1;
        public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
        public const uint MF_SOURCE_READERF_STREAMTICK = 0x100;


        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll")] public static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);
        [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes attributes, out IMFSourceReader reader);
        [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromByteStream(IntPtr byteStream, IMFAttributes attributes, out IMFSourceReader reader);
        [DllImport("mfplat.dll")] static extern int MFCreateFile(int accessMode, int openMode, int flags, [MarshalAs(UnmanagedType.LPWStr)] string path, out IntPtr byteStream);

        // Opens a local file as an IMFByteStream (caller releases). Avoids URL parsing, so '#', '%' and
        // non-ASCII characters in file names are safe.
        public static IntPtr OpenFile(string path)
        {
            IntPtr stream;
            Check(MFCreateFile(1 /* MF_ACCESSMODE_READ */, 0 /* MF_OPENMODE_FAIL_IF_NOT_EXIST */, 0, path, out stream), "Open " + System.IO.Path.GetFileName(path));
            return stream;
        }

        // A URL containing only the extension: tells MF which container parser to use for a byte stream.
        public static string TypeHintUrl(string path)
        {
            string ext = System.IO.Path.GetExtension(path) ?? "";
            var sb = new System.Text.StringBuilder("file:///livewall-media");
            foreach (char c in ext) if (c < 128 && char.IsLetterOrDigit(c) || c == '.') sb.Append(c);
            return sb.ToString();
        }
        [DllImport("ole32.dll")] public static extern int PropVariantClear(IntPtr pvar);

        public static ulong Pack(uint hi, uint lo) { return ((ulong)hi << 32) | lo; }
        public static void Unpack(ulong v, out uint hi, out uint lo) { hi = (uint)(v >> 32); lo = (uint)(v & 0xFFFFFFFF); }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (0x" + hr.ToString("X8") + ")", hr);
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    // Helpers so static readonly GUID fields can be passed to the ref-GUID COM signatures.
    internal static partial class MFAttr
    {
        public static void SetU32(this IMFAttributes a, Guid key, uint value) { MF.Check(a.SetUINT32(ref key, value), "SetUINT32"); }
        public static void SetU64(this IMFAttributes a, Guid key, ulong value) { MF.Check(a.SetUINT64(ref key, value), "SetUINT64"); }
        public static void SetGuid(this IMFAttributes a, Guid key, Guid value) { MF.Check(a.SetGUID(ref key, ref value), "SetGUID"); }
        public static void SetUnk(this IMFAttributes a, Guid key, object value) { MF.Check(a.SetUnknown(ref key, value), "SetUnknown"); }
        public static bool TryGetU32(this IMFAttributes a, Guid key, out uint value) { return a.GetUINT32(ref key, out value) >= 0; }
        public static bool TryGetU64(this IMFAttributes a, Guid key, out ulong value) { return a.GetUINT64(ref key, out value) >= 0; }
        public static bool TryGetGuid(this IMFAttributes a, Guid key, out Guid value) { return a.GetGUID(ref key, out value) >= 0; }
        public static byte[] TryGetBlob(this IMFAttributes a, Guid key)
        {
            uint size;
            if (a.GetBlobSize(ref key, out size) < 0 || size == 0) return null;
            var buf = new byte[size];
            uint actual;
            return a.GetBlob(ref key, buf, size, out actual) >= 0 ? buf : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFVideoNormalizedRect { public float left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFRect { public int left, top, right, bottom; }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem([In] ref Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType([In] ref Guid key, out int type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr buffer, uint cch, IntPtr pcch);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint cch);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint size, out uint actual);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, [In] byte[] buffer, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    // IMFMediaType : IMFAttributes. The 30 inherited slots are placeholders; cast to IMFAttributes for them.
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        void _a01(); void _a02(); void _a03(); void _a04(); void _a05(); void _a06(); void _a07(); void _a08(); void _a09(); void _a10();
        void _a11(); void _a12(); void _a13(); void _a14(); void _a15(); void _a16(); void _a17(); void _a18(); void _a19(); void _a20();
        void _a21(); void _a22(); void _a23(); void _a24(); void _a25(); void _a26(); void _a27(); void _a28(); void _a29(); void _a30();
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr ppv);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr pv);
    }

    // IMFSample : IMFAttributes
    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        void _a01(); void _a02(); void _a03(); void _a04(); void _a05(); void _a06(); void _a07(); void _a08(); void _a09(); void _a10();
        void _a11(); void _a12(); void _a13(); void _a14(); void _a15(); void _a16(); void _a17(); void _a18(); void _a19(); void _a20();
        void _a21(); void _a22(); void _a23(); void _a24(); void _a25(); void _a26(); void _a27(); void _a28(); void _a29(); void _a30();
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);
        [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType, IMFAttributes encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick(uint streamIndex, long timestamp);
        [PreserveSig] int PlaceMarker(uint streamIndex, IntPtr context);
        [PreserveSig] int NotifyEndOfSegment(uint streamIndex);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int DoFinalize();
        [PreserveSig] int GetServiceForStream(uint streamIndex, [In] ref Guid service, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetStatistics(uint streamIndex, IntPtr stats);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, IntPtr position);
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint streamFlags, out long timestamp, out IMFSample sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, [In] ref Guid service, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, [In] ref Guid attribute, IntPtr value);
    }

}
