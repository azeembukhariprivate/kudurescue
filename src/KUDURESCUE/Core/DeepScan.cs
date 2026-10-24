using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>A volume that can say which parts of the drive are free (where deleted files are).</summary>
    public interface IFreeSpace
    {
        /// <summary>Free areas as (byte offset, byte length), in order, each starting on a cluster.</summary>
        IEnumerable<(long Start, long Length)> FreeRanges();
    }

    /// <summary>Areas of a drive, sorted, for asking how much of a stretch is free.</summary>
    public sealed class FreeMap
    {
        private readonly List<(long Start, long End)> _ranges;

        public FreeMap(IEnumerable<(long Start, long Length)> ranges)
        {
            _ranges = ranges.Where(r => r.Length > 0).Select(r => (r.Start, r.Start + r.Length)).ToList();
            Bytes = _ranges.Sum(r => r.End - r.Start);
        }

        public static FreeMap Everything(long length) => new FreeMap(new[] { (0L, length) });

        public long Bytes { get; }
        public IReadOnlyList<(long Start, long End)> Ranges => _ranges;

        /// <summary>How many bytes of [start, start + length) are NOT free.</summary>
        public long UsedBytes(long start, long length)
        {
            var end = start + length;
            long free = 0;
            int lo = 0, hi = _ranges.Count - 1, first = _ranges.Count;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (_ranges[mid].End > start) { first = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            for (var i = first; i < _ranges.Count && _ranges[i].Start < end; i++)
                free += Math.Min(end, _ranges[i].End) - Math.Max(start, _ranges[i].Start);
            return length - free;
        }
    }

    /// <summary>
    /// Deep scan: looks for files by their contents, block by block, in the drive's free space (or the whole drive when its
    /// format can't be read, e.g. after formatting or damage). Names and folders aren't known, so files are named by type.
    /// Positions are in 512-byte units, so recovery works the same way as for the other formats.
    /// </summary>
    public sealed class DeepVolume : IVolume
    {
        public const int Unit = 512;
        private readonly IVolume _inner;
        private readonly bool _ownsDevice;
        private FreeMap _free;

        public DeepVolume(IBlockDevice device, IVolume inner, bool ownsDevice = true)
        {
            Device = device;
            _inner = inner;
            _ownsDevice = ownsDevice;
        }

        public IBlockDevice Device { get; }
        public IVolume Inner => _inner;
        public string FormatName => _inner == null ? "the whole drive" : "free space on " + _inner.FormatName;
        public int ClusterSize => Unit;
        public long ClusterOffset(long cluster) => cluster * Unit;

        /// <summary>The free areas to search (computed once).</summary>
        public FreeMap Free
        {
            get
            {
                if (_free != null) return _free;
                FreeMap map = null;
                if (_inner is IFreeSpace fs)
                {
                    try { map = new FreeMap(fs.FreeRanges()); }
                    catch (IOException) { map = null; }
                }
                return _free = map ?? FreeMap.Everything(Device.Length);
            }
        }

        /// <summary>How far apart files can start: a cluster when the format is known, otherwise a sector.</summary>
        public int Step => _inner == null ? Math.Max(Unit, Device.SectorSize) : Math.Max(Unit, _inner.ClusterSize);

        public void Scan(QuickScan scan, CancellationToken cancel)
        {
            scan.ProgressLabel = "Searched";
            scan.ProgressInBytes = true;
            var free = Free;
            scan.SetTotal(free.Bytes);
            var source = new ByteSource(Device);
            var counters = new Dictionary<FileKind, int>();
            const int chunk = 4 * 1024 * 1024;
            var buffer = new byte[chunk];
            long done = 0;
            long skipUntil = -1;
            (long Start, Carved Found)? open = null; // a file whose end is found when the next file starts

            void Close(long end)
            {
                if (open == null) return;
                var (start, carved) = open.Value;
                open = null;
                var length = Math.Min(end - start, carved.MaxLength);
                if (length > 0) Add(scan, free, counters, start, length, carved.Extension, exact: false);
            }

            foreach (var (rangeStart, rangeEnd) in free.Ranges)
            {
                var step = Step;
                var pos = rangeStart;
                while (pos < rangeEnd)
                {
                    scan.Checkpoint(cancel);
                    var n = (int)Math.Min(chunk, rangeEnd - pos);
                    SafeRead.Read(Device, pos, buffer, 0, n);
                    for (var off = 0; off < n; off += step)
                    {
                        var at = pos + off;
                        if (at < skipUntil) continue;
                        if (off + 16 <= n ? !MightStart(buffer, off) : !MightStart(source.Read(at, 16), 0)) continue;
                        var head = off + 64 <= n ? Slice(buffer, off, 64) : source.Read(at, 64);
                        var carved = Carvers.Detect(source, at, head);
                        if (carved == null) continue;
                        Close(at);
                        if (carved.Exact)
                        {
                            Add(scan, free, counters, at, carved.Length, carved.Extension, exact: true);
                            skipUntil = at + carved.Length; // don't list what's inside it (thumbnails, embedded files)
                        }
                        else open = (at, carved);
                    }
                    pos += n;
                    done += n;
                    scan.SetDone(done);
                }
                Close(rangeEnd);
            }
        }

        private static byte[] Slice(byte[] b, int offset, int count)
        {
            var r = new byte[count];
            Buffer.BlockCopy(b, offset, r, 0, count);
            return r;
        }

        /// <summary>A cheap first check, so most blocks are passed over without a closer look.</summary>
        private static bool MightStart(byte[] h, int o)
        {
            if (h.Length < o + 16) return false;
            switch (h[o])
            {
                case 0xFF: case 0x89: case (byte)'G': case (byte)'B': case (byte)'I': case (byte)'M': case (byte)'%': case (byte)'P':
                case 0xD0: case (byte)'{': case (byte)'R': case 0x1A: case (byte)'O': case (byte)'f': case (byte)'7': case 0x1F:
                case (byte)'S': case (byte)'8': case (byte)'A': case (byte)'!': case (byte)'<':
                    return true;
                case 0x00:
                    return (h[o + 1] == 0x01 && h[o + 2] == 0 && h[o + 3] == 0) || (h[o + 4] == 'f' && h[o + 5] == 't' && h[o + 6] == 'y' && h[o + 7] == 'p');
                default:
                    return h[o + 4] == 'f' && h[o + 5] == 't' && h[o + 6] == 'y' && h[o + 7] == 'p';
            }
        }

        private static void Add(QuickScan scan, FreeMap free, Dictionary<FileKind, int> counters, long start, long length, string ext, bool exact)
        {
            var kind = FileKinds.Of("x" + ext);
            counters.TryGetValue(kind, out var n);
            counters[kind] = ++n;
            var f = new FoundFile
            {
                Source = FoundSource.Carved,
                Name = $"{Singular(kind)} {n:0000}{ext}",
                Folder = FileKinds.Label(kind) + "\\" + ext.TrimStart('.').ToUpperInvariant(),
                Size = length,
                InitializedSize = length,
                Kind = kind,
                Offset = start,
                Runs = new List<DataRun> { new DataRun(0, start / Unit, (length + Unit - 1) / Unit) },
                Chance = exact ? Chance.Excellent : Chance.Unsure,
                Note = exact
                    ? "Found by its contents; its name, folder and dates weren't kept."
                    : "Found by its contents; where it ends had to be guessed, so it may include extra data at the end."
            };
            QuickScan.SetChance(f, Unit, (lcn, count) => (free.UsedBytes(lcn * Unit, count * Unit) + Unit - 1) / Unit);
            scan.Add(f);
        }

        private static string Singular(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Photos: return "Photo";
                case FileKind.Videos: return "Video";
                case FileKind.Documents: return "Document";
                case FileKind.Music: return "Audio";
                case FileKind.Archives: return "Archive";
                case FileKind.Email: return "Email";
                case FileKind.Design: return "Design";
                case FileKind.Data: return "Data";
                case FileKind.Programs: return "Program";
                default: return "File";
            }
        }

        public void Dispose()
        {
            _inner?.Dispose();
            if (_ownsDevice) Device.Dispose();
        }
    }
}
