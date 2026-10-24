using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>An NTFS drive opened read-only: its MFT (the index of every file) and its map of used clusters.</summary>
    public sealed class NtfsVolume : IDisposable
    {
        private readonly bool _ownsDevice;

        private NtfsVolume(IBlockDevice device, NtfsBoot boot, bool ownsDevice)
        {
            Device = device;
            Boot = boot;
            _ownsDevice = ownsDevice;
        }

        public IBlockDevice Device { get; }
        public NtfsBoot Boot { get; }
        public List<DataRun> MftRuns { get; private set; }
        public long MftSize { get; private set; }
        public ClusterBitmap Bitmap { get; private set; }

        public int ClusterSize => Boot.ClusterSize;
        public int RecordSize => Boot.RecordSize;
        public long RecordCount => MftSize / RecordSize;

        public static NtfsVolume Open(IBlockDevice device, bool ownsDevice = true)
        {
            var sector = new byte[Math.Max(512, device.SectorSize)];
            device.Read(0, sector, 0, sector.Length);
            if (NtfsBoot.IsBitLocker(sector))
                throw new InvalidDataException("This drive is locked with BitLocker. Unlock it in File Explorer first, then try again.");
            var boot = NtfsBoot.Parse(sector);
            var volume = new NtfsVolume(device, boot, ownsDevice);
            volume.LoadMft();
            volume.LoadBitmap();
            return volume;
        }

        private void LoadMft()
        {
            // Record 0 describes the MFT itself. Read it from the position in the boot sector, or from the mirror copy.
            FileRecord mft = null;
            foreach (var lcn in new[] { Boot.MftLcn, Boot.MftMirrLcn })
            {
                var buffer = new byte[RecordSize];
                try { Device.Read(lcn * ClusterSize, buffer, 0, RecordSize); }
                catch (IOException) { continue; }
                mft = FileRecord.Parse(buffer, 0, RecordSize, 0);
                if (mft?.Find(AttrType.Data)?.Runs != null) break;
                mft = null;
            }
            if (mft == null) throw new InvalidDataException("The drive's file index (MFT) can't be read. A Deep scan may still find files.");

            var data = mft.Find(AttrType.Data);
            MftRuns = data.Runs;
            MftSize = data.DataSize;
            if (mft.Find(AttrType.AttributeList) != null)
            {
                // A very large or fragmented MFT continues in other records, which are inside the part we already know.
                var full = DataAttribute(mft, "");
                if (full != null)
                {
                    MftRuns = full.Runs;
                    MftSize = full.DataSize;
                }
            }
        }

        private void LoadBitmap()
        {
            var record = ReadRecord(6);
            var data = record == null ? null : DataAttribute(record, "");
            Bitmap = data?.Runs != null ? new ClusterBitmap(this, data.Runs, Boot.TotalClusters) : ClusterBitmap.Unknown(Boot.TotalClusters);
        }

        public FileRecord ReadRecord(long number)
        {
            var buffer = new byte[RecordSize];
            try { ReadRuns(MftRuns, number * RecordSize, buffer, 0, RecordSize); }
            catch (IOException) { return null; }
            return FileRecord.Parse(buffer, 0, RecordSize, number);
        }

        /// <summary>
        /// The complete data attribute of a file, joining the pieces kept in extension records when the file has an attribute list.
        /// Returns null when there's none.
        /// </summary>
        public NtfsAttribute DataAttribute(FileRecord record, string name)
        {
            var list = record.Find(AttrType.AttributeList);
            var own = record.Find(AttrType.Data, name);
            if (list == null) return own;

            byte[] value = list.Value;
            if (list.NonResident)
            {
                if (list.DataSize <= 0 || list.DataSize > 16 * 1024 * 1024) return own;
                value = new byte[list.DataSize];
                try { ReadRuns(list.Runs, 0, value, 0, value.Length); }
                catch (IOException) { return own; }
            }
            if (value == null) return own;

            var pieces = new List<NtfsAttribute>();
            if (own != null) pieces.Add(own);
            var segments = new HashSet<long>();
            for (var pos = 0; pos + 26 <= value.Length;)
            {
                var type = BitConverter.ToUInt32(value, pos);
                var length = BitConverter.ToUInt16(value, pos + 4);
                if (length < 26 || pos + length > value.Length) break;
                var nameLength = value[pos + 6];
                var nameOffset = value[pos + 7];
                var entryName = nameLength > 0 && nameOffset + nameLength * 2 <= length
                    ? System.Text.Encoding.Unicode.GetString(value, pos + nameOffset, nameLength * 2) : string.Empty;
                var segment = BitConverter.ToInt64(value, pos + 16) & 0x0000FFFFFFFFFFFF;
                if (type == (uint)AttrType.Data && entryName == name && segment != record.Number) segments.Add(segment);
                pos += length;
            }
            foreach (var segment in segments)
            {
                var ext = ReadRecord(segment);
                if (ext == null || ext.BaseRecord != record.Number) continue;
                pieces.AddRange(ext.Attributes.Where(a => a.Type == AttrType.Data && a.Name == name && a.NonResident));
            }
            if (pieces.Count == 0) return null;
            var first = pieces.FirstOrDefault(p => !p.NonResident || p.StartVcn == 0) ?? pieces[0];
            if (!first.NonResident) return first;

            var merged = new NtfsAttribute
            {
                Type = AttrType.Data, Name = name, NonResident = true, Flags = first.Flags,
                CompressionUnit = first.CompressionUnit, AllocatedSize = first.AllocatedSize,
                DataSize = first.DataSize, InitializedSize = first.InitializedSize,
                Runs = pieces.Where(p => p.NonResident).OrderBy(p => p.StartVcn).SelectMany(p => p.Runs).ToList()
            };
            merged.LastVcn = merged.Runs.Count == 0 ? -1 : merged.Runs[merged.Runs.Count - 1].Vcn + merged.Runs[merged.Runs.Count - 1].Length - 1;
            return merged;
        }

        /// <summary>Reads bytes of a stream described by runs, as if it were one continuous file. Sparse parts read as zeros.</summary>
        public void ReadRuns(List<DataRun> runs, long position, byte[] buffer, int index, int count)
        {
            var cluster = ClusterSize;
            while (count > 0)
            {
                var vcn = position / cluster;
                var run = FindRun(runs, vcn);
                if (run == null) throw new IOException("Position outside the file's data.");
                var r = run.Value;
                var inRun = position - r.Vcn * cluster;
                var available = r.Length * cluster - inRun;
                var n = (int)Math.Min(count, available);
                if (r.IsSparse) Array.Clear(buffer, index, n);
                else
                {
                    var offset = r.Lcn * cluster + inRun;
                    if (offset + n > Device.Length) throw new IOException("Data lies past the end of the drive.");
                    Device.Read(offset, buffer, index, n);
                }
                position += n;
                index += n;
                count -= n;
            }
        }

        public static DataRun? FindRun(List<DataRun> runs, long vcn)
        {
            int lo = 0, hi = runs.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var r = runs[mid];
                if (vcn < r.Vcn) hi = mid - 1;
                else if (vcn >= r.Vcn + r.Length) lo = mid + 1;
                else return r;
            }
            return null;
        }

        public void Dispose()
        {
            if (_ownsDevice) Device.Dispose();
        }
    }

    /// <summary>NTFS's map of which clusters are in use ($Bitmap). Read in 1 MB pieces as needed.</summary>
    public sealed class ClusterBitmap
    {
        private const int PageBytes = 1 << 20;
        private readonly NtfsVolume _volume;
        private readonly List<DataRun> _runs;
        private readonly Dictionary<long, byte[]> _pages = new Dictionary<long, byte[]>();
        private readonly bool _unknown;

        public ClusterBitmap(NtfsVolume volume, List<DataRun> runs, long totalClusters)
        {
            _volume = volume;
            _runs = runs;
            TotalClusters = totalClusters;
        }

        private ClusterBitmap(long totalClusters)
        {
            TotalClusters = totalClusters;
            _unknown = true;
        }

        /// <summary>Used when the bitmap can't be read: every cluster counts as free, so chances may be too hopeful.</summary>
        public static ClusterBitmap Unknown(long totalClusters) => new ClusterBitmap(totalClusters);

        public long TotalClusters { get; }
        public bool IsUnknown => _unknown;

        public bool IsAllocated(long lcn)
        {
            if (_unknown) return false;
            if (lcn < 0 || lcn >= TotalClusters) return true;
            var page = Page(lcn / 8 / PageBytes);
            var b = (int)(lcn / 8 % PageBytes);
            return b < page.Length && (page[b] & (1 << (int)(lcn % 8))) != 0;
        }

        /// <summary>How many of these clusters are now used by other files.</summary>
        public long CountAllocated(long lcn, long length)
        {
            if (_unknown) return 0;
            long count = 0;
            var end = lcn + length;
            if (lcn < 0) return length;
            if (end > TotalClusters)
            {
                count += end - TotalClusters;
                end = TotalClusters;
            }
            var c = lcn;
            long pageIndex = -1;
            byte[] page = null;
            while (c < end)
            {
                var byteIndex = c >> 3;
                if (byteIndex / PageBytes != pageIndex)
                {
                    pageIndex = byteIndex / PageBytes;
                    page = Page(pageIndex);
                }
                var b = (int)(byteIndex % PageBytes);
                if (b >= page.Length)
                {
                    count += end - c; // past the end of the bitmap: count as used
                    break;
                }
                if ((c & 7) == 0 && end - c >= 8)
                {
                    count += PopCount(page[b]); // a whole byte (8 clusters) at a time
                    c += 8;
                }
                else
                {
                    if ((page[b] & (1 << (int)(c & 7))) != 0) count++;
                    c++;
                }
            }
            return count;
        }

        private static int PopCount(byte b)
        {
            var n = 0;
            for (; b != 0; b &= (byte)(b - 1)) n++;
            return n;
        }

        private byte[] Page(long index)
        {
            lock (_pages)
            {
                if (_pages.TryGetValue(index, out var page)) return page;
                var start = index * PageBytes;
                var size = (TotalClusters + 7) / 8;
                var n = (int)Math.Max(0, Math.Min(PageBytes, size - start));
                page = new byte[n];
                try { _volume.ReadRuns(_runs, start, page, 0, n); }
                catch (IOException)
                {
                    for (var i = 0; i < n; i++) page[i] = 0xFF; // unreadable: assume used, to stay on the safe side
                }
                if (_pages.Count > 64) _pages.Clear();
                _pages[index] = page;
                return page;
            }
        }
    }
}
