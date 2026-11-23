// Richer previews: a still from a video (Media Foundation, from memory) and a PDF's first page (Windows' PDF reader),
// both from the found file read in place.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace Kudurescue.Views
{
    /// <summary>
    /// Pictures for the preview that need Windows' own readers: a still from a video (Media Foundation) and the first page
    /// of a PDF (Windows' PDF reader). Both read the found file in place, in memory; nothing is written anywhere, so the
    /// drive being rescued is never touched.
    /// </summary>
    public static class RichPreview
    {
        /// <summary>The endings of videos a still can be made from.</summary>
        public static readonly System.Collections.Generic.HashSet<string> VideoKinds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".avi", ".wmv", ".asf", ".mkv", ".webm", ".mts", ".m2ts", ".ts" };

        // ------------------------------------------------------------------ PDF

        /// <summary>The first page of a PDF as a picture <paramref name="width"/> pixels wide, or null.</summary>
        public static async Task<BitmapSource> PdfPage(byte[] pdf, uint width = 480)
        {
            try
            {
                var input = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(input.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(pdf);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input);
                if (document.PageCount == 0) return null;
                using (var page = document.GetPage(0))
                {
                    var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(output, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width });
                    var bytes = new byte[output.Size];
                    using (var reader = new Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync((uint)output.Size);
                        reader.ReadBytes(bytes);
                    }
                    return Repaired.Open(bytes);
                }
            }
            catch (Exception)
            {
                // Damaged, password-protected, or Windows' PDF reader isn't there: no picture.
                return null;
            }
        }

        // ------------------------------------------------------------------ video

        /// <summary>Whether Windows' video reader is there (Windows N editions and some servers don't have it).</summary>
        public static bool CanReadVideo()
        {
            try
            {
                if (MFStartup(MfVersion, 0) < 0) return false;
                MFShutdown();
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>A still from a video, a tenth of the way in, or null when Windows can't read it.</summary>
        public static BitmapSource VideoFrame(Stream video)
        {
            if (!CanReadVideo() || MFStartup(MfVersion, 0) < 0) return null;
            IntPtr byteStream = IntPtr.Zero;
            IMFAttributes attributes = null;
            IMFSourceReader reader = null;
            IMFMediaType type = null;
            IMFSample sample = null;
            IMFMediaBuffer buffer = null;
            try
            {
                if (MFCreateMFByteStreamOnStream(new ComStream(video), out byteStream) < 0) return null;
                if (MFCreateAttributes(out attributes, 1) < 0) return null;
                var processing = SourceReaderEnableVideoProcessing;
                attributes.SetUINT32(ref processing, 1); // lets Windows turn the picture into plain colours
                if (MFCreateSourceReaderFromByteStream(byteStream, attributes, out reader) < 0) return null;

                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstVideoStream, true);
                if (MFCreateMediaType(out type) < 0) return null;
                var major = MtMajorType;
                var video_ = MediaTypeVideo;
                var sub = MtSubtype;
                var rgb = VideoFormatRgb32;
                type.SetGUID(ref major, ref video_);
                type.SetGUID(ref sub, ref rgb);
                if (reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type) < 0) return null;
                Marshal.ReleaseComObject(type);
                type = null;
                if (reader.GetCurrentMediaType(FirstVideoStream, out type) < 0) return null;
                var sizeKey = MtFrameSize;
                if (type.GetUINT64(ref sizeKey, out var size) < 0) return null;
                var width = (int)(size >> 32);
                var height = (int)(size & 0xFFFFFFFF);
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
                var strideKey = MtDefaultStride;
                var stride = type.GetUINT32(ref strideKey, out var s) >= 0 ? s : width * 4;

                // A tenth of the way in (the first frames are often black), when the length is known.
                var var = Marshal.AllocCoTaskMem(24);
                try
                {
                    for (var i = 0; i < 24; i++) Marshal.WriteByte(var, i, 0);
                    var durationKey = PdDuration;
                    if (reader.GetPresentationAttribute(MediaSource, ref durationKey, var) >= 0)
                    {
                        var duration = Marshal.ReadInt64(var, 8);
                        Marshal.WriteInt16(var, 0, 20); // VT_I8
                        Marshal.WriteInt64(var, 8, Math.Min(duration / 10, 100_000_000L));
                        var format = Guid.Empty;
                        reader.SetCurrentPosition(ref format, var);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(var);
                }

                for (var tries = 0; tries < 30 && sample == null; tries++)
                {
                    if (reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out _, out sample) < 0) return null;
                    if ((flags & 2) != 0) break; // the end
                }
                if (sample == null || sample.ConvertToContiguousBuffer(out buffer) < 0) return null;
                if (buffer.Lock(out var data, out _, out var length) < 0) return null;
                try
                {
                    var rowBytes = width * 4;
                    if (length < rowBytes * height) return null;
                    var pixels = new byte[rowBytes * height];
                    for (var y = 0; y < height; y++)
                    {
                        // A negative stride means the rows are stored bottom first.
                        var from = stride >= 0 ? y * stride : (height - 1 - y) * -stride;
                        Marshal.Copy(data + from, pixels, y * rowBytes, rowBytes);
                    }
                    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, rowBytes);
                    bitmap.Freeze();
                    return bitmap;
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                foreach (var o in new object[] { buffer, sample, type, reader, attributes })
                    if (o != null) Marshal.ReleaseComObject(o);
                if (byteStream != IntPtr.Zero) Marshal.Release(byteStream);
                MFShutdown();
            }
        }

        /// <summary>Media Foundation's constants and ids (as in Windows' own headers).</summary>
        private const int MfVersion = 0x00020070;
        private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
        private const int AllStreams = unchecked((int)0xFFFFFFFE);
        private const int MediaSource = unchecked((int)0xFFFFFFFF);
        private static readonly Guid SourceReaderEnableVideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static readonly Guid MtMajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MtSubtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MtFrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MtDefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid VideoFormatRgb32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        private static readonly Guid PdDuration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMFByteStreamOnStream(IStream stream, out IntPtr byteStream);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", ExactSpelling = true)] private static extern int MFCreateSourceReaderFromByteStream(IntPtr byteStream, IMFAttributes attributes, out IMFSourceReader reader);

        // The Media Foundation interfaces, in their exact method order. Methods not used here are only placeholders,
        // there so the ones used sit in the right place.

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            void SetUINT64(); void SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            void SetUINT64(); void SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            void GetStreamSelection();
            [PreserveSig] int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
            void GetNativeMediaType();
            [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
            [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample sample);
            void Flush(); void GetServiceForStream();
            [PreserveSig] int GetPresentationAttribute(int streamIndex, ref Guid attribute, IntPtr value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
            void GetBufferCount(); void GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
        }

        /// <summary>A .NET stream handed to Windows as a COM stream (read only; Windows may read from another thread).</summary>
        private sealed class ComStream : IStream
        {
            /// <summary>The found file.</summary>
            private readonly Stream _s;
            /// <summary>One read at a time.</summary>
            private readonly object _lock = new object();

            /// <summary>A read-only COM stream over a .NET stream (for Windows' readers).</summary>
            public ComStream(Stream s) => _s = s;

            /// <summary>Reads bytes.</summary>
            public void Read(byte[] pv, int cb, IntPtr pcbRead)
            {
                int n;
                lock (_lock)
                {
                    n = 0;
                    while (n < cb)
                    {
                        var got = _s.Read(pv, n, cb - n);
                        if (got <= 0) break;
                        n += got;
                    }
                }
                if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
            }

            /// <summary>Moves to a place.</summary>
            public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
            {
                long pos;
                lock (_lock) pos = _s.Seek(dlibMove, (SeekOrigin)dwOrigin);
                if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
            }

            /// <summary>The stream's size.</summary>
            public void Stat(out STATSTG pstatstg, int grfStatFlag)
            {
                pstatstg = new STATSTG { type = 2, cbSize = _s.Length, grfMode = 0 };
            }

            /// <summary>Can't be written.</summary>
            public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Read only", unchecked((int)0x80030005));
            /// <summary>Can't be changed.</summary>
            public void SetSize(long libNewSize) => throw new COMException("Read only", unchecked((int)0x80030005));
            /// <summary>Not done.</summary>
            public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
            /// <summary>Nothing to commit.</summary>
            public void Commit(int grfCommitFlags) { }
            /// <summary>Nothing to undo.</summary>
            public void Revert() { }
            /// <summary>Can't be locked.</summary>
            public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not supported", unchecked((int)0x80030001));
            /// <summary>Can't be locked.</summary>
            public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not supported", unchecked((int)0x80030001));
            /// <summary>Can't be copied.</summary>
            public void Clone(out IStream ppstm) => throw new NotSupportedException();
        }
    }
}
