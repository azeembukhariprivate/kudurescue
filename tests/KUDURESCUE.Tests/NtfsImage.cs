using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudurescue.Tests
{
    /// <summary>
    /// Builds a small NTFS drive image byte by byte: boot sector, MFT records (with fixups), runlists and the cluster bitmap.
    /// Only what KUDURESCUE reads is filled in, which is enough to test the engine against the real on-disk format.
    /// </summary>
    internal sealed class NtfsImage
    {
        public const int Cluster = 4096;
        public const int RecordSize = 1024;
        public const long Clusters = 2048;
        public const long MftLcn = 16;
        public const int MftRecords = 64;
        public const long MirrLcn = 34;
        public const long BitmapLcn = 40;

        public readonly byte[] Data = new byte[Clusters * Cluster];
        private readonly bool[] _used = new bool[Clusters];
        private readonly byte[][] _records = new byte[MftRecords][];

        public static readonly DateTime Created = new DateTime(2026, 3, 10, 9, 30, 0, DateTimeKind.Utc);
        public static readonly DateTime Modified = new DateTime(2026, 3, 14, 18, 5, 0, DateTimeKind.Utc);

        public NtfsImage()
        {
            for (long c = 0; c <= 50; c++) _used[c] = true; // boot, MFT, mirror, bitmap
        }

        public void MarkUsed(long lcn, long count)
        {
            for (var c = lcn; c < lcn + count; c++) _used[c] = true;
        }

        public void WriteClusters(long lcn, byte[] content)
        {
            Buffer.BlockCopy(content, 0, Data, (int)(lcn * Cluster), content.Length);
        }

        public static byte[] Pattern(int length, int seed)
        {
            var b = new byte[length];
            var x = (uint)(seed * 2654435761u + 12345);
            for (var i = 0; i < length; i++)
            {
                x = x * 1103515245 + 12345;
                b[i] = (byte)(x >> 16);
            }
            return b;
        }

        // ------------------------------------------------------------------ records

        public sealed class Record
        {
            public long Number;
            public int Sequence = 1;
            public bool InUse;
            public bool Directory;
            public long BaseRecord;
            public readonly List<byte[]> Attributes = new List<byte[]>();
            public bool TearFixup;
        }

        public void Add(Record r) => _records[r.Number] = Encode(r);

        private static byte[] Encode(Record r)
        {
            var b = new byte[RecordSize];
            Encoding.ASCII.GetBytes("FILE").CopyTo(b, 0);
            W16(b, 4, 0x30);           // update sequence array offset
            W16(b, 6, 3);              // 1 + 1024 / 512
            W16(b, 0x10, r.Sequence);
            W16(b, 0x12, 1);
            W16(b, 0x14, 0x38);        // first attribute
            W16(b, 0x16, (r.InUse ? 1 : 0) | (r.Directory ? 2 : 0));
            W32(b, 0x1C, RecordSize);
            W64(b, 0x20, r.BaseRecord);
            W32(b, 0x2C, (int)r.Number);
            var pos = 0x38;
            foreach (var a in r.Attributes)
            {
                Buffer.BlockCopy(a, 0, b, pos, a.Length);
                pos += a.Length;
            }
            W32(b, pos, unchecked((int)0xFFFFFFFF));
            pos += 8;
            W32(b, 0x18, pos);

            // Fixups: the last two bytes of each 512-byte block move into the array, replaced by the check value.
            const ushort check = 0x0042;
            W16(b, 0x30, check);
            for (var i = 1; i <= 2; i++)
            {
                var end = i * 512 - 2;
                b[0x30 + i * 2] = b[end];
                b[0x30 + i * 2 + 1] = b[end + 1];
                W16(b, end, r.TearFixup && i == 2 ? 0x0099 : check);
            }
            return b;
        }

        public static byte[] StandardInformation(uint fileAttributes = 0x20)
        {
            var v = new byte[48];
            W64(v, 0, Created.ToFileTimeUtc());
            W64(v, 8, Modified.ToFileTimeUtc());
            W64(v, 16, Modified.ToFileTimeUtc());
            W64(v, 24, Modified.ToFileTimeUtc());
            W32(v, 32, (int)fileAttributes);
            return Resident(0x10, v);
        }

        public static byte[] FileName(long parent, int parentSeq, string name, int nameSpace = 1, long size = 0)
        {
            var v = new byte[66 + name.Length * 2];
            W64(v, 0, parent | ((long)parentSeq << 48));
            for (var i = 0; i < 4; i++) W64(v, 8 + i * 8, Modified.ToFileTimeUtc());
            W64(v, 40, size);
            W64(v, 48, size);
            v[64] = (byte)name.Length;
            v[65] = (byte)nameSpace;
            Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
            return Resident(0x30, v);
        }

        public static byte[] Resident(uint type, byte[] value, string name = null, ushort flags = 0)
        {
            var nameBytes = name == null ? new byte[0] : Encoding.Unicode.GetBytes(name);
            var valueOffset = Align(24 + nameBytes.Length);
            var length = Align(valueOffset + value.Length);
            var a = new byte[length];
            W32(a, 0, (int)type);
            W32(a, 4, length);
            a[8] = 0;
            a[9] = (byte)(nameBytes.Length / 2);
            W16(a, 10, 24);
            W16(a, 12, flags);
            W32(a, 16, value.Length);
            W16(a, 20, valueOffset);
            nameBytes.CopyTo(a, 24);
            value.CopyTo(a, valueOffset);
            return a;
        }

        /// <summary>A non-resident attribute. Runs are (lcn, length) pairs; lcn -1 makes a sparse run.</summary>
        public static byte[] NonResident(uint type, (long Lcn, long Length)[] runs, long dataSize, ushort flags = 0, int compressionUnit = 0,
            long startVcn = 0, long? initialized = null)
        {
            var runlist = EncodeRuns(runs);
            var runsOffset = 0x40;
            var length = Align(runsOffset + runlist.Length);
            var a = new byte[length];
            var clusters = runs.Sum(r => r.Length);
            W32(a, 0, (int)type);
            W32(a, 4, length);
            a[8] = 1;
            W16(a, 10, 0x40);
            W16(a, 12, flags);
            W64(a, 16, startVcn);
            W64(a, 24, startVcn + clusters - 1);
            W16(a, 32, runsOffset);
            W16(a, 34, compressionUnit);
            W64(a, 40, clusters * Cluster);
            W64(a, 48, dataSize);
            W64(a, 56, initialized ?? dataSize);
            runlist.CopyTo(a, runsOffset);
            return a;
        }

        public static byte[] EncodeRuns((long Lcn, long Length)[] runs)
        {
            var bytes = new List<byte>();
            long previous = 0;
            foreach (var (lcn, length) in runs)
            {
                var lengthBytes = MinBytes(length, false);
                if (lcn < 0)
                {
                    bytes.Add((byte)lengthBytes.Length);
                    bytes.AddRange(lengthBytes);
                    continue;
                }
                var offsetBytes = MinBytes(lcn - previous, true);
                previous = lcn;
                bytes.Add((byte)(lengthBytes.Length | (offsetBytes.Length << 4)));
                bytes.AddRange(lengthBytes);
                bytes.AddRange(offsetBytes);
            }
            bytes.Add(0);
            return bytes.ToArray();
        }

        private static byte[] MinBytes(long value, bool signed)
        {
            for (var n = 1; n <= 8; n++)
            {
                var min = signed ? -(1L << (8 * n - 1)) : 0;
                var max = signed ? (1L << (8 * n - 1)) - 1 : (n == 8 ? long.MaxValue : (1L << (8 * n)) - 1);
                if (value >= min && value <= max) return BitConverter.GetBytes(value).Take(n).ToArray();
            }
            return BitConverter.GetBytes(value);
        }

        public static byte[] AttributeList(params (uint Type, long Segment, long StartVcn)[] entries)
        {
            var v = new List<byte>();
            foreach (var (type, segment, vcn) in entries)
            {
                var e = new byte[32];
                W32(e, 0, (int)type);
                W16(e, 4, 32);
                e[6] = 0;
                e[7] = 26;
                W64(e, 8, vcn);
                W64(e, 16, segment | (1L << 48));
                v.AddRange(e);
            }
            return Resident(0x20, v.ToArray());
        }

        // ------------------------------------------------------------------ image

        public string Save(string path)
        {
            // Boot sector
            var boot = new byte[512];
            boot[0] = 0xEB; boot[1] = 0x52; boot[2] = 0x90;
            Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot, 3);
            W16(boot, 0x0B, 512);
            boot[0x0D] = 8;
            W64(boot, 0x28, Clusters * 8);
            W64(boot, 0x30, MftLcn);
            W64(boot, 0x38, MirrLcn);
            boot[0x40] = 0xF6; // -10: records are 2^10 = 1024 bytes
            boot[0x44] = 1;
            W64(boot, 0x48, 0x1234567890ABCDEF);
            boot[510] = 0x55; boot[511] = 0xAA;
            boot.CopyTo(Data, 0);

            // System records the engine needs: $MFT (0), root (5), $Bitmap (6).
            var mft = new Record { Number = 0, InUse = true };
            mft.Attributes.Add(StandardInformation(6));
            mft.Attributes.Add(FileName(5, 5, "$MFT"));
            mft.Attributes.Add(NonResident(0x80, new[] { (MftLcn, (long)(MftRecords * RecordSize / Cluster)) }, MftRecords * RecordSize));
            Add(mft);

            var root = new Record { Number = 5, Sequence = 5, InUse = true, Directory = true };
            root.Attributes.Add(StandardInformation(6));
            root.Attributes.Add(FileName(5, 5, "."));
            Add(root);

            var bitmap = new Record { Number = 6, Sequence = 6, InUse = true };
            bitmap.Attributes.Add(StandardInformation(6));
            bitmap.Attributes.Add(FileName(5, 5, "$Bitmap"));
            bitmap.Attributes.Add(NonResident(0x80, new[] { (BitmapLcn, 1L) }, Clusters / 8));
            Add(bitmap);

            for (var i = 0; i < MftRecords; i++)
            {
                if (_records[i] == null) continue;
                Buffer.BlockCopy(_records[i], 0, Data, (int)(MftLcn * Cluster + i * RecordSize), RecordSize);
                if (i < 4) Buffer.BlockCopy(_records[i], 0, Data, (int)(MirrLcn * Cluster + i * RecordSize), RecordSize);
            }

            var bits = new byte[Clusters / 8];
            for (var c = 0; c < Clusters; c++) if (_used[c]) bits[c / 8] |= (byte)(1 << (c % 8));
            bits.CopyTo(Data, (int)(BitmapLcn * Cluster));

            File.WriteAllBytes(path, Data);
            return path;
        }

        private static int Align(int n) => (n + 7) & ~7;
        private static void W16(byte[] b, int o, int v) => BitConverter.GetBytes((ushort)v).CopyTo(b, o);
        private static void W32(byte[] b, int o, int v) => BitConverter.GetBytes(v).CopyTo(b, o);
        private static void W64(byte[] b, int o, long v) => BitConverter.GetBytes(v).CopyTo(b, o);

        // ------------------------------------------------------------------ LZNT1 (for building compressed test files)

        /// <summary>A plain greedy LZNT1 compressor, following the same rules NTFS uses.</summary>
        public static byte[] CompressLznt1(byte[] input)
        {
            var output = new List<byte>();
            for (var start = 0; start < input.Length; start += 4096)
            {
                var length = Math.Min(4096, input.Length - start);
                var body = new List<byte>();
                var pos = 0;
                while (pos < length)
                {
                    var flagIndex = body.Count;
                    body.Add(0);
                    byte flags = 0;
                    for (var bit = 0; bit < 8 && pos < length; bit++)
                    {
                        var lengthBits = 12;
                        for (var p = pos - 1; p >= 0x10; p >>= 1) lengthBits--;
                        var maxLength = (1 << lengthBits) + 2;
                        var maxBack = Math.Min(pos, 1 << (16 - lengthBits));
                        int bestLength = 0, bestBack = 0;
                        for (var back = 1; back <= maxBack; back++)
                        {
                            var n = 0;
                            while (n < maxLength && pos + n < length && input[start + pos + n] == input[start + pos - back + n]) n++;
                            if (n > bestLength) { bestLength = n; bestBack = back; }
                        }
                        if (bestLength >= 3)
                        {
                            var token = ((bestBack - 1) << lengthBits) | (bestLength - 3);
                            body.Add((byte)token);
                            body.Add((byte)(token >> 8));
                            flags |= (byte)(1 << bit);
                            pos += bestLength;
                        }
                        else
                        {
                            body.Add(input[start + pos]);
                            pos++;
                        }
                    }
                    body[flagIndex] = flags;
                }
                if (body.Count >= length)
                {
                    // Not worth compressing: store the chunk as-is.
                    var header = 0x3000 | (length - 1);
                    output.Add((byte)header); output.Add((byte)(header >> 8));
                    output.AddRange(input.Skip(start).Take(length));
                }
                else
                {
                    var header = 0xB000 | (body.Count - 1);
                    output.Add((byte)header); output.Add((byte)(header >> 8));
                    output.AddRange(body);
                }
            }
            return output.ToArray();
        }
    }
}
