using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Kudurescue.Core
{
    /// <summary>Random access to a drive with a small cache, for walking file structures during a Deep scan.</summary>
    public sealed class ByteSource
    {
        private const int PageSize = 64 * 1024;
        private readonly IBlockDevice _device;
        private readonly Dictionary<long, byte[]> _pages = new Dictionary<long, byte[]>();
        private readonly Queue<long> _order = new Queue<long>();

        public ByteSource(IBlockDevice device)
        {
            _device = device;
            Length = device.Length;
        }

        public long Length { get; }

        public int Byte(long offset)
        {
            if (offset < 0 || offset >= Length) return -1;
            var page = Page(offset / PageSize);
            var i = (int)(offset % PageSize);
            return i < page.Length ? page[i] : -1;
        }

        /// <summary>Up to <paramref name="count"/> bytes; fewer at the end of the drive.</summary>
        public byte[] Read(long offset, int count)
        {
            if (offset < 0 || offset >= Length || count <= 0) return new byte[0];
            count = (int)Math.Min(count, Length - offset);
            var result = new byte[count];
            var done = 0;
            while (done < count)
            {
                var pos = offset + done;
                var page = Page(pos / PageSize);
                var i = (int)(pos % PageSize);
                var n = Math.Min(count - done, page.Length - i);
                if (n <= 0) break;
                Buffer.BlockCopy(page, i, result, done, n);
                done += n;
            }
            return done == count ? result : result.Take(done).ToArray();
        }

        private byte[] Page(long index)
        {
            if (_pages.TryGetValue(index, out var page)) return page;
            var start = index * PageSize;
            var n = (int)Math.Max(0, Math.Min(PageSize, Length - start));
            page = new byte[n];
            if (n > 0) SafeRead.Read(_device, start, page, 0, n);
            _pages[index] = page;
            _order.Enqueue(index);
            while (_order.Count > 256) _pages.Remove(_order.Dequeue());
            return page;
        }

        public uint U16BE(long o) => (uint)((Byte(o) << 8) | Byte(o + 1)) & 0xFFFF;
        public uint U16LE(long o) => (uint)(Byte(o) | (Byte(o + 1) << 8)) & 0xFFFF;
        public uint U32BE(long o) => ((uint)Byte(o) << 24) | ((uint)Byte(o + 1) << 16) | ((uint)Byte(o + 2) << 8) | (uint)Byte(o + 3);
        public uint U32LE(long o) => (uint)Byte(o) | ((uint)Byte(o + 1) << 8) | ((uint)Byte(o + 2) << 16) | ((uint)Byte(o + 3) << 24);
        public ulong U64LE(long o) => U32LE(o) | ((ulong)U32LE(o + 4) << 32);
        public ulong U64BE(long o) => ((ulong)U32BE(o) << 32) | U32BE(o + 4);

        public bool Ascii(long o, string text)
        {
            for (var i = 0; i < text.Length; i++) if (Byte(o + i) != text[i]) return false;
            return true;
        }

        /// <summary>The first position of <paramref name="needle"/> in [from, to), or -1.</summary>
        public long Find(long from, long to, byte[] needle)
        {
            const int window = 1 << 20;
            to = Math.Min(to, Length);
            for (var pos = from; pos < to; pos += window - needle.Length)
            {
                var data = Read(pos, (int)Math.Min(window, to - pos));
                var i = IndexOf(data, needle);
                if (i >= 0) return pos + i;
                if (data.Length < window) break;
            }
            return -1;
        }

        public static int IndexOf(byte[] data, byte[] needle, int start = 0)
        {
            for (var i = start; i <= data.Length - needle.Length; i++)
            {
                if (data[i] != needle[0]) continue;
                var k = 1;
                while (k < needle.Length && data[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }

    /// <summary>What a parser found at a position: the file's type and, when its structure says so, its exact length.</summary>
    public sealed class Carved
    {
        public string Extension;
        public long Length;      // 0 = unknown: the file is taken up to the next file found (or a limit)
        public long MaxLength;   // the limit used when the length is unknown

        public bool Exact => Length > 0;
    }

    /// <summary>
    /// Recognises files by their first bytes and works out where each one ends from its internal structure
    /// (JPEG segments, PNG chunks, ZIP's central directory, MP4 boxes, …). Used by Deep scan.
    /// </summary>
    public static class Carvers
    {
        private const long MB = 1024 * 1024;
        private const long MaxFile = 8L * 1024 * MB;

        /// <summary>Checks for a file starting at <paramref name="start"/>. <paramref name="head"/> holds its first bytes (at least 16 when available).</summary>
        public static Carved Detect(ByteSource s, long start, byte[] head)
        {
            if (head.Length < 16) return null;
            try
            {
                switch (head[0])
                {
                    case 0xFF when head[1] == 0xD8 && head[2] == 0xFF: return Jpeg(s, start);
                    case 0x89 when Starts(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A): return Png(s, start);
                    case (byte)'G' when Text(head, 0, "GIF87a") || Text(head, 0, "GIF89a"): return Gif(s, start);
                    case (byte)'B' when head[1] == (byte)'M': return Bmp(s, start);
                    case (byte)'I' when Starts(head, 0x49, 0x49, 0x2A, 0x00): return Tiff(s, start, false);
                    case (byte)'M' when Starts(head, 0x4D, 0x4D, 0x00, 0x2A): return Tiff(s, start, true);
                    case (byte)'M' when head[1] == (byte)'Z': return Pe(s, start);
                    case (byte)'%' when Text(head, 0, "%PDF-"): return Pdf(s, start);
                    case (byte)'P' when Starts(head, 0x50, 0x4B, 0x03, 0x04): return Zip(s, start);
                    case 0xD0 when Starts(head, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1): return Ole(s, start);
                    case (byte)'{' when Text(head, 0, "{\\rtf1"): return Rtf(s, start);
                    case (byte)'R' when Text(head, 0, "RIFF"): return Riff(s, start);
                    case (byte)'R' when Text(head, 0, "Rar!\x1A\x07"): return Unknown(".rar", 2048 * MB);
                    case 0x1A when Starts(head, 0x1A, 0x45, 0xDF, 0xA3): return Matroska(s, start);
                    case (byte)'I' when Text(head, 0, "ID3"): return Mp3(s, start, true);
                    case (byte)'O' when Text(head, 0, "OggS") && head[5] == 0x02: return Ogg(s, start);
                    case (byte)'O' when Text(head, 0, "OTTO"): return Font(s, start, ".otf");
                    case (byte)'f' when Text(head, 0, "fLaC"): return Unknown(".flac", 512 * MB);
                    case (byte)'7' when Starts(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C): return SevenZip(s, start);
                    case 0x1F when head[1] == 0x8B && head[2] == 0x08: return Unknown(".gz", 1024 * MB);
                    case (byte)'S' when Text(head, 0, "SQLite format 3\0"): return Sqlite(s, start);
                    case 0x00 when Starts(head, 0x00, 0x01, 0x00, 0x00): return Font(s, start, ".ttf");
                    case (byte)'8' when Text(head, 0, "8BPS"): return Unknown(".psd", 1024 * MB);
                    case (byte)'A' when Text(head, 0, "AC10"): return Unknown(".dwg", 256 * MB);
                    case (byte)'!' when Text(head, 0, "!BDN"): return Unknown(".pst", 4096 * MB);
                    case (byte)'B' when Text(head, 0, "BEGIN:VCARD"): return TextUntil(s, start, ".vcf", "END:VCARD");
                    case (byte)'B' when Text(head, 0, "BEGIN:VCALENDAR"): return TextUntil(s, start, ".ics", "END:VCALENDAR");
                    case (byte)'<' when Text(head, 0, "<svg") || (Text(head, 0, "<?xml") && Contains(s, start, 1024, "<svg")): return TextUntil(s, start, ".svg", "</svg>");
                    case (byte)'<' when Text(Lower(head), 0, "<!doctype html") || Text(Lower(head), 0, "<html"): return TextUntil(s, start, ".html", "</html>", true);
                }
                if (Text(head, 4, "ftyp") && BE32(head, 0) >= 8 && BE32(head, 0) <= 512) return IsoMedia(s, start, head);
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        private static Carved Unknown(string ext, long max) => new Carved { Extension = ext, MaxLength = max };
        private static Carved Done(string ext, long length) => length > 0 && length <= MaxFile ? new Carved { Extension = ext, Length = length } : null;

        // ------------------------------------------------------------------ pictures

        /// <summary>JPEG: segments with lengths, then compressed data (where FF is followed by 00 or a restart marker) up to the end marker.</summary>
        private static Carved Jpeg(ByteSource s, long start)
        {
            var pos = start + 2;
            var limit = Math.Min(s.Length, start + 256 * MB);
            var segments = 0;
            while (pos < limit)
            {
                if (s.Byte(pos) != 0xFF) return null;
                while (s.Byte(pos) == 0xFF) pos++;
                var marker = s.Byte(pos++);
                if (marker < 0) return null;
                if (marker == 0xD9) return segments > 0 ? Done(".jpg", pos - start) : null;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                var length = s.U16BE(pos);
                if (length < 2) return null;
                pos += length;
                segments++;
                if (marker != 0xDA) continue;

                // Compressed data: look for the next real marker.
                var window = 1 << 16;
                var found = false;
                while (!found && pos < limit)
                {
                    var data = s.Read(pos, window + 1);
                    if (data.Length < 2) return null;
                    for (var i = 0; i < data.Length - 1; i++)
                    {
                        if (data[i] != 0xFF) continue;
                        var next = data[i + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7) || next == 0xFF) continue;
                        pos += i;
                        found = true;
                        break;
                    }
                    if (!found) pos += data.Length - 1;
                }
            }
            return null;
        }

        private static Carved Png(ByteSource s, long start)
        {
            var pos = start + 8;
            var limit = Math.Min(s.Length, start + 512 * MB);
            for (var chunks = 0; pos + 12 <= limit; chunks++)
            {
                var length = s.U32BE(pos);
                var type = s.Read(pos + 4, 4);
                if (type.Length < 4 || type.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))) return null;
                if (chunks == 0 && Encoding.ASCII.GetString(type) != "IHDR") return null;
                pos += 12L + length;
                if (Encoding.ASCII.GetString(type) == "IEND") return Done(".png", pos - start);
            }
            return null;
        }

        private static Carved Gif(ByteSource s, long start)
        {
            var flags = s.Byte(start + 10);
            var pos = start + 13 + ((flags & 0x80) != 0 ? 3 * (2 << (flags & 7)) : 0);
            var limit = Math.Min(s.Length, start + 256 * MB);
            while (pos < limit)
            {
                var b = s.Byte(pos);
                if (b == 0x3B) return Done(".gif", pos + 1 - start);
                if (b == 0x21) pos += 2;
                else if (b == 0x2C)
                {
                    var local = s.Byte(pos + 9);
                    pos += 10 + ((local & 0x80) != 0 ? 3 * (2 << (local & 7)) : 0) + 1;
                }
                else return null;
                int size;
                while ((size = s.Byte(pos)) > 0) pos += 1 + size; // data sub-blocks
                if (size < 0) return null;
                pos++;
            }
            return null;
        }

        private static Carved Bmp(ByteSource s, long start)
        {
            var size = s.U32LE(start + 2);
            var dataOffset = s.U32LE(start + 10);
            var header = s.U32LE(start + 14);
            if (s.U32LE(start + 6) != 0 || dataOffset >= size || !new uint[] { 12, 40, 52, 56, 108, 124 }.Contains(header) || size < 64) return null;
            return Done(".bmp", size);
        }

        /// <summary>TIFF and the camera RAW files based on it: the file ends where its last image data or table ends.</summary>
        private static Carved Tiff(ByteSource s, long start, bool bigEndian)
        {
            uint U16(long o) => bigEndian ? s.U16BE(o) : s.U16LE(o);
            uint U32(long o) => bigEndian ? s.U32BE(o) : s.U32LE(o);
            long end = 8;
            var make = string.Empty;
            var dng = false;
            var queue = new Queue<long>();
            var seen = new HashSet<long>();
            queue.Enqueue(U32(start + 4));
            while (queue.Count > 0 && seen.Count < 64)
            {
                var ifd = queue.Dequeue();
                if (ifd < 8 || ifd > 512 * MB || !seen.Add(ifd)) continue;
                var count = U16(start + ifd);
                if (count == 0 || count > 1000) continue;
                end = Math.Max(end, ifd + 2 + count * 12 + 4);
                var offsets = new Dictionary<uint, List<long>>();
                for (var i = 0; i < count; i++)
                {
                    var e = start + ifd + 2 + i * 12;
                    var tag = U16(e);
                    var type = U16(e + 2);
                    var n = U32(e + 4);
                    var unit = type == 3 || type == 8 ? 2 : type == 4 || type == 9 || type == 11 || type == 13 ? 4 : type == 5 || type == 10 || type == 12 ? 8 : 1;
                    var bytes = (long)n * unit;
                    var valueAt = bytes > 4 ? U32(e + 8) : (long)(e + 8 - start);
                    if (bytes > 4) end = Math.Max(end, valueAt + bytes);
                    if (tag == 271) make = Encoding.ASCII.GetString(s.Read(start + valueAt, (int)Math.Min(bytes, 32))).Trim('\0', ' ').ToUpperInvariant();
                    if (tag == 50706) dng = true;
                    if (tag == 273 || tag == 279 || tag == 324 || tag == 325 || tag == 513 || tag == 514 || tag == 330 || tag == 34665)
                    {
                        var list = new List<long>();
                        for (var k = 0; k < Math.Min(n, 4096); k++)
                            list.Add(unit == 2 ? U16(start + valueAt + k * 2) : U32(start + valueAt + k * 4));
                        offsets[tag] = list;
                    }
                }
                void Pair(uint at, uint size)
                {
                    if (!offsets.TryGetValue(at, out var a) || !offsets.TryGetValue(size, out var b)) return;
                    for (var k = 0; k < Math.Min(a.Count, b.Count); k++) end = Math.Max(end, a[k] + b[k]);
                }
                Pair(273, 279); Pair(324, 325); Pair(513, 514);
                foreach (var sub in offsets.Where(p => p.Key == 330 || p.Key == 34665).SelectMany(p => p.Value)) queue.Enqueue(sub);
                queue.Enqueue(U32(start + ifd + 2 + count * 12));
            }
            var ext = dng ? ".dng" : make.StartsWith("CANON") ? ".cr2" : make.StartsWith("NIKON") ? ".nef" : make.StartsWith("SONY") ? ".arw" : ".tif";
            return end > 64 ? Done(ext, end) : null;
        }

        // ------------------------------------------------------------------ documents

        private static Carved Pdf(ByteSource s, long start)
        {
            var eof = Encoding.ASCII.GetBytes("%%EOF");
            var header = Encoding.ASCII.GetBytes("%PDF-");
            var limit = Math.Min(s.Length, start + 2048 * MB);
            var at = s.Find(start + 5, limit, eof);
            if (at < 0) return null;
            while (true)
            {
                // A PDF can be updated by appending, which adds another %%EOF; keep the last one before anything else starts.
                var look = Math.Min(limit, at + 5 + 4 * MB);
                var next = s.Find(at + 5, look, eof);
                var other = s.Find(at + 5, next < 0 ? look : next, header);
                if (next < 0 || other >= 0) break;
                at = next;
            }
            var end = at + 5;
            if (s.Byte(end) == '\r') end++;
            if (s.Byte(end) == '\n') end++;
            return Done(".pdf", end - start);
        }

        /// <summary>ZIP (and Office, EPUB, APK, JAR): the end record says where the central directory is, which pins the right end.</summary>
        private static Carved Zip(ByteSource s, long start)
        {
            var end = Encoding.ASCII.GetBytes("PK\x05\x06");
            var limit = Math.Min(s.Length, start + 4096 * MB);
            for (var pos = start; pos < limit;)
            {
                var at = s.Find(pos, limit, end);
                if (at < 0) return null;
                var cdSize = s.U32LE(at + 12);
                var cdOffset = s.U32LE(at + 16);
                if (cdOffset + cdSize == at - start)
                {
                    var length = at + 22 + s.U16LE(at + 20) - start;
                    var names = Encoding.ASCII.GetString(s.Read(start + cdOffset, (int)Math.Min(cdSize, 1 << 20)));
                    string ext;
                    if (names.Contains("word/")) ext = ".docx";
                    else if (names.Contains("xl/")) ext = ".xlsx";
                    else if (names.Contains("ppt/")) ext = ".pptx";
                    else if (names.Contains("AndroidManifest.xml")) ext = ".apk";
                    else if (names.Contains("META-INF/container.xml")) ext = ".epub";
                    else if (names.Contains("content.xml") && names.Contains("mimetype")) ext = ".odt";
                    else if (names.Contains("META-INF/MANIFEST.MF")) ext = ".jar";
                    else ext = ".zip";
                    return Done(ext, length);
                }
                pos = at + 4;
            }
            return null;
        }

        /// <summary>Old Office files (and Outlook .msg): the file allocation table shows the last sector in use.</summary>
        private static Carved Ole(ByteSource s, long start)
        {
            var shift = (int)s.U16LE(start + 30);
            if (shift != 9 && shift != 12) return null;
            var sector = 1L << shift;
            var fatSectors = s.U32LE(start + 44);
            if (fatSectors == 0 || fatSectors > 100000) return null;
            var fatIds = new List<uint>();
            for (var i = 0; i < 109 && fatIds.Count < fatSectors; i++) fatIds.Add(s.U32LE(start + 76 + i * 4));
            var difat = s.U32LE(start + 68);
            for (var guard = 0; fatIds.Count < fatSectors && difat < 0xFFFFFFFA && guard < 10000; guard++)
            {
                var at = start + (difat + 1) * sector;
                var per = (int)(sector / 4) - 1;
                for (var i = 0; i < per && fatIds.Count < fatSectors; i++) fatIds.Add(s.U32LE(at + i * 4));
                difat = s.U32LE(at + per * 4);
            }
            long last = -1;
            var perFat = sector / 4;
            for (var f = 0; f < fatIds.Count; f++)
            {
                if (fatIds[f] >= 0xFFFFFFFA) continue;
                var data = s.Read(start + (fatIds[f] + 1) * sector, (int)sector);
                for (var i = 0; i + 4 <= data.Length; i += 4)
                    if (BitConverter.ToUInt32(data, i) != 0xFFFFFFFF) last = Math.Max(last, f * perFat + i / 4);
            }
            if (last < 0) return null;
            var length = (last + 2) * sector;

            var dirStart = s.U32LE(start + 48);
            var dir = Encoding.Unicode.GetString(s.Read(start + (dirStart + 1) * sector, (int)Math.Min(4 * sector, 16384)));
            var ext = dir.Contains("WordDocument") ? ".doc" : dir.Contains("Workbook") || dir.Contains("Book") ? ".xls" :
                      dir.Contains("PowerPoint Document") ? ".ppt" : dir.Contains("__substg1.0_") ? ".msg" : ".ole";
            return Done(ext, length);
        }

        private static Carved Rtf(ByteSource s, long start)
        {
            var depth = 0;
            var limit = Math.Min(s.Length, start + 256 * MB);
            for (var pos = start; pos < limit; pos += 1 << 16)
            {
                var data = s.Read(pos, 1 << 16);
                if (data.Length == 0) return null;
                for (var i = 0; i < data.Length; i++)
                {
                    var c = data[i];
                    if (c == '\\') { i++; continue; }
                    if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) return Done(".rtf", pos + i + 1 - start);
                    else if (c == 0) return null;
                }
            }
            return null;
        }

        /// <summary>Text formats with a clear ending (contacts, calendars, SVG, web pages): up to the last closing tag before the text stops.</summary>
        private static Carved TextUntil(ByteSource s, long start, string ext, string closing, bool ignoreCase = false)
        {
            var limit = Math.Min(s.Length, start + 64 * MB);
            long lastEnd = -1;
            for (var pos = start; pos < limit; pos += 1 << 16)
            {
                var data = s.Read(pos, 1 << 16);
                var stop = Array.FindIndex(data, b => b == 0 || (b < 9 && b != 0));
                var text = Encoding.ASCII.GetString(data, 0, stop < 0 ? data.Length : stop);
                for (var i = text.IndexOf(closing, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); i >= 0;
                     i = text.IndexOf(closing, i + 1, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    lastEnd = pos + i + closing.Length;
                if (stop >= 0 || data.Length < 1 << 16) break;
            }
            if (lastEnd < 0) return null;
            while (s.Byte(lastEnd) == '\r' || s.Byte(lastEnd) == '\n') lastEnd++;
            return Done(ext, lastEnd - start);
        }

        // ------------------------------------------------------------------ audio and video

        private static Carved IsoMedia(ByteSource s, long start, byte[] head)
        {
            var brand = Encoding.ASCII.GetString(head, 8, 4);
            var pos = start;
            var limit = Math.Min(s.Length, start + MaxFile);
            bool moov = false, mdat = false;
            while (pos + 8 <= limit)
            {
                long size = s.U32BE(pos);
                var type = s.Read(pos + 4, 4);
                if (type.Length < 4 || type.Any(c => c < 0x20 || c > 0x7E)) break;
                var name = Encoding.ASCII.GetString(type);
                if (size == 1) size = (long)s.U64BE(pos + 8);
                else if (size == 0) return mdat ? Unknown(Brand(brand), 4096 * MB) : null; // runs to the end: unknown length
                if (size < 8) break;
                if (name == "moov") moov = true;
                if (name == "mdat") mdat = true;
                pos += size;
            }
            return moov || mdat ? Done(Brand(brand), Math.Min(pos, limit) - start) : null;
        }

        private static string Brand(string brand)
        {
            if (brand.StartsWith("qt")) return ".mov";
            if (brand.StartsWith("M4A") || brand.StartsWith("M4B")) return ".m4a";
            if (brand.StartsWith("hei") || brand.StartsWith("mif1") || brand.StartsWith("msf1")) return ".heic";
            if (brand.StartsWith("avif")) return ".avif";
            if (brand.StartsWith("3gp") || brand.StartsWith("3g2")) return ".3gp";
            return ".mp4";
        }

        private static Carved Riff(ByteSource s, long start)
        {
            var size = s.U32LE(start + 4);
            var form = Encoding.ASCII.GetString(s.Read(start + 8, 4));
            var ext = form == "AVI " ? ".avi" : form == "WAVE" ? ".wav" : form == "WEBP" ? ".webp" : null;
            return ext == null || size < 12 ? null : Done(ext, size + 8L);
        }

        /// <summary>Matroska and WebM: an EBML header, then one Segment element whose size covers the rest of the file.</summary>
        private static Carved Matroska(ByteSource s, long start)
        {
            long pos = start;
            if (!Vint(s, ref pos, true, out var id) || id != 0x1A45DFA3 || !Vint(s, ref pos, false, out var headerSize)) return null;
            var header = Encoding.ASCII.GetString(s.Read(pos, (int)Math.Min(headerSize, 256)));
            var ext = header.Contains("webm") ? ".webm" : ".mkv";
            pos += headerSize;
            if (!Vint(s, ref pos, true, out id) || id != 0x18538067) return null;
            var sizeStart = pos;
            if (!Vint(s, ref pos, false, out var segment)) return null;
            if (segment < 0) return Unknown(ext, 4096 * MB); // size not recorded
            return Done(ext, pos + segment - start);
        }

        private static bool Vint(ByteSource s, ref long pos, bool keepMarker, out long value)
        {
            var first = s.Byte(pos);
            value = 0;
            if (first <= 0) return false;
            var length = 1;
            while (length <= 8 && (first & (0x80 >> (length - 1))) == 0) length++;
            if (length > 8) return false;
            value = keepMarker ? first : first & (0xFF >> length);
            var allOnes = (first & (0xFF >> length)) == (0xFF >> length);
            for (var i = 1; i < length; i++)
            {
                var b = s.Byte(pos + i);
                if (b < 0) return false;
                value = (value << 8) | (uint)b;
                allOnes &= b == 0xFF;
            }
            pos += length;
            if (!keepMarker && allOnes) value = -1; // "unknown size"
            return true;
        }

        private static readonly int[] Mp3BitratesV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] Mp3BitratesV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        private static readonly int[] Mp3Rates = { 44100, 48000, 32000, 0 };

        /// <summary>MP3: the ID3 tag (if any), then audio frames whose lengths follow from their headers.</summary>
        private static Carved Mp3(ByteSource s, long start, bool id3)
        {
            var pos = start;
            if (id3)
            {
                var flags = s.Byte(start + 5);
                long size = ((s.Byte(start + 6) & 0x7F) << 21) | ((s.Byte(start + 7) & 0x7F) << 14) | ((s.Byte(start + 8) & 0x7F) << 7) | (s.Byte(start + 9) & 0x7F);
                pos += 10 + size + ((flags & 0x10) != 0 ? 10 : 0);
            }
            var frames = 0;
            var limit = Math.Min(s.Length, start + 1024 * MB);
            while (pos + 4 <= limit)
            {
                var h = s.U32BE(pos);
                if ((h & 0xFFE00000) != 0xFFE00000) break;
                var version = (h >> 19) & 3;     // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
                var layer = (h >> 17) & 3;       // 1 = Layer III
                var bitrate = (int)((h >> 12) & 15);
                var rate = (int)((h >> 10) & 3);
                if (version == 1 || layer != 1 || bitrate == 0 || bitrate == 15 || rate == 3) break;
                var kbps = version == 3 ? Mp3BitratesV1[bitrate] : Mp3BitratesV2[bitrate];
                var hz = Mp3Rates[rate] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
                var length = (version == 3 ? 144 : 72) * kbps * 1000 / hz + (int)((h >> 9) & 1);
                if (length < 21) break;
                pos += length;
                frames++;
            }
            if (s.Ascii(pos, "TAG")) pos += 128;
            return frames >= 4 ? Done(".mp3", pos - start) : null;
        }

        /// <summary>Ogg (Vorbis and Opus audio): pages with their own lengths; the last page carries an end-of-stream flag.</summary>
        private static Carved Ogg(ByteSource s, long start)
        {
            var pos = start;
            var limit = Math.Min(s.Length, start + 1024 * MB);
            var codec = Encoding.ASCII.GetString(s.Read(start + 28, 8));
            var ext = codec.Contains("OpusHead") ? ".opus" : ".ogg";
            while (pos + 27 <= limit && s.Ascii(pos, "OggS"))
            {
                var flags = s.Byte(pos + 5);
                var segments = s.Byte(pos + 26);
                long size = 27 + segments;
                var table = s.Read(pos + 27, segments);
                foreach (var b in table) size += b;
                pos += size;
                if ((flags & 0x04) != 0) return Done(ext, pos - start);
            }
            return null;
        }

        // ------------------------------------------------------------------ archives, data, programs

        private static Carved SevenZip(ByteSource s, long start)
        {
            var offset = (long)s.U64LE(start + 12);
            var size = (long)s.U64LE(start + 20);
            return offset < 0 || size <= 0 || offset > MaxFile ? null : Done(".7z", 32 + offset + size);
        }

        private static Carved Sqlite(ByteSource s, long start)
        {
            long page = s.U16BE(start + 16);
            if (page == 1) page = 65536;
            var pages = s.U32BE(start + 28);
            if (page < 512 || (page & (page - 1)) != 0 || pages == 0 || s.U32BE(start + 92) != s.U32BE(start + 24)) return Unknown(".sqlite", 1024 * MB);
            return Done(".sqlite", page * pages);
        }

        /// <summary>Windows programs: the section table (and any signature) shows where the file ends.</summary>
        private static Carved Pe(ByteSource s, long start)
        {
            var pe = s.U32LE(start + 0x3C);
            if (pe < 0x40 || pe > 1 << 20 || !s.Ascii(start + pe, "PE\0\0")) return null;
            var sections = s.U16LE(start + pe + 6);
            var optional = s.U16LE(start + pe + 20);
            var characteristics = s.U16LE(start + pe + 22);
            if (sections == 0 || sections > 96) return null;
            long end = pe + 24 + optional + sections * 40;
            var table = start + pe + 24 + optional;
            for (var i = 0; i < sections; i++)
                end = Math.Max(end, (long)s.U32LE(table + i * 40 + 20) + s.U32LE(table + i * 40 + 16));
            var magic = s.U16LE(start + pe + 24);
            var dirs = start + pe + 24 + (magic == 0x20B ? 112 : 96);
            var certOffset = s.U32LE(dirs + 4 * 8);
            var certSize = s.U32LE(dirs + 4 * 8 + 4);
            if (certOffset > 0 && certSize > 0) end = Math.Max(end, (long)certOffset + certSize);
            return Done((characteristics & 0x2000) != 0 ? ".dll" : ".exe", end);
        }

        private static Carved Font(ByteSource s, long start, string ext)
        {
            var tables = s.U16BE(start + 4);
            if (tables < 4 || tables > 100) return null;
            long end = 12 + tables * 16;
            var hasHead = false;
            for (var i = 0; i < tables; i++)
            {
                var e = start + 12 + i * 16;
                var tag = s.Read(e, 4);
                if (tag.Length < 4 || tag.Any(c => c < 0x20 || c > 0x7E)) return null;
                if (Encoding.ASCII.GetString(tag) == "head") hasHead = true;
                end = Math.Max(end, (long)s.U32BE(e + 8) + s.U32BE(e + 12));
            }
            return hasHead ? Done(ext, (end + 3) & ~3L) : null;
        }

        // ------------------------------------------------------------------ helpers

        private static bool Starts(byte[] b, params byte[] magic) => b.Length >= magic.Length && !magic.Where((m, i) => b[i] != m).Any();
        private static bool Text(byte[] b, int offset, string text) => b.Length >= offset + text.Length && Encoding.ASCII.GetString(b, offset, text.Length) == text;
        private static uint BE32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static byte[] Lower(byte[] b) => b.Select(x => x >= 'A' && x <= 'Z' ? (byte)(x + 32) : x).ToArray();
        private static bool Contains(ByteSource s, long start, int window, string text) =>
            Encoding.ASCII.GetString(s.Read(start, window)).Contains(text);
    }
}
