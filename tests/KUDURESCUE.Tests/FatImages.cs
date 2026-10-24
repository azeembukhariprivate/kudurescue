using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudurescue.Core;

namespace Kudurescue.Tests
{
    /// <summary>Builds a FAT32 drive image byte by byte, with live and deleted entries (long names included).</summary>
    internal sealed class Fat32Image
    {
        public const int Sector = 512;
        public const int Reserved = 32;
        public const long Clusters = 70000; // more than 65535, so the "cleared top half" case applies
        public readonly long FatSectors = ((Clusters + 2) * 4 + Sector - 1) / Sector;
        public readonly byte[] Data;
        private readonly uint[] _fat = new uint[Clusters + 2];
        private readonly Dictionary<long, List<byte[]>> _folders = new Dictionary<long, List<byte[]>>();

        public static readonly DateTime Modified = new DateTime(2026, 3, 14, 18, 4, 58, DateTimeKind.Local);

        public Fat32Image()
        {
            Data = new byte[(Reserved + 2 * FatSectors + Clusters) * Sector];
            _fat[0] = 0x0FFFFFF8;
            _fat[1] = 0x0FFFFFFF;
            Folder(2, 4); // root: four clusters, chained
        }

        public long DataStart => (Reserved + 2 * FatSectors) * Sector;
        public long Offset(long cluster) => DataStart + (cluster - 2) * Sector;

        private readonly Dictionary<long, int> _sizes = new Dictionary<long, int>();

        public void Folder(long cluster, int clusters = 1)
        {
            _folders[cluster] = new List<byte[]>();
            _sizes[cluster] = clusters;
            Use(cluster, clusters);
        }

        /// <summary>A live file's clusters, chained in the FAT.</summary>
        public void Use(long first, long count)
        {
            for (var c = first; c < first + count; c++) _fat[c] = c == first + count - 1 ? 0x0FFFFFFF : (uint)(c + 1);
        }

        public void Write(long cluster, byte[] content) => content.CopyTo(Data, Offset(cluster));

        /// <summary>Adds an entry (with a long name if given) to a folder. Deleted entries get 0xE5 and a cleared top half.</summary>
        public void Entry(long folder, string shortName, string longName, long cluster, long size, bool deleted, bool isFolder = false)
        {
            var sn = Encoding.ASCII.GetBytes(shortName.PadRight(11));
            var checksum = FatVolume.Checksum(sn);
            var entries = _folders[folder];
            if (longName != null)
            {
                var chars = longName.ToCharArray().ToList();
                var count = (chars.Count + 12) / 13;
                if (chars.Count % 13 != 0) { chars.Add('\0'); while (chars.Count % 13 != 0) chars.Add('￿'); }
                for (var n = count; n >= 1; n--)
                {
                    var e = new byte[32];
                    e[0] = deleted ? (byte)0xE5 : (byte)(n | (n == count ? 0x40 : 0));
                    e[11] = 0x0F;
                    e[13] = checksum;
                    var part = chars.Skip((n - 1) * 13).Take(13).ToArray();
                    for (var k = 0; k < 5; k++) BitConverter.GetBytes((ushort)part[k]).CopyTo(e, 1 + k * 2);
                    for (var k = 0; k < 6; k++) BitConverter.GetBytes((ushort)part[5 + k]).CopyTo(e, 14 + k * 2);
                    for (var k = 0; k < 2; k++) BitConverter.GetBytes((ushort)part[11 + k]).CopyTo(e, 28 + k * 2);
                    entries.Add(e);
                }
            }
            var s = new byte[32];
            sn.CopyTo(s, 0);
            if (deleted) s[0] = 0xE5;
            s[11] = (byte)(isFolder ? 0x10 : 0x20);
            var date = (ushort)(((Modified.Year - 1980) << 9) | (Modified.Month << 5) | Modified.Day);
            var time = (ushort)((Modified.Hour << 11) | (Modified.Minute << 5) | (Modified.Second / 2));
            BitConverter.GetBytes(time).CopyTo(s, 22);
            BitConverter.GetBytes(date).CopyTo(s, 24);
            BitConverter.GetBytes(date).CopyTo(s, 16);
            BitConverter.GetBytes(deleted ? (ushort)0 : (ushort)(cluster >> 16)).CopyTo(s, 20);
            BitConverter.GetBytes((ushort)(cluster & 0xFFFF)).CopyTo(s, 26);
            BitConverter.GetBytes((uint)size).CopyTo(s, 28);
            entries.Add(s);
        }

        /// <summary>The "." and ".." entries every subfolder starts with.</summary>
        public void Dots(long folder, long parent)
        {
            foreach (var (name, c) in new[] { (".", folder), ("..", parent) })
            {
                var e = new byte[32];
                Encoding.ASCII.GetBytes(name.PadRight(11)).CopyTo(e, 0);
                e[11] = 0x10;
                BitConverter.GetBytes((ushort)(c >> 16)).CopyTo(e, 20);
                BitConverter.GetBytes((ushort)(c & 0xFFFF)).CopyTo(e, 26);
                _folders[folder].Add(e);
            }
        }

        public void FreeFolder(long cluster) => _fat[cluster] = 0;

        public string Save(string path)
        {
            var b = new byte[512];
            b[0] = 0xEB; b[1] = 0x58; b[2] = 0x90;
            Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(b, 3);
            BitConverter.GetBytes((ushort)Sector).CopyTo(b, 11);
            b[13] = 1;
            BitConverter.GetBytes((ushort)Reserved).CopyTo(b, 14);
            b[16] = 2;
            b[21] = 0xF8;
            BitConverter.GetBytes((uint)(Data.Length / Sector)).CopyTo(b, 32);
            BitConverter.GetBytes((uint)FatSectors).CopyTo(b, 36);
            BitConverter.GetBytes(2u).CopyTo(b, 44);
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(b, 82);
            b[510] = 0x55; b[511] = 0xAA;
            b.CopyTo(Data, 0);

            foreach (var pair in _folders)
            {
                var bytes = pair.Value.SelectMany(x => x).ToArray();
                if (bytes.Length > Sector * _sizes[pair.Key]) throw new Exception("test folder too big");
                bytes.CopyTo(Data, Offset(pair.Key));
            }
            for (var copy = 0; copy < 2; copy++)
                for (var c = 0; c < _fat.Length; c++)
                    BitConverter.GetBytes(_fat[c]).CopyTo(Data, (Reserved + copy * FatSectors) * Sector + c * 4);
            File.WriteAllBytes(path, Data);
            return path;
        }
    }

    /// <summary>Builds an exFAT drive image byte by byte: boot sector, FAT, allocation bitmap and entry sets.</summary>
    internal sealed class ExFatImage
    {
        public const int Sector = 512;
        public const int Cluster = 4096;
        public const long Clusters = 1000;
        public const long FatOffsetSectors = 128;
        public const long HeapOffsetSectors = 256;
        public readonly byte[] Data = new byte[HeapOffsetSectors * Sector + Clusters * Cluster];
        private readonly uint[] _fat = new uint[Clusters + 2];
        private readonly bool[] _used = new bool[Clusters + 2];
        private readonly Dictionary<long, List<byte[]>> _folders = new Dictionary<long, List<byte[]>>();

        public static readonly DateTime Modified = new DateTime(2026, 3, 14, 18, 4, 58, DateTimeKind.Utc);

        public ExFatImage()
        {
            _fat[0] = 0xFFFFFFF8;
            _fat[1] = 0xFFFFFFFF;
            Use(2, 1, true); // bitmap
            Use(4, 1, true); // root
            _folders[4] = new List<byte[]>();
            var bitmap = new byte[32];
            bitmap[0] = 0x81;
            BitConverter.GetBytes(2u).CopyTo(bitmap, 20);
            BitConverter.GetBytes((ulong)((Clusters + 7) / 8)).CopyTo(bitmap, 24);
            _folders[4].Add(bitmap);
        }

        public long Offset(long cluster) => HeapOffsetSectors * Sector + (cluster - 2) * Cluster;
        public void Write(long cluster, byte[] content) => content.CopyTo(Data, Offset(cluster));
        public void Folder(long cluster) => _folders[cluster] = new List<byte[]>();

        public void Use(long first, long count, bool chain)
        {
            for (var c = first; c < first + count; c++)
            {
                _used[c] = true;
                if (chain) _fat[c] = c == first + count - 1 ? 0xFFFFFFFF : (uint)(c + 1);
            }
        }

        public void Chain(params long[] clusters)
        {
            for (var i = 0; i < clusters.Length; i++) _fat[clusters[i]] = i == clusters.Length - 1 ? 0xFFFFFFFF : (uint)clusters[i + 1];
        }

        public void MarkUsed(long cluster) => _used[cluster] = true;

        public void Set(long folder, string name, long cluster, long size, bool deleted, bool noFatChain, bool isFolder = false)
        {
            var nameEntries = (name.Length + 14) / 15;
            var file = new byte[32];
            file[0] = (byte)(deleted ? 0x05 : 0x85);
            file[1] = (byte)(1 + nameEntries);
            BitConverter.GetBytes((ushort)(isFolder ? 0x10 : 0x20)).CopyTo(file, 4);
            var ts = (uint)((((Modified.Year - 1980) << 9) | (Modified.Month << 5) | Modified.Day) << 16) |
                     (uint)((Modified.Hour << 11) | (Modified.Minute << 5) | (Modified.Second / 2));
            BitConverter.GetBytes(ts).CopyTo(file, 8);
            BitConverter.GetBytes(ts).CopyTo(file, 12);
            file[22] = 0x80; file[23] = 0x80; // UTC
            var stream = new byte[32];
            stream[0] = (byte)(deleted ? 0x40 : 0xC0);
            stream[1] = (byte)(1 | (noFatChain ? 2 : 0));
            stream[3] = (byte)name.Length;
            BitConverter.GetBytes((ulong)size).CopyTo(stream, 8);
            BitConverter.GetBytes((uint)cluster).CopyTo(stream, 20);
            BitConverter.GetBytes((ulong)size).CopyTo(stream, 24);
            var list = _folders[folder];
            list.Add(file);
            list.Add(stream);
            for (var n = 0; n < nameEntries; n++)
            {
                var e = new byte[32];
                e[0] = (byte)(deleted ? 0x41 : 0xC1);
                var part = name.Skip(n * 15).Take(15).ToArray();
                for (var k = 0; k < part.Length; k++) BitConverter.GetBytes((ushort)part[k]).CopyTo(e, 2 + k * 2);
                list.Add(e);
            }
        }

        public string Save(string path)
        {
            var b = new byte[512];
            b[0] = 0xEB; b[1] = 0x76; b[2] = 0x90;
            Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(b, 3);
            BitConverter.GetBytes((ulong)(Data.Length / Sector)).CopyTo(b, 72);
            BitConverter.GetBytes((uint)FatOffsetSectors).CopyTo(b, 80);
            BitConverter.GetBytes((uint)(((Clusters + 2) * 4 + Sector - 1) / Sector)).CopyTo(b, 84);
            BitConverter.GetBytes((uint)HeapOffsetSectors).CopyTo(b, 88);
            BitConverter.GetBytes((uint)Clusters).CopyTo(b, 92);
            BitConverter.GetBytes(4u).CopyTo(b, 96);
            b[108] = 9;  // 512-byte sectors
            b[109] = 3;  // 8 sectors per cluster
            b[110] = 1;
            b[510] = 0x55; b[511] = 0xAA;
            b.CopyTo(Data, 0);

            for (var c = 0; c < _fat.Length; c++) BitConverter.GetBytes(_fat[c]).CopyTo(Data, FatOffsetSectors * Sector + c * 4);
            var bits = new byte[(Clusters + 7) / 8];
            for (var c = 2; c < _used.Length; c++) if (_used[c]) bits[(c - 2) / 8] |= (byte)(1 << ((c - 2) % 8));
            Write(2, bits);
            foreach (var pair in _folders) Write(pair.Key, pair.Value.SelectMany(x => x).ToArray());
            File.WriteAllBytes(path, Data);
            return path;
        }
    }
}
