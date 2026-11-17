using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Kudurescue.Core
{
    /// <summary>
    /// Repairs files that came back only partly whole, so they open: photos (JPEG, PNG) show the part that survived with the
    /// rest grey, and Word, Excel and PowerPoint files keep whatever text, sheets and slides can still be read. The file as
    /// found is always kept too; a repair is an extra copy, "name (repaired).ext".
    /// </summary>
    public static class Repair
    {
        /// <summary>The kinds of files a repair is tried for.</summary>
        public static readonly IReadOnlyCollection<string> Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm" };

        public static bool CanTry(string extension) => Kinds.Contains(extension ?? string.Empty);

        /// <summary>
        /// A repaired copy of a partly whole file, or null when nothing could be saved (or nothing needed repairing).
        /// <paramref name="damaged"/> are the parts known to hold other files' data now, as (start, length) in bytes;
        /// it may be null (a scan saved by an older version), and then only what the file itself shows is used.
        /// </summary>
        public static byte[] Make(byte[] data, string extension, IList<(long Start, long Length)> damaged)
        {
            if (data == null || data.Length == 0) return null;
            var ranges = (damaged ?? new List<(long, long)>()).Where(r => r.Length > 0).OrderBy(r => r.Start).ToList();
            try
            {
                switch ((extension ?? string.Empty).ToLowerInvariant())
                {
                    case ".jpg": case ".jpeg": case ".jpe": case ".jfif": return Jpeg(data, ranges);
                    case ".png": return Png(data, ranges);
                    case ".docx": case ".docm": case ".xlsx": case ".xlsm": case ".pptx": case ".pptm": return Office.Make(data, extension, ranges);
                    default: return null;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException || ex is IndexOutOfRangeException ||
                                       ex is System.Xml.XmlException || ex is InvalidOperationException || ex is OverflowException)
            {
                return null;
            }
        }

        /// <summary>The first damaged byte at or after <paramref name="from"/>, or -1.</summary>
        private static long FirstDamage(List<(long Start, long Length)> ranges, long from)
        {
            foreach (var r in ranges)
                if (r.Start + r.Length > from) return Math.Max(r.Start, from);
            return -1;
        }

        // ================================================================== JPEG

        /// <summary>
        /// A JPEG's picture data has no checks, so the damage is cut off where it starts and the file is closed there: the
        /// part before is shown and the rest grey, as every viewer does for a photo that ends early. The header (sizes,
        /// colour tables) has to be whole; without it nothing can be shown.
        /// </summary>
        public static byte[] Jpeg(byte[] data, List<(long Start, long Length)> damaged)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
            // Walk the header to where the picture data starts.
            var pos = 2;
            var scanStart = -1;
            var hasFrame = false;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF) return null;
                var marker = data[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                var length = (data[pos + 2] << 8) | data[pos + 3];
                if (length < 2 || pos + 2 + length > data.Length) return null;
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC) hasFrame = true;
                pos += 2 + length;
                if (marker == 0xDA)
                {
                    scanStart = pos;
                    break;
                }
            }
            if (scanStart < 0 || !hasFrame) return null;
            var firstDamage = FirstDamage(damaged, 0);
            if (firstDamage >= 0 && firstDamage < scanStart) return null; // the header itself was overwritten

            long cut;
            if (firstDamage >= 0) cut = firstDamage;
            else
            {
                // Nothing known to be damaged: a photo that ends in zeros (not read, or never written) is closed before them.
                var end = data.Length;
                while (end > scanStart && data[end - 1] == 0) end--;
                if (end == data.Length && data[end - 2] == 0xFF && data[end - 1] == 0xD9) return null; // whole already
                cut = end;
            }
            // Picture data needs a little to show anything at all; and a cut can't leave half a marker behind.
            if (cut - scanStart < 16) return null;
            var keep = (int)cut;
            while (keep > scanStart && data[keep - 1] == 0xFF) keep--;
            var result = new byte[keep + 2];
            Buffer.BlockCopy(data, 0, result, 0, keep);
            result[keep] = 0xFF;
            result[keep + 1] = 0xD9;
            return result;
        }

        // ================================================================== PNG

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// A PNG's picture is one compressed stream of rows. The rows read before the damage are kept as they are; the rows
        /// after it are made grey, and the file is written again so any viewer opens it.
        /// </summary>
        public static byte[] Png(byte[] data, List<(long Start, long Length)> damaged)
        {
            if (data.Length < 33 || !data.Take(8).SequenceEqual(PngSignature)) return null;
            var firstDamage = FirstDamage(damaged, 0);
            int width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
            byte[] palette = null, transparency = null;
            var keep = new List<(string Type, byte[] Data)>();
            var compressed = new MemoryStream();
            var wholeImage = false;
            var pos = 8;
            while (pos + 12 <= data.Length)
            {
                var length = (int)ReadBig(data, pos);
                var type = Encoding.ASCII.GetString(data, pos + 4, 4);
                if (length < 0 || type.Any(c => !char.IsLetter(c))) break;
                var dataStart = pos + 8;
                var available = Math.Min(length, data.Length - dataStart);
                if (available < 0) break;
                var hit = firstDamage >= 0 && firstDamage < dataStart + length + 4;
                var crcOk = !hit && available == length && dataStart + length + 4 <= data.Length &&
                            Crc32.Of(data, pos + 4, length + 4) == ReadBig(data, dataStart + length);
                if (type == "IHDR")
                {
                    if (!crcOk || length != 13) return null; // without its sizes, nothing can be shown
                    width = (int)ReadBig(data, dataStart);
                    height = (int)ReadBig(data, dataStart + 4);
                    depth = data[dataStart + 8];
                    colour = data[dataStart + 9];
                    interlace = data[dataStart + 12];
                }
                else if (type == "PLTE" && crcOk) palette = Slice(data, dataStart, length);
                else if (type == "tRNS" && crcOk) transparency = Slice(data, dataStart, length);
                else if (type == "IDAT")
                {
                    if (crcOk) compressed.Write(data, dataStart, length);
                    else
                    {
                        // The part of a damaged chunk before the damage is still worth having. When where the damage is
                        // isn't known, the chunk is left out: other data can unpack into rows that look real.
                        var good = firstDamage >= 0 ? (int)Math.Min(available, Math.Max(0, firstDamage - dataStart)) : 0;
                        compressed.Write(data, dataStart, Math.Max(0, good));
                        break;
                    }
                }
                else if (type == "IEND") { wholeImage = crcOk; break; }
                else if (crcOk && (type == "gAMA" || type == "sRGB" || type == "cHRM" || type == "pHYs" || type == "iCCP")) keep.Add((type, Slice(data, dataStart, length)));
                else if (!crcOk && hit) break;
                pos = dataStart + length + 4;
            }
            if (width <= 0 || height <= 0 || width > 1 << 16 || height > 1 << 16 || interlace != 0) return null;
            var channels = colour == 0 ? 1 : colour == 2 ? 3 : colour == 3 ? 1 : colour == 4 ? 2 : colour == 6 ? 4 : 0;
            if (channels == 0 || (colour == 3 && palette == null)) return null;
            var rowBytes = (int)(((long)width * channels * depth + 7) / 8) + 1;

            // As many whole rows as the compressed data still gives.
            var rows = Inflate(compressed.ToArray(), (long)rowBytes * height);
            var goodRows = rows.Length / rowBytes;
            if (goodRows == 0) return null;
            if (goodRows >= height && wholeImage && firstDamage < 0) return null; // nothing wrong with it

            // Grey for the missing rows: middle grey, fully opaque; in a palette picture, a grey added to the palette.
            var grey = new byte[rowBytes];
            if (colour == 3)
            {
                var entries = palette.Length / 3;
                byte index;
                if (entries < 256 && depth == 8)
                {
                    palette = palette.Concat(new byte[] { 128, 128, 128 }).ToArray();
                    index = (byte)entries;
                }
                else index = 0;
                for (var i = 1; i < rowBytes; i++) grey[i] = depth == 8 ? index : (byte)0;
            }
            else
            {
                var sampleBytes = depth / 8;
                if (depth < 8)
                {
                    var levels = (1 << depth) - 1;
                    var mid = (byte)(levels / 2);
                    var perByte = 8 / depth;
                    byte b = 0;
                    for (var k = 0; k < perByte; k++) b |= (byte)(mid << (8 - depth * (k + 1)));
                    for (var i = 1; i < rowBytes; i++) grey[i] = b;
                }
                else
                {
                    for (var x = 0; x < width; x++)
                        for (var c = 0; c < channels; c++)
                        {
                            var opaque = (colour == 4 && c == 1) || (colour == 6 && c == 3);
                            for (var s = 0; s < sampleBytes; s++)
                                grey[1 + (x * channels + c) * sampleBytes + s] = opaque ? (byte)0xFF : (byte)0x80;
                        }
                }
            }
            var all = new MemoryStream();
            all.Write(rows, 0, goodRows * rowBytes);
            for (var y = goodRows; y < height; y++) all.Write(grey, 0, rowBytes);

            var output = new MemoryStream();
            output.Write(PngSignature, 0, 8);
            var ihdr = new byte[13];
            WriteBig(ihdr, 0, (uint)width);
            WriteBig(ihdr, 4, (uint)height);
            ihdr[8] = (byte)depth;
            ihdr[9] = (byte)colour;
            WriteChunk(output, "IHDR", ihdr);
            foreach (var (type, chunk) in keep) WriteChunk(output, type, chunk);
            if (palette != null) WriteChunk(output, "PLTE", palette);
            if (transparency != null) WriteChunk(output, "tRNS", transparency);
            WriteChunk(output, "IDAT", Zlib(all.ToArray()));
            WriteChunk(output, "IEND", new byte[0]);
            return output.ToArray();
        }

        /// <summary>Unpacks a zlib stream as far as it goes (up to <paramref name="max"/> bytes), stopping quietly at damage.</summary>
        public static byte[] Inflate(byte[] zlib, long max)
        {
            var output = new MemoryStream();
            if (zlib.Length <= 2) return output.ToArray();
            using (var input = new MemoryStream(zlib, 2, zlib.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[1 << 16];
                try
                {
                    while (output.Length < max)
                    {
                        var n = deflate.Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;
                        output.Write(buffer, 0, n);
                    }
                }
                catch (InvalidDataException) { /* the damage: keep what came before it */ }
                catch (IOException) { }
                catch (ArgumentException) { }
                catch (IndexOutOfRangeException) { }
            }
            var bytes = output.ToArray();
            return bytes.Length > max ? bytes.Take((int)max).ToArray() : bytes;
        }

        /// <summary>Packs bytes as a zlib stream (the deflate data with its two-byte header and Adler-32 check).</summary>
        public static byte[] Zlib(byte[] raw)
        {
            var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true)) deflate.Write(raw, 0, raw.Length);
            uint a = 1, b = 0;
            foreach (var x in raw)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }
            var adler = new byte[4];
            WriteBig(adler, 0, (b << 16) | a);
            output.Write(adler, 0, 4);
            return output.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] chunk)
        {
            var head = new byte[8];
            WriteBig(head, 0, (uint)chunk.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            s.Write(head, 0, 8);
            s.Write(chunk, 0, chunk.Length);
            var crc = new byte[4];
            var both = head.Skip(4).Concat(chunk).ToArray();
            WriteBig(crc, 0, Crc32.Of(both, 0, both.Length));
            s.Write(crc, 0, 4);
        }

        private static byte[] Slice(byte[] data, int start, int length)
        {
            var b = new byte[length];
            Buffer.BlockCopy(data, start, b, 0, length);
            return b;
        }

        internal static uint ReadBig(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

        internal static void WriteBig(byte[] d, int p, uint v)
        {
            d[p] = (byte)(v >> 24);
            d[p + 1] = (byte)(v >> 16);
            d[p + 2] = (byte)(v >> 8);
            d[p + 3] = (byte)v;
        }
    }

    /// <summary>The CRC-32 used by PNG and ZIP.</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Of(byte[] data, int start, int length)
        {
            var c = 0xFFFFFFFF;
            for (var i = start; i < start + length; i++) c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
