using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LiveWall.Interop;

namespace LiveWall
{
    // Runs slow work off the UI thread, one job at a time at below-normal priority:
    // GIF -> H.264 conversion, first-frame snapshots, and native-wallpaper changes (cross-process calls into Explorer).
    internal sealed class MediaWorker : IDisposable
    {
        sealed class Job
        {
            public string Key;
            public bool High;
            public Func<bool> Work;
            public List<Action<bool>> Done = new List<Action<bool>>();
        }

        readonly SynchronizationContext ui;
        readonly Thread thread;
        readonly object gate = new object();
        readonly List<Job> queue = new List<Job>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        volatile bool stopping;
        string runningKey;

        public MediaWorker(SynchronizationContext uiContext)
        {
            ui = uiContext;
            thread = new Thread(Run) { IsBackground = true, Name = "LiveWall worker", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public bool IsBusyWith(string key)
        {
            lock (gate) return runningKey == key || queue.Any(j => j.Key == key);
        }

        // Jobs with the same key are merged; `done` runs on the UI thread with the job's result.
        public void Enqueue(string key, bool high, Func<bool> work, Action<bool> done)
        {
            lock (gate)
            {
                Job existing = queue.FirstOrDefault(j => j.Key == key);
                if (existing != null)
                {
                    if (done != null) existing.Done.Add(done);
                    if (high && !existing.High) { existing.High = true; queue.Remove(existing); queue.Insert(0, existing); }
                    return;
                }
                var job = new Job { Key = key, High = high, Work = work };
                if (done != null) job.Done.Add(done);
                if (high) queue.Insert(queue.TakeWhile(j => j.High).Count(), job); else queue.Add(job);
            }
            signal.Set();
        }

        // Replaces any queued job with the same key (used for "set the Windows wallpaper": only the latest matters).
        public void EnqueueLatest(string key, Func<bool> work, Action<bool> done)
        {
            lock (gate) queue.RemoveAll(j => j.Key == key);
            Enqueue(key, true, work, done);
        }

        void Run()
        {
            MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL);
            while (!stopping)
            {
                Job job = null;
                lock (gate)
                {
                    if (queue.Count > 0) { job = queue[0]; queue.RemoveAt(0); runningKey = job.Key; }
                }
                if (job == null) { signal.WaitOne(); continue; }
                bool ok = false;
                try { ok = job.Work(); }
                catch (Exception ex) { Log.Error("Job " + job.Key + " failed", ex); }
                lock (gate) runningKey = null;
                var callbacks = job.Done;
                ui.Post(_ => { foreach (var cb in callbacks) { try { cb(ok); } catch (Exception ex) { Log.Error("Job callback", ex); } } }, null);
            }
        }

        public void Dispose()
        {
            stopping = true;
            signal.Set();
        }

        public void Post(Action a) { ui.Post(_ => a(), null); }

        // ------------------------------------------------------------------------------------------------------
        // GIF handling
        // ------------------------------------------------------------------------------------------------------

        public static bool IsAnimatedGif(string path)
        {
            try
            {
                using (var img = Image.FromFile(path))
                    return img.FrameDimensionsList.Length > 0 && img.GetFrameCount(new FrameDimension(img.FrameDimensionsList[0])) > 1;
            }
            catch { return false; }
        }

        // Converts an animated GIF to an H.264 MP4 once, so it plays through the same hardware video decoder as any
        // video (a GIF decoded on the CPU every frame costs far more battery). Small GIFs are upscaled by an integer
        // factor with nearest-neighbour sampling so pixel art stays crisp. Frame timing follows the GIF's delays.
        public static bool ConvertGif(string gifPath, string outPath, int targetW, int targetH, Action<int> progress)
        {
            var sw = Stopwatch.StartNew();
            string tmp = Path.Combine(Path.GetDirectoryName(outPath), Path.GetFileNameWithoutExtension(outPath) + ".part.mp4");
            bool ok;
            try { ok = EncodeGif(gifPath, tmp, targetW, targetH, progress, true); }
            catch (Exception ex) { Log.Warn("Hardware encode failed (" + ex.Message + "), retrying in software"); ok = false; }
            if (!ok)
            {
                TryDelete(tmp);
                ok = EncodeGif(gifPath, tmp, targetW, targetH, progress, false);
            }
            if (!ok) { TryDelete(tmp); return false; }
            TryDelete(outPath);
            File.Move(tmp, outPath);
            Log.Info(string.Format("Converted GIF {0} -> {1} ({2:N0} KB) in {3} ms", Path.GetFileName(gifPath), Path.GetFileName(outPath), new FileInfo(outPath).Length / 1024, sw.ElapsedMilliseconds));
            return true;
        }

        static bool EncodeGif(string gifPath, string outPath, int targetW, int targetH, Action<int> progress, bool hardware)
        {
            using (var img = Image.FromFile(gifPath))
            {
                var dim = new FrameDimension(img.FrameDimensionsList[0]);
                int frames = img.GetFrameCount(dim);
                if (frames < 2) return false;
                int[] delays = FrameDelays(img, frames);
                int w = img.Width, h = img.Height;

                // Small pixel-art GIFs are upscaled by a whole-number factor with nearest-neighbour sampling so they
                // stay crisp; other GIFs keep their size and are scaled smoothly by the GPU at playback.
                int k = 1;
                if (targetW > 0 && targetH > 0 && w * 2 <= targetW && h * 2 <= targetH && LooksLikePixelArt(img))
                    k = (int)Math.Ceiling(Math.Max((double)targetW / w, (double)targetH / h));
                double scale = k;
                // Stay within what H.264 hardware handles (4096 px per side, ~8.3 MP).
                scale = Math.Min(scale, Math.Min(4096.0 / w, 4096.0 / h));
                scale = Math.Min(scale, Math.Sqrt(3840.0 * 2160.0 / ((double)w * h)));
                int drawW = Math.Max(2, (int)Math.Round(w * scale)), drawH = Math.Max(2, (int)Math.Round(h * scale));
                int ow = drawW & ~1, oh = drawH & ~1;   // NV12 needs even dimensions

                int period = FramePeriod(delays);        // ms per output frame
                long frameDuration = period * 10000L;    // 100 ns units
                double fps = 1000.0 / period;
                uint bitrate = (uint)Math.Max(1500000, Math.Min(24000000, ow * (double)oh * fps * 0.08));

                IMFAttributes attrs = null; IMFSinkWriter writer = null; IMFMediaType outType = null, inType = null;
                try
                {
                    MF.Check(MF.MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
                    if (hardware) attrs.SetU32(MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                    MF.Check(MF.MFCreateSinkWriterFromURL(outPath, IntPtr.Zero, attrs, out writer), "MFCreateSinkWriterFromURL");

                    MF.Check(MF.MFCreateMediaType(out outType), "MFCreateMediaType");
                    var o = (IMFAttributes)outType;
                    o.SetGuid(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                    o.SetGuid(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_H264);
                    o.SetU32(MF.MF_MT_AVG_BITRATE, bitrate);
                    o.SetU32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                    o.SetU64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)ow, (uint)oh));
                    o.SetU64(MF.MF_MT_FRAME_RATE, MF.Pack(1000, (uint)period));
                    o.SetU64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                    o.SetU32(MF.MF_MT_MPEG2_PROFILE, MF.eAVEncH264VProfile_High);
                    uint stream;
                    MF.Check(writer.AddStream(outType, out stream), "AddStream");

                    MF.Check(MF.MFCreateMediaType(out inType), "MFCreateMediaType");
                    var i = (IMFAttributes)inType;
                    i.SetGuid(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                    i.SetGuid(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32);
                    i.SetU32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                    i.SetU64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)ow, (uint)oh));
                    i.SetU64(MF.MF_MT_FRAME_RATE, MF.Pack(1000, (uint)period));
                    i.SetU64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                    i.SetU32(MF.MF_MT_DEFAULT_STRIDE, (uint)(ow * 4));   // top-down rows
                    MF.Check(writer.SetInputMediaType(stream, inType, null), "SetInputMediaType");
                    MF.Check(writer.BeginWriting(), "BeginWriting");

                    int frameBytes = ow * oh * 4;
                    long written = 0;
                    double elapsedMs = 0;
                    int lastReported = -1;
                    using (var canvas = new Bitmap(ow, oh, PixelFormat.Format32bppRgb))
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.InterpolationMode = scale >= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.None;
                        for (int f = 0; f < frames; f++)
                        {
                            elapsedMs += delays[f];
                            long target = (long)Math.Round(elapsedMs / period);
                            int repeats = (int)(target - written);
                            if (repeats <= 0) continue;   // shorter than one output frame
                            img.SelectActiveFrame(dim, f);  // GDI+ returns the fully composed frame
                            g.Clear(Color.Black);
                            g.DrawImage(img, new Rectangle(0, 0, drawW, drawH), 0, 0, w, h, GraphicsUnit.Pixel);

                            IMFMediaBuffer buffer;
                            MF.Check(MF.MFCreateMemoryBuffer((uint)frameBytes, out buffer), "MFCreateMemoryBuffer");
                            try
                            {
                                IntPtr dst; uint max, cur;
                                MF.Check(buffer.Lock(out dst, out max, out cur), "Lock");
                                var bits = canvas.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                                for (int y = 0; y < oh; y++) CopyMemory(dst + y * ow * 4, bits.Scan0 + y * bits.Stride, (UIntPtr)(uint)(ow * 4));
                                canvas.UnlockBits(bits);
                                buffer.Unlock();
                                buffer.SetCurrentLength((uint)frameBytes);
                                for (int r = 0; r < repeats; r++)
                                {
                                    IMFSample sample;
                                    MF.Check(MF.MFCreateSample(out sample), "MFCreateSample");
                                    try
                                    {
                                        sample.AddBuffer(buffer);
                                        sample.SetSampleTime(written * frameDuration);
                                        sample.SetSampleDuration(frameDuration);
                                        MF.Check(writer.WriteSample(stream, sample), "WriteSample");
                                        written++;
                                    }
                                    finally { MF.Release(sample); }
                                }
                            }
                            finally { MF.Release(buffer); }

                            int pct = (f + 1) * 100 / frames;
                            if (progress != null && pct / 5 != lastReported) { lastReported = pct / 5; progress(pct); }
                        }
                    }
                    if (written == 0) return false;
                    MF.Check(writer.DoFinalize(), "Finalize");
                    return true;
                }
                finally
                {
                    MF.Release(inType); MF.Release(outType); MF.Release(writer); MF.Release(attrs);
                }
            }
        }

        // Encodes `count` frames drawn by `draw` (frame index, the w x h canvas; the canvas keeps the previous frame, so
        // `draw` may repaint only what changed) as an H.264 MP4 at `fps`: the board animation loop. Hardware encoder
        // first, software if that fails; written to a .part file and moved into place when complete.
        public static bool EncodeFrames(string outPath, int w, int h, int fps, int count, Action<int, Bitmap> draw)
        {
            var sw = Stopwatch.StartNew();
            string tmp = Path.Combine(Path.GetDirectoryName(outPath), Path.GetFileNameWithoutExtension(outPath) + ".part.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            bool ok;
            try { ok = WriteFrames(tmp, w, h, fps, count, draw, true); }
            catch (Exception ex) { Log.Warn("Hardware encode failed (" + ex.Message + "), retrying in software"); ok = false; }
            if (!ok)
            {
                TryDelete(tmp);
                try { ok = WriteFrames(tmp, w, h, fps, count, draw, false); }
                catch (Exception ex) { Log.Warn("Encoding failed: " + ex.Message); ok = false; }
            }
            if (!ok) { TryDelete(tmp); return false; }
            TryDelete(outPath);
            File.Move(tmp, outPath);
            Log.Info(string.Format("Encoded {0} ({1}x{2}, {3} frames, {4:N0} KB) in {5} ms", Path.GetFileName(outPath), w, h, count, new FileInfo(outPath).Length / 1024, sw.ElapsedMilliseconds));
            return true;
        }

        static bool WriteFrames(string outPath, int w, int h, int fps, int count, Action<int, Bitmap> draw, bool hardware)
        {
            long frameDuration = 10000000L / fps;
            uint bitrate = (uint)Math.Max(1500000, Math.Min(16000000, w * (double)h * fps * 0.06));
            IMFAttributes attrs = null; IMFSinkWriter writer = null; IMFMediaType outType = null, inType = null;
            try
            {
                MF.Check(MF.MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
                if (hardware) attrs.SetU32(MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                MF.Check(MF.MFCreateSinkWriterFromURL(outPath, IntPtr.Zero, attrs, out writer), "MFCreateSinkWriterFromURL");

                MF.Check(MF.MFCreateMediaType(out outType), "MFCreateMediaType");
                var o = (IMFAttributes)outType;
                o.SetGuid(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                o.SetGuid(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_H264);
                o.SetU32(MF.MF_MT_AVG_BITRATE, bitrate);
                o.SetU32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                o.SetU64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)h));
                o.SetU64(MF.MF_MT_FRAME_RATE, MF.Pack((uint)fps, 1));
                o.SetU64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                o.SetU32(MF.MF_MT_MPEG2_PROFILE, MF.eAVEncH264VProfile_High);
                uint stream;
                MF.Check(writer.AddStream(outType, out stream), "AddStream");

                MF.Check(MF.MFCreateMediaType(out inType), "MFCreateMediaType");
                var i = (IMFAttributes)inType;
                i.SetGuid(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                i.SetGuid(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32);
                i.SetU32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                i.SetU64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)h));
                i.SetU64(MF.MF_MT_FRAME_RATE, MF.Pack((uint)fps, 1));
                i.SetU64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                i.SetU32(MF.MF_MT_DEFAULT_STRIDE, (uint)(w * 4));   // top-down rows
                MF.Check(writer.SetInputMediaType(stream, inType, null), "SetInputMediaType");
                MF.Check(writer.BeginWriting(), "BeginWriting");

                int frameBytes = w * h * 4;
                using (var canvas = new Bitmap(w, h, PixelFormat.Format32bppRgb))
                {
                    for (int f = 0; f < count; f++)
                    {
                        draw(f, canvas);
                        IMFMediaBuffer buffer;
                        MF.Check(MF.MFCreateMemoryBuffer((uint)frameBytes, out buffer), "MFCreateMemoryBuffer");
                        try
                        {
                            IntPtr dst; uint max, cur;
                            MF.Check(buffer.Lock(out dst, out max, out cur), "Lock");
                            var bits = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                            for (int y = 0; y < h; y++) CopyMemory(dst + y * w * 4, bits.Scan0 + y * bits.Stride, (UIntPtr)(uint)(w * 4));
                            canvas.UnlockBits(bits);
                            buffer.Unlock();
                            buffer.SetCurrentLength((uint)frameBytes);
                            IMFSample sample;
                            MF.Check(MF.MFCreateSample(out sample), "MFCreateSample");
                            try
                            {
                                sample.AddBuffer(buffer);
                                sample.SetSampleTime(f * frameDuration);
                                sample.SetSampleDuration(frameDuration);
                                MF.Check(writer.WriteSample(stream, sample), "WriteSample");
                            }
                            finally { MF.Release(sample); }
                        }
                        finally { MF.Release(buffer); }
                    }
                }
                MF.Check(writer.DoFinalize(), "Finalize");
                return true;
            }
            finally
            {
                MF.Release(inType); MF.Release(outType); MF.Release(writer); MF.Release(attrs);
            }
        }

        // Pixel art is made of flat runs of identical pixels; dithered photos/video captures are not.
        static bool LooksLikePixelArt(Image img)
        {
            int w = img.Width, h = img.Height;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, w, h);
                var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                var row = new int[w];
                long same = 0, total = 0;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, w);
                    for (int x = 1; x < w; x++) { if (row[x] == row[x - 1]) same++; total++; }
                }
                bmp.UnlockBits(bits);
                return total > 0 && same * 100 / total >= 55;
            }
        }

        // GIF frame delays in ms. Like browsers, delays of 0/10 ms are treated as 100 ms.
        static int[] FrameDelays(Image img, int frames)
        {
            var delays = new int[frames];
            byte[] raw = null;
            try { raw = img.GetPropertyItem(0x5100).Value; } catch { }
            for (int f = 0; f < frames; f++)
            {
                int cs = raw != null && raw.Length >= (f + 1) * 4 ? BitConverter.ToInt32(raw, f * 4) : 10;
                delays[f] = cs <= 1 ? 100 : cs * 10;
            }
            return delays;
        }

        // Output frame period: the GCD of all delays so timing is exact, clamped to 20..200 ms (50..5 fps).
        static int FramePeriod(int[] delays)
        {
            int g = 0;
            foreach (int d in delays) g = Gcd(g, d);
            return Math.Max(20, Math.Min(200, g));
        }

        static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }
        // ------------------------------------------------------------------------------------------------------
        // First-frame snapshot of a video (becomes the Windows wallpaper behind the live one)
        // ------------------------------------------------------------------------------------------------------

        public static bool ExtractSnapshot(string videoPath, string jpgPath)
        {
            IMFAttributes attrs = null; IMFSourceReader reader = null; IMFMediaType type = null, current = null;
            IMFSample sample = null; IMFMediaBuffer buffer = null;
            IntPtr stream = IntPtr.Zero;
            try
            {
                MF.Check(MF.MFCreateAttributes(out attrs, 1), "MFCreateAttributes");
                attrs.SetU32(MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);   // decoder output -> RGB32
                stream = MF.OpenFile(videoPath);
                MF.Check(MF.MFCreateSourceReaderFromByteStream(stream, attrs, out reader), "MFCreateSourceReaderFromByteStream");
                reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, 0);
                reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1);
                MF.Check(MF.MFCreateMediaType(out type), "MFCreateMediaType");
                ((IMFAttributes)type).SetGuid(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                ((IMFAttributes)type).SetGuid(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32);
                MF.Check(reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, type), "SetCurrentMediaType");
                MF.Check(reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out current), "GetCurrentMediaType");
                var ca = (IMFAttributes)current;
                ulong size;
                if (!ca.TryGetU64(MF.MF_MT_FRAME_SIZE, out size)) return false;
                uint fw, fh;
                MF.Unpack(size, out fw, out fh);
                int w = (int)fw, h = (int)fh;
                uint strideAttr;
                int stride = ca.TryGetU32(MF.MF_MT_DEFAULT_STRIDE, out strideAttr) ? (int)strideAttr : w * 4;

                // Visible area (e.g. 1920x1080 inside a 1920x1088 decoded frame).
                int cx = 0, cy = 0, cw = w, ch = h;
                byte[] ap = ca.TryGetBlob(MF.MF_MT_MINIMUM_DISPLAY_APERTURE);
                if (ap != null && ap.Length >= 16)
                {
                    int ax = BitConverter.ToInt16(ap, 2), ay = BitConverter.ToInt16(ap, 6);
                    int aw = BitConverter.ToInt32(ap, 8), ah = BitConverter.ToInt32(ap, 12);
                    if (aw > 0 && ah > 0 && ax >= 0 && ay >= 0 && ax + aw <= w && ay + ah <= h) { cx = ax; cy = ay; cw = aw; ch = ah; }
                }

                for (int tries = 0; tries < 60 && sample == null; tries++)
                {
                    uint actual, flags; long ts;
                    int hr = reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out actual, out flags, out ts, out sample);
                    if (hr < 0 || (flags & (MF.MF_SOURCE_READERF_ERROR | MF.MF_SOURCE_READERF_ENDOFSTREAM)) != 0) break;
                }
                if (sample == null) return false;
                MF.Check(sample.ConvertToContiguousBuffer(out buffer), "ConvertToContiguousBuffer");

                IntPtr scan0 = IntPtr.Zero; int pitch = 0;
                var b2 = buffer as IMF2DBuffer;
                bool locked2D = b2 != null && b2.Lock2D(out scan0, out pitch) >= 0;
                if (!locked2D)
                {
                    IntPtr p; uint max, cur;
                    MF.Check(buffer.Lock(out p, out max, out cur), "Lock");
                    pitch = stride;
                    scan0 = stride < 0 ? p + (h - 1) * -stride : p;
                }
                try
                {
                    using (var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppRgb))
                    {
                        var bits = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        for (int y = 0; y < ch; y++)
                            CopyMemory(bits.Scan0 + y * bits.Stride, scan0 + (cy + y) * pitch + cx * 4, (UIntPtr)(uint)(cw * 4));
                        bmp.UnlockBits(bits);
                        SaveJpeg(bmp, jpgPath);
                    }
                }
                finally
                {
                    if (locked2D) b2.Unlock2D(); else buffer.Unlock();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Snapshot failed for " + Path.GetFileName(videoPath) + ": " + ex.Message);
                return false;
            }
            finally
            {
                MF.Release(buffer); MF.Release(sample); MF.Release(current); MF.Release(type); MF.Release(reader); MF.Release(attrs);
                if (stream != IntPtr.Zero) Marshal.Release(stream);
            }
        }

        static void SaveJpeg(Bitmap bmp, string path)
        {
            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using (var p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                string tmp = path + ".part";
                bmp.Save(tmp, jpeg, p);
                TryDelete(path);
                File.Move(tmp, path);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Cache housekeeping
        // ------------------------------------------------------------------------------------------------------

        // Removes leftovers from interrupted conversions, cache entries unused for 30 days, and trims the cache to 4 GB.
        public static void CleanCache(ICollection<string> keepKeys)
        {
            try
            {
                var files = new DirectoryInfo(AppPaths.CacheDir).GetFiles();
                foreach (var f in files.Where(f => f.Name.Contains(".part"))) TryDelete(f.FullName);
                var live = files.Where(f => !f.Name.Contains(".part") && f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var f in live)
                {
                    string key = f.Name.Split('.')[0];   // "<key>.mp4", "<key>.video.mp4", "<key>.jpg"
                    bool keep = keepKeys.Contains(key);
                    total += f.Length;
                    if ((!keep && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30)) || total > 4L * 1024 * 1024 * 1024) TryDelete(f.FullName);
                }
            }
            catch (Exception ex) { Log.Warn("Cache cleanup: " + ex.Message); }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
    }
}
