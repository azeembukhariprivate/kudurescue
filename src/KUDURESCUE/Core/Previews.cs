using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>
    /// A found file read in place, from any position, straight from the drive (or the Recycle Bin, or a snapshot), without
    /// copying it anywhere: what the previews read. Nothing is ever written.
    /// </summary>
    public sealed class FoundStream : Stream
    {
        private readonly IVolume _volume;
        private readonly FoundFile _file;
        private readonly Stream _inner;
        private long _position;

        private FoundStream(IVolume volume, FoundFile file, Stream inner)
        {
            _volume = volume;
            _file = file;
            _inner = inner;
        }

        /// <summary>The file to read, or null when it can't be read in place.</summary>
        public static Stream Open(IVolume volume, FoundFile f, long maxInMemory = 64L << 20)
        {
            if (f == null || f.IsFolder || !f.CanRecover) return null;
            if (f.IsCopy) return File.Exists(f.RecyclePath) ? new FileStream(f.RecyclePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
            if (volume == null) return null;
            if (f.ResidentData != null) return new MemoryStream(f.ResidentData, 0, (int)Math.Min(f.Size, f.ResidentData.Length), false);
            if (f.Compressed)
            {
                // Compressed files are unpacked whole; only small ones.
                if (f.Size > maxInMemory) return null;
                var ms = new MemoryStream();
                Recovery.Write(volume, f, ms, null, CancellationToken.None);
                ms.Position = 0;
                return ms;
            }
            return f.Runs == null ? null : new FoundStream(volume, f, null);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_inner != null) return _inner.Read(buffer, offset, count);
            var total = _file.Size;
            if (_position >= total || count <= 0) return 0;
            count = (int)Math.Min(count, total - _position);
            var cluster = _volume.ClusterSize;
            var initialized = _file.InitializedSize > 0 ? _file.InitializedSize : _file.Size;
            var done = 0;
            while (done < count)
            {
                var pos = _position + done;
                var vcn = pos / cluster;
                var index = _file.Runs.FindIndex(r => vcn >= r.Vcn && vcn < r.Vcn + r.Length);
                if (index < 0) break; // past the file's map
                var run = _file.Runs[index];
                var runEnd = (run.Vcn + run.Length) * cluster;
                var n = (int)Math.Min(count - done, runEnd - pos);
                if (run.IsSparse || pos >= initialized) Array.Clear(buffer, offset + done, n);
                else
                {
                    var at = _volume.ClusterOffset(run.Lcn) + (pos - run.Vcn * cluster);
                    if (at + n > _volume.Device.Length) break;
                    SafeRead.Read(_volume.Device, at, buffer, offset + done, n);
                    if (pos + n > initialized) Array.Clear(buffer, offset + done + (int)(initialized - pos), (int)(pos + n - initialized));
                }
                done += n;
            }
            _position += done;
            return done;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_inner != null) return _inner.Seek(offset, origin);
            var to = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : _file.Size + offset;
            if (to < 0) throw new IOException("Before the start of the file.");
            return _position = to;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner?.Length ?? _file.Size;

        public override long Position
        {
            get => _inner?.Position ?? _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner?.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Reads exactly up to <paramref name="count"/> bytes at a position (fewer at the end).</summary>
        public static byte[] ReadAt(Stream s, long position, int count)
        {
            if (position < 0 || position >= s.Length || count <= 0) return new byte[0];
            s.Position = position;
            var buffer = new byte[(int)Math.Min(count, s.Length - position)];
            var got = 0;
            while (got < buffer.Length)
            {
                var n = s.Read(buffer, got, buffer.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return got == buffer.Length ? buffer : buffer.Take(got).ToArray();
        }
    }

    /// <summary>What a music or video file says about itself: its name, who made it, and how long it is.</summary>
    public sealed class MediaInfo
    {
        public string Title, Artist, Album;
        public TimeSpan Length;

        public bool IsEmpty => string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Artist) && string.IsNullOrEmpty(Album) && Length == TimeSpan.Zero;

        public static readonly IReadOnlyCollection<string> Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".m4a", ".m4b", ".aac", ".mp4", ".m4v", ".mov", ".3gp", ".flac", ".wav", ".ogg", ".oga", ".opus" };

        /// <summary>Reads the details of a music or video file, or null when its kind isn't known or nothing was found.</summary>
        public static MediaInfo Read(Stream s, string extension)
        {
            try
            {
                MediaInfo info;
                switch ((extension ?? string.Empty).ToLowerInvariant())
                {
                    case ".mp3": info = Mp3(s); break;
                    case ".m4a": case ".m4b": case ".aac": case ".mp4": case ".m4v": case ".mov": case ".3gp": info = Mp4(s); break;
                    case ".flac": info = Flac(s); break;
                    case ".wav": info = Wav(s); break;
                    case ".ogg": case ".oga": case ".opus": info = Ogg(s); break;
                    default: return null;
                }
                return info == null || info.IsEmpty ? null : info;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException || ex is InvalidDataException)
            {
                return null;
            }
        }

        private static uint Be32(byte[] b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
        private static ulong Be64(byte[] b, int p) => (ulong)Be32(b, p) << 32 | Be32(b, p + 4);
        private static uint Le32(byte[] b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);
        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace("\0", string.Empty).Trim();

        // ------------------------------------------------------------------ MP3

        private static readonly int[] Mp3Rates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };

        private static MediaInfo Mp3(Stream s)
        {
            var info = new MediaInfo();
            var head = FoundStream.ReadAt(s, 0, 10);
            long audio = 0;
            if (head.Length == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                var version = head[3];
                var size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
                audio = 10 + size;
                var tag = FoundStream.ReadAt(s, 10, Math.Min(size, 1 << 20));
                for (var p = 0; p + 10 <= tag.Length;)
                {
                    var id = Encoding.ASCII.GetString(tag, p, 4);
                    if (id[0] == 0) break;
                    var length = version >= 4 ? (tag[p + 4] & 0x7F) << 21 | (tag[p + 5] & 0x7F) << 14 | (tag[p + 6] & 0x7F) << 7 | (tag[p + 7] & 0x7F) : (int)Be32(tag, p + 4);
                    if (length <= 0 || p + 10 + length > tag.Length) break;
                    if (id == "TIT2" || id == "TPE1" || id == "TALB")
                    {
                        var text = Id3Text(tag, p + 10, length);
                        if (id == "TIT2") info.Title = text;
                        else if (id == "TPE1") info.Artist = text;
                        else info.Album = text;
                    }
                    p += 10 + length;
                }
            }
            // The first audio frame: its bit rate, and a "Xing"/"Info" header with the number of frames when there is one.
            var frames = FoundStream.ReadAt(s, audio, 4096);
            for (var p = 0; p + 4 <= frames.Length; p++)
            {
                if (frames[p] != 0xFF || (frames[p + 1] & 0xE0) != 0xE0) continue;
                var mpeg1 = (frames[p + 1] & 0x18) == 0x18;
                var layer3 = (frames[p + 1] & 0x06) == 0x02;
                var rate = Mp3Rates[frames[p + 2] >> 4];
                var sampleIndex = (frames[p + 2] >> 2) & 3;
                if (!layer3 || rate == 0 || sampleIndex == 3) continue;
                var sampleRate = new[] { 44100, 48000, 32000 }[sampleIndex] / (mpeg1 ? 1 : 2);
                var mono = (frames[p + 3] >> 6) == 3;
                var side = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
                var x = p + 4 + side;
                if (x + 12 <= frames.Length && (Encoding.ASCII.GetString(frames, x, 4) == "Xing" || Encoding.ASCII.GetString(frames, x, 4) == "Info") && (Be32(frames, x + 4) & 1) != 0)
                {
                    var count = Be32(frames, x + 8);
                    info.Length = TimeSpan.FromSeconds(count * (mpeg1 ? 1152.0 : 576.0) / sampleRate);
                }
                else info.Length = TimeSpan.FromSeconds((s.Length - audio - p) * 8.0 / (rate * 1000.0));
                break;
            }
            return info;
        }

        private static string Id3Text(byte[] tag, int p, int length)
        {
            var encoding = tag[p];
            var body = tag.Skip(p + 1).Take(length - 1).ToArray();
            switch (encoding)
            {
                case 1:
                    // Two-byte letters with a mark saying which byte comes first.
                    if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) return Clean(Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2));
                    if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) return Clean(Encoding.Unicode.GetString(body, 2, body.Length - 2));
                    return Clean(Encoding.Unicode.GetString(body));
                case 2: return Clean(Encoding.BigEndianUnicode.GetString(body));
                case 3: return Clean(Encoding.UTF8.GetString(body));
                default: return Clean(Encoding.GetEncoding("ISO-8859-1").GetString(body));
            }
        }

        // ------------------------------------------------------------------ MP4, M4A, MOV

        private static MediaInfo Mp4(Stream s)
        {
            var info = new MediaInfo();
            // Find "moov" among the top boxes (it may come after the media data, at the end).
            long pos = 0;
            while (pos + 8 <= s.Length)
            {
                var h = FoundStream.ReadAt(s, pos, 16);
                if (h.Length < 8) break;
                long size = Be32(h, 0);
                var type = Encoding.ASCII.GetString(h, 4, 4);
                var headerSize = 8;
                if (size == 1 && h.Length >= 16) { size = (long)Be64(h, 8); headerSize = 16; }
                else if (size == 0) size = s.Length - pos;
                if (size < headerSize) break;
                if (type == "moov")
                {
                    var moov = FoundStream.ReadAt(s, pos + headerSize, (int)Math.Min(size - headerSize, 16 << 20));
                    Boxes(moov, 0, moov.Length, info);
                    break;
                }
                pos += size;
            }
            return info;
        }

        private static void Boxes(byte[] b, int start, int end, MediaInfo info)
        {
            for (var p = start; p + 8 <= end;)
            {
                var size = (int)Be32(b, p);
                var type = Encoding.GetEncoding("ISO-8859-1").GetString(b, p + 4, 4);
                if (size < 8 || p + size > end) break;
                switch (type)
                {
                    case "mvhd":
                        var version = b[p + 8];
                        var timescale = version == 1 ? Be32(b, p + 28) : Be32(b, p + 20);
                        var duration = version == 1 ? Be64(b, p + 32) : Be32(b, p + 24);
                        if (timescale > 0) info.Length = TimeSpan.FromSeconds((double)duration / timescale);
                        break;
                    case "udta": case "ilst": Boxes(b, p + 8, p + size, info); break;
                    case "meta": Boxes(b, p + 12, p + size, info); break; // "meta" has 4 bytes of version and flags first
                    case "©nam": info.Title = info.Title ?? Data(b, p, size); break;
                    case "©ART": info.Artist = info.Artist ?? Data(b, p, size); break;
                    case "©alb": info.Album = info.Album ?? Data(b, p, size); break;
                }
                p += size;
            }
        }

        private static string Data(byte[] b, int p, int size)
        {
            // An item holds a "data" box: size, "data", type and locale (8 bytes), then the text.
            var d = p + 8;
            if (d + 16 > p + size || Encoding.ASCII.GetString(b, d + 4, 4) != "data") return null;
            var length = (int)Be32(b, d) - 16;
            return length > 0 ? Clean(Encoding.UTF8.GetString(b, d + 16, length)) : null;
        }

        // ------------------------------------------------------------------ FLAC, WAV, Ogg

        private static MediaInfo Flac(Stream s)
        {
            var info = new MediaInfo();
            var head = FoundStream.ReadAt(s, 0, 4);
            if (head.Length < 4 || Encoding.ASCII.GetString(head) != "fLaC") return null;
            long pos = 4;
            while (pos + 4 <= s.Length)
            {
                var h = FoundStream.ReadAt(s, pos, 4);
                var last = (h[0] & 0x80) != 0;
                var type = h[0] & 0x7F;
                var length = h[1] << 16 | h[2] << 8 | h[3];
                var body = FoundStream.ReadAt(s, pos + 4, Math.Min(length, 1 << 20));
                if (type == 0 && body.Length >= 18)
                {
                    var rate = body[10] << 12 | body[11] << 4 | body[12] >> 4;
                    var samples = ((long)(body[13] & 0x0F) << 32) | Be32(body, 14);
                    if (rate > 0) info.Length = TimeSpan.FromSeconds((double)samples / rate);
                }
                else if (type == 4) Comments(body, 0, info);
                pos += 4 + length;
                if (last) break;
            }
            return info;
        }

        /// <summary>A Vorbis comment block: the maker's name, then "TITLE=…" style lines.</summary>
        private static void Comments(byte[] b, int p, MediaInfo info)
        {
            var vendor = (int)Le32(b, p);
            p += 4 + vendor;
            var count = Le32(b, p);
            p += 4;
            for (var i = 0; i < count && p + 4 <= b.Length; i++)
            {
                var length = (int)Le32(b, p);
                if (length < 0 || p + 4 + length > b.Length) break;
                var line = Encoding.UTF8.GetString(b, p + 4, length);
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    var key = line.Substring(0, eq).ToUpperInvariant();
                    var value = Clean(line.Substring(eq + 1));
                    if (key == "TITLE") info.Title = value;
                    else if (key == "ARTIST") info.Artist = value;
                    else if (key == "ALBUM") info.Album = value;
                }
                p += 4 + length;
            }
        }

        private static MediaInfo Wav(Stream s)
        {
            var info = new MediaInfo();
            var head = FoundStream.ReadAt(s, 0, 12);
            if (head.Length < 12 || Encoding.ASCII.GetString(head, 0, 4) != "RIFF" || Encoding.ASCII.GetString(head, 8, 4) != "WAVE") return null;
            long pos = 12, byteRate = 0, data = 0;
            while (pos + 8 <= s.Length)
            {
                var h = FoundStream.ReadAt(s, pos, 8);
                var id = Encoding.ASCII.GetString(h, 0, 4);
                long size = Le32(h, 4);
                if (id == "fmt ")
                {
                    var fmt = FoundStream.ReadAt(s, pos + 8, 16);
                    if (fmt.Length >= 12) byteRate = Le32(fmt, 8);
                }
                else if (id == "data") data = size;
                else if (id == "LIST")
                {
                    var list = FoundStream.ReadAt(s, pos + 8, (int)Math.Min(size, 1 << 16));
                    if (list.Length >= 4 && Encoding.ASCII.GetString(list, 0, 4) == "INFO")
                        for (var p = 4; p + 8 <= list.Length;)
                        {
                            var key = Encoding.ASCII.GetString(list, p, 4);
                            var length = (int)Le32(list, p + 4);
                            if (length < 0 || p + 8 + length > list.Length) break;
                            var value = Clean(Encoding.UTF8.GetString(list, p + 8, length));
                            if (key == "INAM") info.Title = value;
                            else if (key == "IART") info.Artist = value;
                            else if (key == "IPRD") info.Album = value;
                            p += 8 + length + (length & 1);
                        }
                }
                pos += 8 + size + (size & 1);
            }
            if (byteRate > 0 && data > 0) info.Length = TimeSpan.FromSeconds((double)data / byteRate);
            return info;
        }

        private static MediaInfo Ogg(Stream s)
        {
            var info = new MediaInfo();
            var start = FoundStream.ReadAt(s, 0, 1 << 16);
            if (start.Length < 64 || Encoding.ASCII.GetString(start, 0, 4) != "OggS") return null;
            // The comment packet is near the start; the length comes from the last page's sample position.
            long rate = 0;
            var opus = Find(start, "OpusHead");
            var vorbis = Find(start, "\u0001vorbis");
            if (opus >= 0) rate = 48000;
            else if (vorbis >= 0) rate = Le32(start, vorbis + 12);
            var tags = opus >= 0 ? Find(start, "OpusTags") : Find(start, "\u0003vorbis");
            if (tags >= 0) Comments(start, tags + (opus >= 0 ? 8 : 7), info);
            var tail = FoundStream.ReadAt(s, Math.Max(0, s.Length - 65536), 65536);
            var last = -1;
            for (var i = tail.Length - 27; i >= 0; i--)
                if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S') { last = i; break; }
            if (last >= 0 && rate > 0)
            {
                var granule = (long)(Le32(tail, last + 6) | (ulong)Le32(tail, last + 10) << 32);
                if (granule > 0) info.Length = TimeSpan.FromSeconds((double)(granule - (opus >= 0 ? 312 : 0)) / rate);
            }
            return info;
        }

        private static int Find(byte[] b, string text)
        {
            var key = Encoding.GetEncoding("ISO-8859-1").GetBytes(text);
            for (var i = 0; i + key.Length <= b.Length; i++)
            {
                var ok = true;
                for (var k = 0; k < key.Length && ok; k++) ok = b[i + k] == key[k];
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>"3:07" or "1:02:03".</summary>
        public static string LengthText(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>The words in a Word, Excel or PowerPoint file, for the preview.</summary>
    public static class OfficeText
    {
        public static readonly IReadOnlyCollection<string> Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".doc", ".xls", ".ppt", ".odt", ".ods", ".odp", ".rtf" };

        /// <summary>Up to <paramref name="max"/> characters of the file's text, or null when none could be read.</summary>
        public static string Read(byte[] data, string extension, int max = 3000)
        {
            if (data == null || data.Length == 0) return null;
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            try
            {
                string text;
                if (ext == ".rtf") text = Rtf(data);
                else if (ext == ".doc" || ext == ".xls" || ext == ".ppt") text = Words(data);
                else text = Package(data, ext);
                text = Regex.Replace(text ?? string.Empty, @" {2,}", " ");
                text = Regex.Replace(text, @"\n\s*\n+", "\n\n").Trim();
                if (text.Length == 0) return null;
                return text.Length > max ? text.Substring(0, max) + "…" : text;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is System.Xml.XmlException || ex is ArgumentException)
            {
                return null;
            }
        }

        private static string Package(byte[] data, string ext)
        {
            // The parts, read from the list at the end; if that's lost, from each part's own header.
            Dictionary<string, byte[]> parts;
            try
            {
                using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                    parts = zip.Entries.Where(e => e.Length < 32 << 20).ToDictionary(e => e.FullName, e =>
                    {
                        using (var s = e.Open())
                        using (var m = new MemoryStream())
                        {
                            s.CopyTo(m);
                            return m.ToArray();
                        }
                    }, StringComparer.OrdinalIgnoreCase);
            }
            catch (InvalidDataException)
            {
                parts = Office.Salvage(data, new List<(long, long)>()).ToDictionary(p => p.Key, p => p.Value.Data, StringComparer.OrdinalIgnoreCase);
            }
            string Get(string name) => parts.TryGetValue(name, out var b) ? Encoding.UTF8.GetString(b) : null;
            var sb = new StringBuilder();
            if (ext.StartsWith(".doc") || ext == ".odt")
            {
                var xml = Get("word/document.xml") ?? Get("content.xml") ?? string.Empty;
                foreach (Match p in Regex.Matches(xml, @"<(w:p|text:p|text:h)[\s>][\s\S]*?(?:</\1>|$)"))
                    sb.Append(Strip(Regex.Replace(p.Value, @"<w:tab/>|<text:tab/>", " "))).Append('\n');
            }
            else if (ext.StartsWith(".xls") || ext == ".ods")
            {
                var strings = Regex.Matches(Get("xl/sharedStrings.xml") ?? string.Empty, @"<si>([\s\S]*?)</si>").Cast<Match>().Select(m => Strip(m.Groups[1].Value)).ToList();
                var sheet = Get("xl/worksheets/sheet1.xml");
                if (sheet != null)
                    foreach (Match row in Regex.Matches(sheet, @"<row[\s\S]*?</row>"))
                    {
                        var cells = new List<string>();
                        foreach (Match c in Regex.Matches(row.Value, @"<c\s([^>]*?)(?:/>|>([\s\S]*?)</c>)"))
                        {
                            var shared = Regex.IsMatch(c.Groups[1].Value, @"\bt=""s""");
                            var v = Regex.Match(c.Groups[2].Value, @"<v>([^<]*)</v>").Groups[1].Value;
                            var inline = Regex.Match(c.Groups[2].Value, @"<is>([\s\S]*?)</is>").Groups[1].Value;
                            cells.Add(shared && int.TryParse(v, out var i) && i < strings.Count ? strings[i] : inline.Length > 0 ? Strip(inline) : System.Net.WebUtility.HtmlDecode(v));
                        }
                        sb.Append(string.Join("\t", cells)).Append('\n');
                    }
                else sb.Append(Strip(Get("content.xml") ?? string.Empty));
            }
            else
            {
                var slides = parts.Keys.Where(k => Regex.IsMatch(k, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
                    .OrderBy(k => int.Parse(Regex.Match(k, @"\d+").Value)).ToList();
                foreach (var slide in slides)
                {
                    var texts = Regex.Matches(Get(slide), @"<a:t>([^<]*)</a:t>").Cast<Match>().Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                    sb.Append(string.Join(" ", texts)).Append("\n\n");
                }
                if (slides.Count == 0) sb.Append(Strip(Get("content.xml") ?? string.Empty));
            }
            return sb.ToString();
        }

        private static string Strip(string xml) => System.Net.WebUtility.HtmlDecode(Regex.Replace(xml, "<[^>]+>", string.Empty));

        /// <summary>The older Word, Excel and PowerPoint files: the readable words found inside them.</summary>
        private static string Words(byte[] data)
        {
            var sb = new StringBuilder();
            // Text is kept as two-byte letters, or one-byte letters in older files.
            foreach (Match m in Regex.Matches(Encoding.Unicode.GetString(data), @"[\p{L}\p{N}\p{P} ]{6,}"))
                if (m.Value.Count(char.IsLetter) * 2 >= m.Value.Length) sb.Append(m.Value.Trim()).Append('\n');
            if (sb.Length < 40)
                foreach (Match m in Regex.Matches(Encoding.GetEncoding("ISO-8859-1").GetString(data), @"[A-Za-z0-9 ,.;:'!?()\-]{8,}"))
                    if (m.Value.Count(char.IsLetter) * 2 >= m.Value.Length) sb.Append(m.Value.Trim()).Append('\n');
            return sb.ToString();
        }

        private static string Rtf(byte[] data)
        {
            var text = Encoding.GetEncoding("ISO-8859-1").GetString(data);
            text = Regex.Replace(text, @"\{\\\*[^{}]*\}|\{\\(fonttbl|colortbl|stylesheet|info)[\s\S]*?\}\}?", string.Empty);
            text = Regex.Replace(text, @"\\par[d]?\b ?", "\n");
            text = Regex.Replace(text, @"\\'([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            text = Regex.Replace(text, @"\\[a-z]+-?\d* ?|[{}]", string.Empty);
            return text;
        }
    }
}
