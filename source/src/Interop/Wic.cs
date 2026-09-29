using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LiveWall.Interop
{
    // Windows Imaging Component, just enough to read a picture file with any codec Windows has (WebP, HEIC, AVIF,
    // JPEG XR, camera RAW...) into a 32bpp premultiplied bitmap, scaled down on the way if it is bigger than asked.
    internal static class Wic
    {
        static readonly Guid CLSID_WICImagingFactory = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static readonly Guid PixelFormat32bppPBGRA = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc910");
        const uint GENERIC_READ = 0x80000000;
        const int DecodeMetadataCacheOnDemand = 0, InterpolationFant = 3;

        public static Bitmap Load(string path, int maxW, int maxH)
        {
            IWICImagingFactory factory = null;
            IWICBitmapDecoder decoder = null;
            IWICBitmapFrameDecode frame = null;
            IWICBitmapScaler scaler = null;
            IWICFormatConverter converter = null;
            try
            {
                factory = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_WICImagingFactory));
                if (factory.CreateDecoderFromFilename(path, IntPtr.Zero, GENERIC_READ, DecodeMetadataCacheOnDemand, out decoder) < 0) return null;
                if (decoder.GetFrame(0, out frame) < 0) return null;
                uint w, h;
                if (frame.GetSize(out w, out h) < 0 || w == 0 || h == 0) return null;
                IWICBitmapSource source = (IWICBitmapSource)frame;
                double k = Math.Min(1.0, Math.Min(maxW / (double)w, maxH / (double)h));
                if (k < 1 && factory.CreateBitmapScaler(out scaler) >= 0)
                {
                    uint sw = (uint)Math.Max(1, Math.Round(w * k)), sh = (uint)Math.Max(1, Math.Round(h * k));
                    if (scaler.Initialize(source, sw, sh, InterpolationFant) >= 0) { source = (IWICBitmapSource)scaler; w = sw; h = sh; }
                }
                if (factory.CreateFormatConverter(out converter) < 0) return null;
                Guid format = PixelFormat32bppPBGRA;
                if (converter.Initialize(source, ref format, 0, IntPtr.Zero, 0, 0) < 0) return null;
                var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppPArgb);
                var bits = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                int hr;
                try { hr = converter.CopyPixels(IntPtr.Zero, (uint)bits.Stride, (uint)bits.Stride * h, bits.Scan0); }
                finally { bmp.UnlockBits(bits); }
                if (hr >= 0) return bmp;
                bmp.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("Picture file could not be read (" + Path.GetFileName(path) + "): " + ex.Message);
                return null;
            }
            finally
            {
                Release(converter);
                Release(scaler);
                Release(frame);
                Release(decoder);
                Release(factory);
            }
        }

        static void Release(object o) { if (o != null) Marshal.ReleaseComObject(o); }

        // Vtable order as in wincodec.h; unused methods are placeholders.
        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICImagingFactory
        {
            [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr vendor, uint access, int options, out IWICBitmapDecoder decoder);
            void CreateDecoderFromStream();
            void CreateDecoderFromFileHandle();
            void CreateComponentInfo();
            void CreateDecoder();
            void CreateEncoder();
            void CreatePalette();
            [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
            [PreserveSig] int CreateBitmapScaler(out IWICBitmapScaler scaler);
        }

        [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapDecoder
        {
            void QueryCapability();
            void Initialize();
            void GetContainerFormat();
            void GetDecoderInfo();
            void CopyPalette();
            void GetMetadataQueryReader();
            void GetPreview();
            void GetColorContexts();
            void GetThumbnail();
            void GetFrameCount();
            [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
        }

        [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapSource
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        }

        [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapFrameDecode
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        }

        [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICFormatConverter
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
            [PreserveSig] int Initialize(IWICBitmapSource source, ref Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteType);
        }

        [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapScaler
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
            [PreserveSig] int Initialize(IWICBitmapSource source, uint width, uint height, int mode);
        }
    }
}
