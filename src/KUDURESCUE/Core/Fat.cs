using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kudurescue.Core
{
    public sealed class FatBoot
    {
        public int BytesPerSector { get; private set; }
        public int SectorsPerCluster { get; private set; }
        public int ReservedSectors { get; private set; }
        public int FatCount { get; private set; }
        public int RootEntries { get; private set; }
        public long TotalSectors { get; private set; }
        public long FatSectors { get; private set; }
        public long RootCluster { get; private set; }
        public int Type { get; private set; } // 12, 16 or 32

        public int ClusterSize => BytesPerSector * SectorsPerCluster;
        public long RootDirSectors => (RootEntries * 32L + BytesPerSector - 1) / BytesPerSector;
        public long FatStart => (long)ReservedSectors * BytesPerSector;
        public long RootDirStart => (ReservedSectors + FatCount * FatSectors) * BytesPerSector;
        public long FirstDataSector => ReservedSectors + FatCount * FatSectors + RootDirSectors;
        public long ClusterCount => (TotalSectors - FirstDataSector) / SectorsPerCluster;
        public long MaxCluster => ClusterCount + 1;

        /// <summary>A quick check that a boot sector looks like FAT (before parsing it fully).</summary>
        public static bool Looks(byte[] s)
        {
            if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA || (s[0] != 0xEB && s[0] != 0xE9)) return false;
            var bps = BitConverter.ToUInt16(s, 11);
            var spc = s[13];
            return bps >= 512 && bps <= 4096 && (bps & (bps - 1)) == 0 && spc > 0 && (spc & (spc - 1)) == 0 &&
                   BitConverter.ToUInt16(s, 14) > 0 && s[16] >= 1 && s[16] <= 2;
        }

        public static FatBoot Parse(byte[] s)
        {
            if (!Looks(s)) throw new InvalidDataException("This drive isn't formatted as FAT.");
            var b = new FatBoot
            {
                BytesPerSector = BitConverter.ToUInt16(s, 11),
                SectorsPerCluster = s[13],
                ReservedSectors = BitConverter.ToUInt16(s, 14),
                FatCount = s[16],
                RootEntries = BitConverter.ToUInt16(s, 17)
            };
            var total16 = BitConverter.ToUInt16(s, 19);
            b.TotalSectors = total16 != 0 ? total16 : BitConverter.ToUInt32(s, 32);
            var fat16 = BitConverter.ToUInt16(s, 22);
            b.FatSectors = fat16 != 0 ? fat16 : BitConverter.ToUInt32(s, 36);
            if (b.FatSectors == 0 || b.TotalSectors <= b.FirstDataSector) throw new InvalidDataException("The FAT boot sector is damaged.");

            // The type follows from the number of clusters, as the FAT specification says.
            var clusters = b.ClusterCount;
            b.Type = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
            if (b.Type == 32)
            {
                b.RootCluster = BitConverter.ToUInt32(s, 44);
                if (b.RootCluster < 2 || b.RootCluster > b.MaxCluster) throw new InvalidDataException("The FAT boot sector is damaged.");
            }
            return b;
        }
    }

    /// <summary>
    /// A FAT12, FAT16 or FAT32 drive (memory cards, USB sticks). When a file is deleted, the first character of its name
    /// is replaced by 0xE5 and its clusters are freed in the FAT, but its long name, size and first cluster stay in the folder.
    /// The cluster chain is lost, so the file is read from consecutive clusters, which is right for most files on these drives.
    /// </summary>
    public sealed class FatVolume : IVolume, IFreeSpace
    {
        private const int PageBytes = 1 << 20;
        private readonly bool _ownsDevice;
        private readonly Dictionary<long, byte[]> _pages = new Dictionary<long, byte[]>();

        private FatVolume(IBlockDevice device, FatBoot boot, bool ownsDevice)
        {
            Device = device;
            Boot = boot;
            _ownsDevice = ownsDevice;
        }

        public IBlockDevice Device { get; }
        public FatBoot Boot { get; }
        public string FormatName => "FAT" + Boot.Type;
        public int ClusterSize => Boot.ClusterSize;

        public long ClusterOffset(long cluster) => (Boot.FirstDataSector + (cluster - 2) * Boot.SectorsPerCluster) * Boot.BytesPerSector;

        public static FatVolume Open(IBlockDevice device, bool ownsDevice = true)
        {
            var sector = new byte[Math.Max(512, device.SectorSize)];
            device.Read(0, sector, 0, sector.Length);
            return new FatVolume(device, FatBoot.Parse(sector), ownsDevice);
        }

        // ------------------------------------------------------------------ the FAT

        /// <summary>The FAT entry of a cluster: 0 = free, a cluster number = next in the chain, high values = end or bad.</summary>
        public uint Entry(long cluster)
        {
            if (cluster < 0 || cluster > Boot.MaxCluster) return 0x0FFFFFF7;
            switch (Boot.Type)
            {
                case 32: return ReadUInt(cluster * 4, 4) & 0x0FFFFFFF;
                case 16: return ReadUInt(cluster * 2, 2);
                default:
                    var v = ReadUInt(cluster * 3 / 2, 2);
                    return (cluster & 1) == 0 ? v & 0x0FFF : v >> 4;
            }
        }

        public IEnumerable<(long Start, long Length)> FreeRanges()
        {
            long start = -1;
            for (long c = 2; c <= Boot.MaxCluster + 1; c++)
            {
                var free = c <= Boot.MaxCluster && Entry(c) == 0;
                if (free && start < 0) start = c;
                else if (!free && start >= 0)
                {
                    yield return (ClusterOffset(start), (c - start) * ClusterSize);
                    start = -1;
                }
            }
        }

        private bool IsEnd(uint entry) => entry >= (Boot.Type == 32 ? 0x0FFFFFF8u : Boot.Type == 16 ? 0xFFF8u : 0xFF8u);

        public long CountUsed(long cluster, long length)
        {
            long used = 0;
            for (var c = cluster; c < cluster + length; c++) if (c < 2 || c > Boot.MaxCluster || Entry(c) != 0) used++;
            return used;
        }

        private uint ReadUInt(long offset, int size)
        {
            uint v = 0;
            for (var i = 0; i < size; i++) v |= (uint)FatByte(offset + i) << (8 * i);
            return v;
        }

        private byte FatByte(long offset)
        {
            var index = offset / PageBytes;
            byte[] page;
            lock (_pages)
            {
                if (!_pages.TryGetValue(index, out page))
                {
                    var fatBytes = Boot.FatSectors * Boot.BytesPerSector;
                    var n = (int)Math.Max(0, Math.Min(PageBytes, fatBytes - index * PageBytes));
                    page = new byte[n];
                    if (n > 0) SafeRead.Read(Device, Boot.FatStart + index * PageBytes, page, 0, n);
                    if (_pages.Count > 64) _pages.Clear();
                    _pages[index] = page;
                }
            }
            var i = (int)(offset % PageBytes);
            return i < page.Length ? page[i] : (byte)0xFF;
        }

        /// <summary>A folder's (or file's) clusters, following the FAT. Stops at the end, a free entry or a loop.</summary>
        public List<long> Chain(long first, int limit = 1 << 16)
        {
            var list = new List<long>();
            var seen = new HashSet<long>();
            var c = first;
            while (c >= 2 && c <= Boot.MaxCluster && list.Count < limit && seen.Add(c))
            {
                list.Add(c);
                var next = Entry(c);
                if (next == 0 || IsEnd(next) || next == (Boot.Type == 32 ? 0x0FFFFFF7u : Boot.Type == 16 ? 0xFFF7u : 0xFF7u)) break;
                c = next;
            }
            return list;
        }

        private byte[] ReadClusters(IList<long> clusters)
        {
            var data = new byte[clusters.Count * ClusterSize];
            for (var i = 0; i < clusters.Count; i++) SafeRead.Read(Device, ClusterOffset(clusters[i]), data, i * ClusterSize, ClusterSize);
            return data;
        }

        // ------------------------------------------------------------------ scan

        private sealed class Folder
        {
            public string Path;
            public long Cluster;      // 0 = FAT12/16 root area
            public bool Deleted;
        }

        public void Scan(QuickScan scan, CancellationToken cancel)
        {
            scan.ProgressLabel = "Folders";
            var queue = new Queue<Folder>();
            var visited = new HashSet<long>();
            queue.Enqueue(new Folder { Path = string.Empty, Cluster = Boot.Type == 32 ? Boot.RootCluster : 0 });
            if (Boot.Type == 32) visited.Add(Boot.RootCluster);
            long done = 0, total = 1;
            scan.SetTotal(total);

            while (queue.Count > 0)
            {
                scan.Checkpoint(cancel);
                var folder = queue.Dequeue();
                byte[] data;
                try { data = FolderData(folder); }
                catch (IOException) { scan.Unreadable++; data = new byte[0]; }

                foreach (var entry in Entries(data, folder.Deleted))
                {
                    if (entry.IsFolder)
                    {
                        if (entry.Cluster < 2 || !visited.Add(entry.Cluster)) continue;
                        queue.Enqueue(new Folder { Path = Join(folder.Path, entry.Name), Cluster = entry.Cluster, Deleted = entry.Deleted || folder.Deleted });
                        total++;
                    }
                    else if (entry.Deleted)
                    {
                        var file = Describe(entry, folder.Path);
                        if (file != null) scan.Add(file);
                    }
                }
                scan.SetDone(++done);
                scan.SetTotal(total);
            }
        }

        private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "\\" + name;

        private byte[] FolderData(Folder folder)
        {
            if (folder.Cluster == 0)
            {
                var root = new byte[Boot.RootDirSectors * Boot.BytesPerSector];
                SafeRead.Read(Device, Boot.RootDirStart, root, 0, root.Length);
                return root;
            }
            if (!folder.Deleted) return ReadClusters(Chain(folder.Cluster));

            // A deleted folder's chain is gone: read consecutive clusters while they still look like the folder.
            var clusters = new List<long>();
            for (var c = folder.Cluster; c <= Boot.MaxCluster && clusters.Count < 64; c++)
            {
                if (clusters.Count > 0 && Entry(c) != 0) break; // that cluster belongs to another file now
                clusters.Add(c);
                var one = ReadClusters(new[] { c });
                if (EndsFolder(one)) break;
            }
            return ReadClusters(clusters);
        }

        private static bool EndsFolder(byte[] data)
        {
            for (var p = 0; p + 32 <= data.Length; p += 32) if (data[p] == 0) return true;
            return false;
        }

        internal sealed class DirEntry
        {
            public string Name;
            public bool Deleted;
            public bool IsFolder;
            public long Cluster;
            public bool ClusterGuessed;
            public long Size;
            public DateTime Created;
            public DateTime Modified;
            public bool UnsureName;
        }

        /// <summary>Reads the 32-byte entries of a folder, putting long names back together, also for deleted entries.</summary>
        internal IEnumerable<DirEntry> Entries(byte[] data, bool folderDeleted)
        {
            var lfn = new List<byte[]>();
            for (var p = 0; p + 32 <= data.Length; p += 32)
            {
                var b0 = data[p];
                if (b0 == 0x00) yield break; // end of the folder
                var attr = data[p + 11];
                if (attr == 0x0F)
                {
                    lfn.Add(data.Skip(p).Take(32).ToArray());
                    continue;
                }
                var parts = lfn;
                lfn = new List<byte[]>();
                if ((attr & 0x08) != 0 || b0 == (byte)'.') continue; // volume label, "." and ".."

                var deleted = b0 == 0xE5 || folderDeleted;
                var entry = new DirEntry
                {
                    Deleted = deleted,
                    IsFolder = (attr & 0x10) != 0,
                    Size = BitConverter.ToUInt32(data, p + 28),
                    Created = DosTime(BitConverter.ToUInt16(data, p + 16), BitConverter.ToUInt16(data, p + 14)),
                    Modified = DosTime(BitConverter.ToUInt16(data, p + 24), BitConverter.ToUInt16(data, p + 22))
                };
                var low = BitConverter.ToUInt16(data, p + 26);
                var high = Boot.Type == 32 ? BitConverter.ToUInt16(data, p + 20) : 0;
                entry.Cluster = ((long)high << 16) | low;

                var shortName = data.Skip(p).Take(11).ToArray();
                entry.Name = LongName(parts, shortName, b0 == 0xE5) ?? ShortName(shortName, data[p + 12], b0 == 0xE5, parts, out entry.UnsureName);
                if (entry.Name.Length == 0) continue;

                // Windows clears the top half of a deleted FAT32 entry's first cluster. Work it out when the drive is big enough to need it.
                if (b0 == 0xE5 && Boot.Type == 32 && high == 0 && Boot.MaxCluster > 0xFFFF && entry.Cluster != 0)
                {
                    entry.Cluster = entry.IsFolder ? GuessFolderCluster(low) : GuessFileCluster(low, entry);
                    entry.ClusterGuessed = true;
                }
                if (entry.IsFolder && entry.Deleted && entry.Cluster >= 2 && !LooksLikeFolder(entry.Cluster)) continue; // reused
                yield return entry;
            }
        }

        private static string LongName(List<byte[]> parts, byte[] shortName, bool deleted)
        {
            if (parts.Count == 0) return null;
            var checksum = parts[0][13];
            if (parts.Any(x => x[13] != checksum)) return null;
            if (deleted)
            {
                // The first character was overwritten; the checksum tells which one it was, and confirms the long name belongs here.
                var candidate = (byte[])shortName.Clone();
                var match = false;
                for (var c = 0x20; c <= 0xFF && !match; c++)
                {
                    candidate[0] = (byte)c;
                    match = Checksum(candidate) == checksum;
                }
                if (!match) return null;
            }
            else if (Checksum(shortName) != checksum) return null;

            var chars = new List<char>();
            for (var i = parts.Count - 1; i >= 0; i--) // the entry nearest the short name holds the start of the name
            {
                var e = parts[i];
                foreach (var (offset, count) in new[] { (1, 5), (14, 6), (28, 2) })
                    for (var k = 0; k < count; k++) chars.Add((char)BitConverter.ToUInt16(e, offset + k * 2));
            }
            var end = chars.IndexOf('\0');
            var name = new string((end >= 0 ? chars.Take(end) : chars).Where(c => c != '￿').ToArray());
            return name.Length == 0 ? null : name;
        }

        private static string ShortName(byte[] raw, byte caseFlags, bool deleted, List<byte[]> parts, out bool unsure)
        {
            var bytes = (byte[])raw.Clone();
            unsure = false;
            if (deleted)
            {
                // Without a long name the first character is lost; '_' stands in for it.
                bytes[0] = (byte)'_';
                unsure = true;
            }
            else if (bytes[0] == 0x05) bytes[0] = 0xE5;
            var latin = Encoding.GetEncoding(28591);
            var stem = latin.GetString(bytes, 0, 8).TrimEnd(' ');
            var ext = latin.GetString(bytes, 8, 3).TrimEnd(' ');
            if ((caseFlags & 0x08) != 0) stem = stem.ToLowerInvariant();
            if ((caseFlags & 0x10) != 0) ext = ext.ToLowerInvariant();
            return ext.Length > 0 ? stem + "." + ext : stem;
        }

        public static byte Checksum(byte[] shortName)
        {
            byte sum = 0;
            for (var i = 0; i < 11; i++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + shortName[i]);
            return sum;
        }

        private static DateTime DosTime(ushort date, ushort time)
        {
            if (date == 0) return DateTime.MinValue;
            try
            {
                var local = new DateTime(1980 + (date >> 9), (date >> 5) & 0x0F, date & 0x1F, time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2, DateTimeKind.Local);
                return local.ToUniversalTime();
            }
            catch (ArgumentException)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>A deleted folder still begins with its "." entry, which holds its own full cluster number.</summary>
        private bool LooksLikeFolder(long cluster)
        {
            if (cluster < 2 || cluster > Boot.MaxCluster) return false;
            var first = ReadClusters(new[] { cluster });
            if (first.Length < 64 || first[0] != (byte)'.' || first[1] != (byte)' ' || (first[11] & 0x10) == 0) return false;
            var own = ((long)(Boot.Type == 32 ? BitConverter.ToUInt16(first, 20) : 0) << 16) | BitConverter.ToUInt16(first, 26);
            return own == cluster && first[32] == (byte)'.' && first[33] == (byte)'.';
        }

        private long GuessFolderCluster(ushort low)
        {
            for (long high = 0; (high << 16) <= Boot.MaxCluster; high++)
            {
                var c = (high << 16) | low;
                if (LooksLikeFolder(c)) return c;
            }
            return low;
        }

        /// <summary>
        /// Picks the top half of the first cluster: the place must be free and fit the file; when several fit, the one whose
        /// data starts like the file's type wins.
        /// </summary>
        private long GuessFileCluster(ushort low, DirEntry entry)
        {
            var needed = Math.Max(1, (entry.Size + ClusterSize - 1) / ClusterSize);
            var candidates = new List<long>();
            for (long high = 0; (high << 16) <= Boot.MaxCluster; high++)
            {
                var c = (high << 16) | low;
                if (c >= 2 && c + needed - 1 <= Boot.MaxCluster && Entry(c) == 0) candidates.Add(c);
            }
            if (candidates.Count == 0) return low;
            if (candidates.Count == 1) return candidates[0];
            var ext = System.IO.Path.GetExtension(entry.Name);
            foreach (var c in candidates)
                if (Signatures.Matches(ext, ReadClusters(new[] { c }).Take(64).ToArray()) == true) return c;
            return candidates[0];
        }

        private FoundFile Describe(DirEntry e, string folder)
        {
            if (e.Size > 0 && (e.Cluster < 2 || e.Cluster > Boot.MaxCluster)) return null;
            var f = new FoundFile
            {
                Source = FoundSource.Index,
                Name = e.Name,
                Folder = folder,
                Size = e.Size,
                InitializedSize = e.Size,
                Created = e.Created,
                Modified = e.Modified,
                Kind = FileKinds.Of(e.Name)
            };
            var needed = (e.Size + ClusterSize - 1) / ClusterSize;
            if (needed > 0)
            {
                var length = Math.Min(needed, Boot.MaxCluster - e.Cluster + 1);
                f.Runs = new List<DataRun> { new DataRun(0, e.Cluster, length) };
                if (length < needed) f.Runs.Add(new DataRun(length, -1, needed - length)); // past the end of the drive: zeros
            }
            else f.Runs = new List<DataRun>();

            if (e.ClusterGuessed)
            {
                var verified = needed > 0 && Signatures.Matches(f.Extension, ReadClusters(new[] { e.Cluster }).Take(64).ToArray()) == true;
                if (!verified)
                {
                    f.Chance = Chance.Unsure;
                    f.Note = "Windows only kept part of where this file starts; KUDURESCUE worked out the rest.";
                }
            }
            QuickScan.SetChance(f, ClusterSize, CountUsed);
            if (needed > 1 && f.Chance != Chance.Overwritten)
                f.Note = (f.Note == null ? string.Empty : f.Note + " ") + "On FAT drives the file is read from consecutive clusters; if it was stored in pieces, parts may be wrong.";
            if (e.UnsureName) f.Note = (f.Note == null ? string.Empty : f.Note + " ") + "The first letter of its name was lost when it was deleted.";
            return f;
        }

        public void Dispose()
        {
            if (_ownsDevice) Device.Dispose();
        }
    }
}
