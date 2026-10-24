using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudurescue.Core
{
    /// <summary>A piece of a file on disk: <see cref="Length"/> clusters starting at <see cref="Lcn"/>, or a sparse gap (Lcn = -1).</summary>
    [System.Runtime.Serialization.DataContract]
    public struct DataRun
    {
        [System.Runtime.Serialization.DataMember] public long Vcn;
        [System.Runtime.Serialization.DataMember] public long Lcn;
        [System.Runtime.Serialization.DataMember] public long Length;

        public bool IsSparse => Lcn < 0;

        public DataRun(long vcn, long lcn, long length)
        {
            Vcn = vcn;
            Lcn = lcn;
            Length = length;
        }

        public override string ToString() => IsSparse ? $"sparse×{Length}" : $"{Lcn}×{Length}";
    }

    public static class Runlist
    {
        /// <summary>Decodes an NTFS runlist (mapping pairs). Each entry: a header byte, a length, then an offset relative to the previous run.</summary>
        public static List<DataRun> Decode(byte[] data, int offset, int end, long startVcn = 0)
        {
            var runs = new List<DataRun>();
            long lcn = 0, vcn = startVcn;
            var pos = offset;
            while (pos < end)
            {
                var header = data[pos++];
                if (header == 0) break;
                int lengthBytes = header & 0x0F, offsetBytes = header >> 4;
                if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || pos + lengthBytes + offsetBytes > end)
                    throw new InvalidDataException("Damaged runlist.");

                long length = 0;
                for (var i = 0; i < lengthBytes; i++) length |= (long)data[pos + i] << (8 * i);
                pos += lengthBytes;
                if (length <= 0) throw new InvalidDataException("Damaged runlist.");

                if (offsetBytes == 0)
                {
                    runs.Add(new DataRun(vcn, -1, length));
                }
                else
                {
                    long delta = 0;
                    for (var i = 0; i < offsetBytes; i++) delta |= (long)data[pos + i] << (8 * i);
                    if ((data[pos + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8) delta |= -1L << (8 * offsetBytes); // sign-extend
                    pos += offsetBytes;
                    lcn += delta;
                    if (lcn < 0) throw new InvalidDataException("Damaged runlist.");
                    runs.Add(new DataRun(vcn, lcn, length));
                }
                vcn += length;
            }
            return runs;
        }
    }

    public sealed class NtfsBoot
    {
        public int BytesPerSector { get; private set; }
        public int SectorsPerCluster { get; private set; }
        public long TotalSectors { get; private set; }
        public long MftLcn { get; private set; }
        public long MftMirrLcn { get; private set; }
        public int RecordSize { get; private set; }
        public ulong Serial { get; private set; }

        public int ClusterSize => BytesPerSector * SectorsPerCluster;
        public long TotalClusters => TotalSectors / SectorsPerCluster;

        public static bool IsNtfs(byte[] sector) => sector.Length >= 512 && Encoding.ASCII.GetString(sector, 3, 8) == "NTFS    ";

        public static bool IsBitLocker(byte[] sector) => sector.Length >= 512 && Encoding.ASCII.GetString(sector, 3, 8) == "-FVE-FS-";

        public static NtfsBoot Parse(byte[] s)
        {
            if (!IsNtfs(s)) throw new InvalidDataException("This drive isn't formatted as NTFS.");
            var b = new NtfsBoot { BytesPerSector = BitConverter.ToUInt16(s, 0x0B) };
            var spc = s[0x0D];
            b.SectorsPerCluster = spc <= 0x80 ? spc : 1 << (256 - spc);
            b.TotalSectors = BitConverter.ToInt64(s, 0x28);
            b.MftLcn = BitConverter.ToInt64(s, 0x30);
            b.MftMirrLcn = BitConverter.ToInt64(s, 0x38);
            var cpr = unchecked((sbyte)s[0x40]);
            b.RecordSize = cpr < 0 ? 1 << -cpr : cpr * b.ClusterSize;
            b.Serial = BitConverter.ToUInt64(s, 0x48);

            if (b.BytesPerSector < 256 || b.BytesPerSector > 4096 || (b.BytesPerSector & (b.BytesPerSector - 1)) != 0 ||
                b.SectorsPerCluster <= 0 || b.ClusterSize > 2 * 1024 * 1024 || b.RecordSize < 256 || b.RecordSize > 65536 ||
                b.TotalSectors <= 0 || b.MftLcn <= 0 || b.MftLcn >= b.TotalClusters)
                throw new InvalidDataException("The NTFS boot sector is damaged.");
            return b;
        }
    }

    public enum AttrType : uint
    {
        StandardInformation = 0x10,
        AttributeList = 0x20,
        FileName = 0x30,
        Data = 0x80,
        IndexRoot = 0x90,
        End = 0xFFFFFFFF
    }

    public sealed class NtfsAttribute
    {
        public AttrType Type;
        public string Name = string.Empty;
        public bool NonResident;
        public ushort Flags;
        public ushort Id;

        // Resident
        public byte[] Value;

        // Non-resident
        public long StartVcn;
        public long LastVcn;
        public int CompressionUnit;
        public long AllocatedSize;
        public long DataSize;
        public long InitializedSize;
        public List<DataRun> Runs;

        public bool Compressed => (Flags & 0x0001) != 0;
        public bool Encrypted => (Flags & 0x4000) != 0;
        public bool Sparse => (Flags & 0x8000) != 0;
    }

    public sealed class FileNameInfo
    {
        public long ParentRecord;
        public int ParentSequence;
        public string Name;
        public int Namespace; // 0 POSIX, 1 Win32, 2 DOS, 3 Win32 and DOS
        public DateTime Created;
        public DateTime Modified;
        public long RealSize;
    }

    /// <summary>One MFT record (FILE record) with its attributes.</summary>
    public sealed class FileRecord
    {
        public long Number;
        public int Sequence;
        public bool InUse;
        public bool IsDirectory;
        public long BaseRecord;
        public readonly List<NtfsAttribute> Attributes = new List<NtfsAttribute>();

        public bool IsExtension => BaseRecord != 0;

        public NtfsAttribute Find(AttrType type, string name = "") =>
            Attributes.FirstOrDefault(a => a.Type == type && string.Equals(a.Name, name, StringComparison.Ordinal));

        /// <summary>The file's name: the long (Win32 or POSIX) name rather than the 8.3 DOS name.</summary>
        public FileNameInfo FileName
        {
            get
            {
                FileNameInfo best = null;
                foreach (var a in Attributes.Where(a => a.Type == AttrType.FileName && a.Value != null))
                {
                    var fn = ParseFileName(a.Value);
                    if (fn == null) continue;
                    if (best == null || (best.Namespace == 2 && fn.Namespace != 2)) best = fn;
                }
                return best;
            }
        }

        public void StandardTimes(out DateTime created, out DateTime modified)
        {
            created = modified = DateTime.MinValue;
            var si = Find(AttrType.StandardInformation);
            if (si?.Value == null || si.Value.Length < 32) return;
            created = FileTime(BitConverter.ToInt64(si.Value, 0));
            modified = FileTime(BitConverter.ToInt64(si.Value, 8));
        }

        public static DateTime FileTime(long ft)
        {
            try { return ft > 0 && ft < 2650467743999999999 ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        public static FileNameInfo ParseFileName(byte[] v)
        {
            if (v.Length < 66) return null;
            var length = v[64];
            if (66 + length * 2 > v.Length || length == 0) return null;
            var parent = BitConverter.ToInt64(v, 0);
            return new FileNameInfo
            {
                ParentRecord = parent & 0x0000FFFFFFFFFFFF,
                ParentSequence = (int)((ulong)parent >> 48),
                Created = FileTime(BitConverter.ToInt64(v, 8)),
                Modified = FileTime(BitConverter.ToInt64(v, 16)),
                RealSize = BitConverter.ToInt64(v, 48),
                Namespace = v[65],
                Name = Encoding.Unicode.GetString(v, 66, length * 2)
            };
        }

        /// <summary>
        /// Applies the fixups and parses a record. Returns null for empty, unused-signature or torn records.
        /// <paramref name="data"/> is changed in place (the fixups are applied).
        /// </summary>
        public static FileRecord Parse(byte[] data, int offset, int size, long number)
        {
            if (BitConverter.ToUInt32(data, offset) != 0x454C4946) return null; // "FILE"
            if (!ApplyFixups(data, offset, size)) return null;

            var r = new FileRecord
            {
                Number = number,
                Sequence = BitConverter.ToUInt16(data, offset + 0x10),
                InUse = (BitConverter.ToUInt16(data, offset + 0x16) & 1) != 0,
                IsDirectory = (BitConverter.ToUInt16(data, offset + 0x16) & 2) != 0,
                BaseRecord = BitConverter.ToInt64(data, offset + 0x20) & 0x0000FFFFFFFFFFFF
            };
            int pos = BitConverter.ToUInt16(data, offset + 0x14);
            var used = (int)Math.Min(BitConverter.ToUInt32(data, offset + 0x18), (uint)size);
            if (pos < 0x30 || pos >= used) return r;

            while (pos + 16 <= used)
            {
                var type = BitConverter.ToUInt32(data, offset + pos);
                if (type == 0xFFFFFFFF || type == 0) break;
                var length = (int)BitConverter.ToUInt32(data, offset + pos + 4);
                if (length < 16 || pos + length > used) break;
                try
                {
                    var a = ParseAttribute(data, offset + pos, length);
                    if (a != null) r.Attributes.Add(a);
                }
                catch (InvalidDataException)
                {
                    // Skip a damaged attribute; the rest of the record may still be useful.
                }
                pos += length;
            }
            return r;
        }

        private static NtfsAttribute ParseAttribute(byte[] d, int p, int length)
        {
            var a = new NtfsAttribute
            {
                Type = (AttrType)BitConverter.ToUInt32(d, p),
                NonResident = d[p + 8] != 0,
                Flags = BitConverter.ToUInt16(d, p + 12),
                Id = BitConverter.ToUInt16(d, p + 14)
            };
            var nameLength = d[p + 9];
            if (nameLength > 0)
            {
                var nameOffset = BitConverter.ToUInt16(d, p + 10);
                if (nameOffset + nameLength * 2 > length) throw new InvalidDataException();
                a.Name = Encoding.Unicode.GetString(d, p + nameOffset, nameLength * 2);
            }

            if (!a.NonResident)
            {
                var valueLength = (int)BitConverter.ToUInt32(d, p + 16);
                var valueOffset = BitConverter.ToUInt16(d, p + 20);
                if (valueLength < 0 || valueOffset + valueLength > length) throw new InvalidDataException();
                a.Value = new byte[valueLength];
                Buffer.BlockCopy(d, p + valueOffset, a.Value, 0, valueLength);
                return a;
            }

            if (length < 0x40) throw new InvalidDataException();
            a.StartVcn = BitConverter.ToInt64(d, p + 16);
            a.LastVcn = BitConverter.ToInt64(d, p + 24);
            var runsOffset = BitConverter.ToUInt16(d, p + 32);
            a.CompressionUnit = BitConverter.ToUInt16(d, p + 34);
            a.AllocatedSize = BitConverter.ToInt64(d, p + 40);
            a.DataSize = BitConverter.ToInt64(d, p + 48);
            a.InitializedSize = BitConverter.ToInt64(d, p + 56);
            if (runsOffset >= length) throw new InvalidDataException();
            a.Runs = Runlist.Decode(d, p + runsOffset, p + length, a.StartVcn);
            return a;
        }

        /// <summary>
        /// Every 512 bytes of a record end with a check value; the real bytes are kept in the update sequence array.
        /// A mismatch means the record was only partly written.
        /// </summary>
        public static bool ApplyFixups(byte[] data, int offset, int size)
        {
            int usaOffset = BitConverter.ToUInt16(data, offset + 4);
            int usaCount = BitConverter.ToUInt16(data, offset + 6);
            if (usaCount < 2 || usaOffset + usaCount * 2 > size || (usaCount - 1) * 512 > size) return false;
            var check = BitConverter.ToUInt16(data, offset + usaOffset);
            for (var i = 1; i < usaCount; i++)
            {
                var end = offset + i * 512 - 2;
                if (BitConverter.ToUInt16(data, end) != check) return false;
                data[end] = data[offset + usaOffset + i * 2];
                data[end + 1] = data[offset + usaOffset + i * 2 + 1];
            }
            return true;
        }
    }

    /// <summary>LZNT1, the compression NTFS uses for compressed files and folders.</summary>
    public static class Lznt1
    {
        /// <summary>Decompresses one compression unit. Output is padded with zeros to <paramref name="outputSize"/>.</summary>
        public static byte[] Decompress(byte[] input, int inputLength, int outputSize)
        {
            var output = new byte[outputSize];
            var inPos = 0;
            var outBase = 0;
            while (inPos + 2 <= inputLength && outBase < outputSize)
            {
                var header = BitConverter.ToUInt16(input, inPos);
                if (header == 0) break;
                var chunkEnd = inPos + 2 + (header & 0x0FFF) + 1;
                inPos += 2;
                if (chunkEnd > inputLength) chunkEnd = inputLength;
                var chunkLimit = Math.Min(outBase + 4096, outputSize);

                if ((header & 0x8000) == 0)
                {
                    // Stored without compression.
                    var n = Math.Min(chunkEnd - inPos, chunkLimit - outBase);
                    Buffer.BlockCopy(input, inPos, output, outBase, n);
                }
                else
                {
                    var outPos = outBase;
                    while (inPos < chunkEnd && outPos < chunkLimit)
                    {
                        var flags = input[inPos++];
                        for (var bit = 0; bit < 8 && inPos < chunkEnd && outPos < chunkLimit; bit++)
                        {
                            if ((flags & (1 << bit)) == 0)
                            {
                                output[outPos++] = input[inPos++];
                                continue;
                            }
                            if (inPos + 2 > chunkEnd) { inPos = chunkEnd; break; }
                            var token = BitConverter.ToUInt16(input, inPos);
                            inPos += 2;

                            // The split between offset and length bits depends on how far into the chunk we are.
                            var lengthBits = 12;
                            for (var p = outPos - outBase - 1; p >= 0x10; p >>= 1) lengthBits--;
                            var length = (token & ((1 << lengthBits) - 1)) + 3;
                            var back = (token >> lengthBits) + 1;
                            var from = outPos - back;
                            if (from < outBase) throw new InvalidDataException("Damaged compressed data.");
                            for (var i = 0; i < length && outPos < chunkLimit; i++) output[outPos++] = output[from + i];
                        }
                    }
                }
                inPos = chunkEnd;
                outBase += 4096;
            }
            return output;
        }
    }
}
