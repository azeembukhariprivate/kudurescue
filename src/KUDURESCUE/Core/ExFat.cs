using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>
    /// An exFAT drive (memory cards and USB sticks over 32 GB). Each file is a set of 32-byte entries: a File entry
    /// (times, attributes), a Stream entry (size, first cluster, whether it's stored in one piece) and File Name entries.
    /// Deleting a file only clears the "in use" bit of each entry and frees its clusters in the allocation bitmap,
    /// so names, sizes and locations survive.
    /// </summary>
    public sealed class ExFatVolume : IVolume
    {
        private const int PageBytes = 1 << 20;
        private readonly bool _ownsDevice;
        private readonly Dictionary<long, byte[]> _fatPages = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> _bitmapPages = new Dictionary<long, byte[]>();
        private List<long> _bitmapClusters = new List<long>();
        private long _bitmapLength;

        private ExFatVolume(IBlockDevice device, bool ownsDevice)
        {
            Device = device;
            _ownsDevice = ownsDevice;
        }

        public IBlockDevice Device { get; }
        public string FormatName => "exFAT";
        public int BytesPerSector { get; private set; }
        public int ClusterSize { get; private set; }
        public long FatOffset { get; private set; }     // bytes
        public long HeapOffset { get; private set; }    // bytes
        public long ClusterCount { get; private set; }
        public long RootCluster { get; private set; }
        public long MaxCluster => ClusterCount + 1;

        public long ClusterOffset(long cluster) => HeapOffset + (cluster - 2) * ClusterSize;

        public static ExFatVolume Open(IBlockDevice device, bool ownsDevice = true)
        {
            var s = new byte[Math.Max(512, device.SectorSize)];
            device.Read(0, s, 0, s.Length);
            if (Encoding.ASCII.GetString(s, 3, 8) != "EXFAT   ") throw new InvalidDataException("This drive isn't formatted as exFAT.");
            int bpsShift = s[108], spcShift = s[109];
            if (bpsShift < 9 || bpsShift > 12 || spcShift > 25 - bpsShift) throw new InvalidDataException("The exFAT boot sector is damaged.");
            var v = new ExFatVolume(device, ownsDevice)
            {
                BytesPerSector = 1 << bpsShift,
                ClusterSize = 1 << (bpsShift + spcShift)
            };
            v.FatOffset = (long)BitConverter.ToUInt32(s, 80) * v.BytesPerSector;
            v.HeapOffset = (long)BitConverter.ToUInt32(s, 88) * v.BytesPerSector;
            v.ClusterCount = BitConverter.ToUInt32(s, 92);
            v.RootCluster = BitConverter.ToUInt32(s, 96);
            if (v.ClusterCount <= 0 || v.RootCluster < 2 || v.RootCluster > v.MaxCluster) throw new InvalidDataException("The exFAT boot sector is damaged.");
            v.FindBitmap();
            return v;
        }

        // ------------------------------------------------------------------ FAT and allocation bitmap

        public uint Entry(long cluster)
        {
            if (cluster < 0 || cluster > MaxCluster) return 0xFFFFFFF7;
            var offset = cluster * 4;
            var page = Page(_fatPages, offset / PageBytes, FatOffset, (MaxCluster + 1) * 4, null);
            var i = (int)(offset % PageBytes);
            return i + 4 <= page.Length ? BitConverter.ToUInt32(page, i) : 0xFFFFFFF7;
        }

        public List<long> Chain(long first, long limit = 1 << 20)
        {
            var list = new List<long>();
            var seen = new HashSet<long>();
            var c = first;
            while (c >= 2 && c <= MaxCluster && list.Count < limit && seen.Add(c))
            {
                list.Add(c);
                var next = Entry(c);
                if (next < 2 || next >= 0xFFFFFFF7) break;
                c = next;
            }
            return list;
        }

        /// <summary>The allocation bitmap (entry type 0x81 in the root folder) says which clusters are in use.</summary>
        private void FindBitmap()
        {
            var root = ReadClusters(Chain(RootCluster, 4096));
            for (var p = 0; p + 32 <= root.Length; p += 32)
            {
                if (root[p] == 0) break;
                if (root[p] != 0x81 || (root[p + 1] & 1) != 0) continue; // the first bitmap (a second one exists only with two FATs)
                var first = BitConverter.ToUInt32(root, p + 20);
                _bitmapLength = (long)BitConverter.ToUInt64(root, p + 24);
                _bitmapClusters = Chain(first, (_bitmapLength + ClusterSize - 1) / ClusterSize);
                return;
            }
        }

        public bool IsUsed(long cluster)
        {
            if (cluster < 2 || cluster > MaxCluster) return true;
            if (_bitmapClusters.Count == 0) return false;
            var bit = cluster - 2;
            var byteIndex = bit / 8;
            var page = Page(_bitmapPages, byteIndex / PageBytes, -1, _bitmapLength, _bitmapClusters);
            var i = (int)(byteIndex % PageBytes);
            return i >= page.Length || (page[i] & (1 << (int)(bit % 8))) != 0;
        }

        public long CountUsed(long cluster, long length)
        {
            long used = 0;
            for (var c = cluster; c < cluster + length; c++) if (IsUsed(c)) used++;
            return used;
        }

        /// <summary>A 1 MB piece of the FAT (at a fixed offset) or of the bitmap (stored in clusters).</summary>
        private byte[] Page(Dictionary<long, byte[]> cache, long index, long start, long totalBytes, List<long> clusters)
        {
            lock (cache)
            {
                if (cache.TryGetValue(index, out var page)) return page;
                var n = (int)Math.Max(0, Math.Min(PageBytes, totalBytes - index * PageBytes));
                page = new byte[n];
                if (clusters == null)
                {
                    if (n > 0) SafeRead.Read(Device, start + index * PageBytes, page, 0, n);
                }
                else
                {
                    for (var done = 0; done < n;)
                    {
                        var pos = index * PageBytes + done;
                        var ci = (int)(pos / ClusterSize);
                        var within = (int)(pos % ClusterSize);
                        var count = Math.Min(n - done, ClusterSize - within);
                        if (ci < clusters.Count) SafeRead.Read(Device, ClusterOffset(clusters[ci]) + within, page, done, count);
                        else for (var k = 0; k < count; k++) page[done + k] = 0xFF;
                        done += count;
                    }
                }
                if (cache.Count > 64) cache.Clear();
                cache[index] = page;
                return page;
            }
        }

        private byte[] ReadClusters(IList<long> clusters)
        {
            var data = new byte[clusters.Count * (long)ClusterSize];
            for (var i = 0; i < clusters.Count; i++) SafeRead.Read(Device, ClusterOffset(clusters[i]), data, i * ClusterSize, ClusterSize);
            return data;
        }

        private List<long> Contiguous(long first, long count)
        {
            var list = new List<long>();
            for (long i = 0; i < count && first + i <= MaxCluster; i++) list.Add(first + i);
            return list;
        }

        // ------------------------------------------------------------------ scan

        private sealed class Folder
        {
            public string Path;
            public List<long> Clusters;
            public bool Deleted;
        }

        internal sealed class EntrySet
        {
            public string Name;
            public bool Deleted;
            public bool IsFolder;
            public long FirstCluster;
            public bool NoFatChain;
            public long Size;
            public long ValidSize;
            public DateTime Created;
            public DateTime Modified;
        }

        public void Scan(QuickScan scan, CancellationToken cancel)
        {
            scan.ProgressLabel = "Folders";
            var queue = new Queue<Folder>();
            var visited = new HashSet<long> { RootCluster };
            queue.Enqueue(new Folder { Path = string.Empty, Clusters = Chain(RootCluster, 1 << 16) });
            long done = 0, total = 1;
            scan.SetTotal(total);

            while (queue.Count > 0)
            {
                scan.Checkpoint(cancel);
                var folder = queue.Dequeue();
                byte[] data;
                try { data = ReadClusters(folder.Clusters); }
                catch (IOException) { scan.Unreadable++; data = new byte[0]; }

                foreach (var set in Entries(data))
                {
                    var deleted = set.Deleted || folder.Deleted;
                    if (set.IsFolder)
                    {
                        if (set.FirstCluster < 2 || set.FirstCluster > MaxCluster || !visited.Add(set.FirstCluster)) continue;
                        var count = Math.Max(1, (set.Size + ClusterSize - 1) / ClusterSize);
                        var clusters = set.NoFatChain || deleted ? Contiguous(set.FirstCluster, Math.Min(count, 4096)) : Chain(set.FirstCluster, 4096);
                        if (deleted && !set.NoFatChain)
                        {
                            // A deleted folder that used the FAT: follow it if it still holds together, otherwise read on from the start.
                            var chained = Chain(set.FirstCluster, count);
                            if (chained.Count == count) clusters = chained;
                        }
                        queue.Enqueue(new Folder { Path = folder.Path.Length == 0 ? set.Name : folder.Path + "\\" + set.Name, Clusters = clusters, Deleted = deleted });
                        total++;
                    }
                    else if (deleted)
                    {
                        var file = Describe(set, folder.Path);
                        if (file != null) scan.Add(file);
                    }
                }
                scan.SetDone(++done);
                scan.SetTotal(total);
            }
        }

        /// <summary>Reads a folder's entry sets, live and deleted. A deleted set has the "in use" bit (0x80) cleared on every entry.</summary>
        internal IEnumerable<EntrySet> Entries(byte[] data)
        {
            for (var p = 0; p + 32 <= data.Length; p += 32)
            {
                var type = data[p];
                if (type == 0x00) yield break; // end of the folder
                if ((type & 0x7F) != 0x05) continue; // not a File entry (live 0x85 or deleted 0x05)

                var deleted = (type & 0x80) == 0;
                int secondary = data[p + 1];
                if (secondary < 2 || secondary > 18 || p + (secondary + 1) * 32 > data.Length) continue;
                var streamAt = p + 32;
                if ((data[streamAt] & 0x7F) != 0x40) continue; // the Stream entry must follow
                if (((data[streamAt] & 0x80) == 0) != deleted) continue; // a mix of live and deleted: not one set

                var set = new EntrySet
                {
                    Deleted = deleted,
                    IsFolder = (BitConverter.ToUInt16(data, p + 4) & 0x10) != 0,
                    Created = Timestamp(BitConverter.ToUInt32(data, p + 8), data[p + 20], data[p + 22]),
                    Modified = Timestamp(BitConverter.ToUInt32(data, p + 12), data[p + 21], data[p + 23]),
                    NoFatChain = (data[streamAt + 1] & 0x02) != 0,
                    ValidSize = (long)BitConverter.ToUInt64(data, streamAt + 8),
                    FirstCluster = BitConverter.ToUInt32(data, streamAt + 20),
                    Size = (long)BitConverter.ToUInt64(data, streamAt + 24)
                };
                int nameLength = data[streamAt + 3];
                var name = new StringBuilder();
                for (var k = 2; k <= secondary && name.Length < nameLength; k++)
                {
                    var at = p + k * 32;
                    if ((data[at] & 0x7F) != 0x41) break;
                    for (var c = 0; c < 15 && name.Length < nameLength; c++) name.Append((char)BitConverter.ToUInt16(data, at + 2 + c * 2));
                }
                if (name.Length == 0 || name.Length < nameLength) continue;
                set.Name = name.ToString();
                yield return set;
                p += secondary * 32;
            }
        }

        /// <summary>exFAT times: DOS date and time, plus 10 ms steps and the offset from UTC when it was recorded.</summary>
        private static DateTime Timestamp(uint value, byte tenMs, byte utcOffset)
        {
            if (value == 0) return DateTime.MinValue;
            try
            {
                var date = (int)(value >> 16);
                var time = (int)(value & 0xFFFF);
                var t = new DateTime(1980 + (date >> 9), (date >> 5) & 0x0F, date & 0x1F, time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2)
                    .AddMilliseconds(tenMs * 10);
                if ((utcOffset & 0x80) != 0)
                {
                    var quarters = (sbyte)(utcOffset << 1) >> 1; // 7-bit signed number of 15-minute steps
                    return DateTime.SpecifyKind(t.AddMinutes(-15 * quarters), DateTimeKind.Utc);
                }
                return DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime();
            }
            catch (ArgumentException)
            {
                return DateTime.MinValue;
            }
        }

        private FoundFile Describe(EntrySet s, string folder)
        {
            if (s.Size < 0 || s.Size > Device.Length) return null;
            var needed = (s.Size + ClusterSize - 1) / ClusterSize;
            if (needed > 0 && (s.FirstCluster < 2 || s.FirstCluster > MaxCluster)) return null;
            var f = new FoundFile
            {
                Source = FoundSource.Index,
                Name = s.Name,
                Folder = folder,
                Size = s.Size,
                InitializedSize = Math.Min(s.ValidSize, s.Size),
                Created = s.Created,
                Modified = s.Modified,
                Kind = FileKinds.Of(s.Name),
                Runs = new List<DataRun>()
            };
            if (needed > 0)
            {
                List<long> clusters = null;
                if (!s.NoFatChain)
                {
                    // Stored in pieces: the FAT may still hold the chain (exFAT doesn't have to clear it). Use it only if it's complete.
                    var chained = Chain(s.FirstCluster, needed);
                    if (chained.Count == needed) clusters = chained;
                    else f.Note = "This file was stored in pieces and the map of them is gone; it's read from consecutive clusters, so parts may be wrong.";
                }
                clusters = clusters ?? Contiguous(s.FirstCluster, needed);
                f.Runs = ToRuns(clusters);
                if (clusters.Count < needed) f.Runs.Add(new DataRun(clusters.Count, -1, needed - clusters.Count));
            }
            QuickScan.SetChance(f, ClusterSize, CountUsed);
            return f;
        }

        public static List<DataRun> ToRuns(List<long> clusters)
        {
            var runs = new List<DataRun>();
            for (var i = 0; i < clusters.Count;)
            {
                var start = i;
                while (i + 1 < clusters.Count && clusters[i + 1] == clusters[i] + 1) i++;
                runs.Add(new DataRun(start, clusters[start], i - start + 1));
                i++;
            }
            return runs;
        }

        public void Dispose()
        {
            if (_ownsDevice) Device.Dispose();
        }
    }
}
