using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>
    /// The NTFS part of a Quick scan: reads every record of the file index (MFT), lists the deleted files, and works out
    /// their folders at the end (also when stopped early), when every folder record has been seen.
    /// </summary>
    internal sealed class NtfsScanner
    {
        public const long RootRecord = 5;
        private const int FirstUserRecord = 24;

        private readonly NtfsVolume _volume;
        private readonly QuickScan _scan;
        private readonly Dictionary<long, DirEntry> _dirs = new Dictionary<long, DirEntry>();
        private readonly Dictionary<long, string> _folderCache = new Dictionary<long, string>();
        private readonly Dictionary<FoundFile, (long Parent, int Sequence)> _parents = new Dictionary<FoundFile, (long, int)>();

        private sealed class DirEntry
        {
            public string Name;
            public long Parent;
            public int ParentSequence;
            public int Sequence;
            public bool InUse;
        }

        public NtfsScanner(NtfsVolume volume, QuickScan scan)
        {
            _volume = volume;
            _scan = scan;
        }

        public void Run(CancellationToken cancel)
        {
            _scan.ProgressLabel = "Index entries";
            try
            {
                ReadIndex(cancel);
            }
            finally
            {
                _scan.Update(files =>
                {
                    _folderCache.Clear();
                    foreach (var pair in _parents) pair.Key.Folder = Resolve(pair.Value.Parent, pair.Value.Sequence);
                });
            }
        }

        private void ReadIndex(CancellationToken cancel)
        {
            var size = _volume.RecordSize;
            var total = _volume.RecordCount;
            _scan.SetTotal(total);
            var perBatch = Math.Max(1, (1 << 20) / size);
            var buffer = new byte[perBatch * size];

            for (long first = 0; first < total; first += perBatch)
            {
                _scan.Checkpoint(cancel);
                var count = (int)Math.Min(perBatch, total - first);
                var readable = new bool[count];
                try
                {
                    _volume.ReadRuns(_volume.MftRuns, first * size, buffer, 0, count * size);
                    for (var i = 0; i < count; i++) readable[i] = true;
                }
                catch (IOException)
                {
                    // A bad area in the index: read record by record and skip the ones that fail.
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            _volume.ReadRuns(_volume.MftRuns, (first + i) * size, buffer, i * size, size);
                            readable[i] = true;
                        }
                        catch (IOException) { _scan.Unreadable++; }
                    }
                }

                for (var i = 0; i < count; i++)
                {
                    if (!readable[i]) continue;
                    FileRecord record;
                    try { record = FileRecord.Parse(buffer, i * size, size, first + i); }
                    catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException) { continue; }
                    if (record != null) Consider(record);
                }
                _scan.SetDone(first + count);
            }
        }

        private void Consider(FileRecord record)
        {
            if (record.IsExtension) return;
            var name = record.FileName;
            if (name == null) return;

            // A folder has a directory index; look for it too, in case the folder flag was cleared when it was deleted.
            if (record.IsDirectory || record.Find(AttrType.IndexRoot, "$I30") != null)
            {
                lock (_dirs)
                {
                    _dirs[record.Number] = new DirEntry
                    {
                        Name = name.Name, Parent = name.ParentRecord, ParentSequence = name.ParentSequence,
                        Sequence = record.Sequence, InUse = record.InUse
                    };
                }
                return;
            }
            if (record.InUse || record.Number < FirstUserRecord) return;

            var file = Describe(record, name);
            if (file == null) return;
            lock (_dirs)
            {
                _parents[file] = (name.ParentRecord, name.ParentSequence);
                file.Folder = Resolve(name.ParentRecord, name.ParentSequence); // provisional: more folders may come later
            }
            _scan.Add(file);
        }

        private FoundFile Describe(FileRecord record, FileNameInfo name)
        {
            NtfsAttribute data;
            try { data = _volume.DataAttribute(record, string.Empty); }
            catch (InvalidDataException) { return null; }
            if (data == null) return null;

            record.StandardTimes(out var created, out var modified);
            var f = new FoundFile
            {
                Source = FoundSource.Index,
                Record = record.Number,
                Name = name.Name,
                Kind = FileKinds.Of(name.Name),
                Created = created != DateTime.MinValue ? created : name.Created,
                Modified = modified != DateTime.MinValue ? modified : name.Modified
            };

            var si = record.Find(AttrType.StandardInformation);
            var encrypted = data.Encrypted || (si?.Value != null && si.Value.Length >= 36 && (BitConverter.ToUInt32(si.Value, 32) & 0x4000) != 0);

            if (!data.NonResident)
            {
                f.ResidentData = data.Value;
                f.Size = data.Value.Length;
                f.Chance = encrypted ? Chance.Unrecoverable : Chance.Excellent;
                return f;
            }

            if (data.DataSize < 0 || data.DataSize > _volume.Device.Length) return null;
            f.Size = data.DataSize;
            f.InitializedSize = Math.Min(data.InitializedSize, data.DataSize);
            f.Runs = data.Runs;
            f.Compressed = data.Compressed && data.CompressionUnit > 0;
            f.CompressionUnit = data.CompressionUnit;

            if (encrypted)
            {
                f.Chance = Chance.Unrecoverable;
                f.GoodPercent = 0;
                return f;
            }
            QuickScan.SetChance(f, _volume.ClusterSize, _volume.Bitmap.CountAllocated);
            return f;
        }

        /// <summary>
        /// The folder a deleted file was in, following parent folders up to the root.
        /// When a parent's record has been reused by something else, the file goes under "Lost folders".
        /// </summary>
        private string Resolve(long parent, int sequence)
        {
            var key = (parent << 16) ^ sequence;
            if (_folderCache.TryGetValue(key, out var cached)) return cached;

            var parts = new List<string>();
            var lost = false;
            var current = parent;
            var seq = sequence;
            for (var depth = 0; ; depth++)
            {
                if (current == RootRecord) break;
                if (depth > 64 || !_dirs.TryGetValue(current, out var d) || !SequenceMatches(d, seq))
                {
                    lost = true;
                    break;
                }
                parts.Add(d.Name);
                current = d.Parent;
                seq = d.ParentSequence;
            }
            parts.Reverse();
            if (lost) parts.Insert(0, QuickScan.LostFolders);
            var folder = string.Join("\\", parts.Select(p => p.Replace('\\', '_').Replace('/', '_')));
            _folderCache[key] = folder;
            return folder;
        }

        // Windows changes a record's sequence number when it's freed, so a deleted folder may be one ahead.
        private static bool SequenceMatches(DirEntry d, int seq) =>
            seq == 0 || d.Sequence == seq || (!d.InUse && d.Sequence == seq + 1);
    }
}
