using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kudurescue.Core
{
    public enum ScanState { Running, Paused, Finished, Stopped, Failed }

    /// <summary>
    /// Quick scan: reads every record of the drive's file index (MFT) and lists the deleted files, with their names,
    /// original folders and how much of each is still on the drive. Nothing is written to the drive.
    /// </summary>
    public sealed class QuickScan
    {
        public const long RootRecord = 5;
        private const int FirstUserRecord = 24;

        private readonly object _lock = new object();
        private readonly List<FoundFile> _files = new List<FoundFile>();
        private readonly Queue<FoundFile> _recent = new Queue<FoundFile>();
        private readonly Dictionary<long, DirEntry> _dirs = new Dictionary<long, DirEntry>();
        private readonly Dictionary<long, string> _folderCache = new Dictionary<long, string>();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly Stopwatch _clock = new Stopwatch();
        private volatile bool _paused;
        private long _done, _total;

        private sealed class DirEntry
        {
            public string Name;
            public long Parent;
            public int ParentSequence;
            public int Sequence;
            public bool InUse;
        }

        public QuickScan(NtfsVolume volume, string drive)
        {
            Volume = volume;
            Drive = drive;
        }

        public NtfsVolume Volume { get; }
        public string Drive { get; }
        public ScanState State { get; private set; } = ScanState.Running;
        public string Error { get; private set; }
        public long RecordsDone => Interlocked.Read(ref _done);
        public long RecordsTotal => Interlocked.Read(ref _total);
        public TimeSpan Elapsed => _clock.Elapsed;
        public int UnreadableRecords { get; private set; }
        public bool IsDone => State == ScanState.Finished || State == ScanState.Stopped || State == ScanState.Failed;

        public double Fraction => RecordsTotal == 0 ? 0 : Math.Min(1, (double)RecordsDone / RecordsTotal);

        public TimeSpan? Remaining
        {
            get
            {
                var f = Fraction;
                if (f < 0.02 || IsDone) return null;
                return TimeSpan.FromSeconds(Elapsed.TotalSeconds * (1 - f) / f);
            }
        }

        /// <summary>A copy of the files found so far.</summary>
        public List<FoundFile> Files
        {
            get { lock (_lock) return _files.ToList(); }
        }

        public List<FoundFile> Recent
        {
            get { lock (_lock) return _recent.Reverse().ToList(); }
        }

        public Task RunAsync() => Task.Run(() => Run());

        public void Pause()
        {
            if (State != ScanState.Running) return;
            _paused = true;
            State = ScanState.Paused;
            _clock.Stop();
        }

        public void Resume()
        {
            if (State != ScanState.Paused) return;
            _paused = false;
            State = ScanState.Running;
            _clock.Start();
        }

        public void Stop()
        {
            _paused = false;
            _cancel.Cancel();
        }

        /// <summary>Runs the scan on this thread. Folders are worked out at the end, also when stopped early.</summary>
        public void Run()
        {
            _clock.Start();
            try
            {
                ReadIndex(_cancel.Token);
                State = ScanState.Finished;
            }
            catch (OperationCanceledException)
            {
                State = ScanState.Stopped;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Error = ex.Message;
                State = ScanState.Failed;
            }
            finally
            {
                lock (_lock)
                {
                    _folderCache.Clear();
                    foreach (var f in _files) f.Folder = FolderOf(f.Record, f.Folder);
                    _files.Sort((a, b) =>
                    {
                        var c = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    });
                }
                _clock.Stop();
            }
        }

        private readonly Dictionary<long, (long Parent, int Sequence)> _parents = new Dictionary<long, (long, int)>();

        private void ReadIndex(CancellationToken cancel)
        {
            var size = Volume.RecordSize;
            var total = Volume.RecordCount;
            Interlocked.Exchange(ref _total, total);
            var perBatch = Math.Max(1, (1 << 20) / size);
            var buffer = new byte[perBatch * size];

            for (long first = 0; first < total; first += perBatch)
            {
                while (_paused) { cancel.ThrowIfCancellationRequested(); Thread.Sleep(100); }
                cancel.ThrowIfCancellationRequested();

                var count = (int)Math.Min(perBatch, total - first);
                var readable = new bool[count];
                try
                {
                    Volume.ReadRuns(Volume.MftRuns, first * size, buffer, 0, count * size);
                    for (var i = 0; i < count; i++) readable[i] = true;
                }
                catch (IOException)
                {
                    // A bad area in the index: read record by record and skip the ones that fail.
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            Volume.ReadRuns(Volume.MftRuns, (first + i) * size, buffer, i * size, size);
                            readable[i] = true;
                        }
                        catch (IOException) { UnreadableRecords++; }
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
                Interlocked.Exchange(ref _done, first + count);
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
                lock (_lock)
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
            lock (_lock)
            {
                _parents[record.Number] = (name.ParentRecord, name.ParentSequence);
                file.Folder = FolderOf(record.Number, string.Empty);
                _files.Add(file);
                _recent.Enqueue(file);
                while (_recent.Count > 12) _recent.Dequeue();
            }
        }

        private FoundFile Describe(FileRecord record, FileNameInfo name)
        {
            NtfsAttribute data;
            try { data = Volume.DataAttribute(record, string.Empty); }
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

            if (data.DataSize < 0 || data.DataSize > Volume.Device.Length) return null;
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

            // Compare the clusters the file used with the drive's map of used clusters.
            var needed = (f.Size + Volume.ClusterSize - 1) / Volume.ClusterSize;
            long used = 0, taken = 0;
            foreach (var run in f.Runs)
            {
                if (run.IsSparse || run.Vcn >= needed) continue;
                var length = Math.Min(run.Length, needed - run.Vcn);
                used += length;
                taken += Volume.Bitmap.CountAllocated(run.Lcn, length);
            }
            if (used == 0 || taken == 0)
            {
                f.Chance = Chance.Excellent;
            }
            else if (taken >= used)
            {
                f.Chance = Chance.Overwritten;
                f.GoodPercent = 0;
            }
            else
            {
                f.Chance = Chance.Partial;
                f.GoodPercent = (int)Math.Max(1, Math.Min(99, (used - taken) * 100 / used));
            }
            return f;
        }

        /// <summary>
        /// The folder a deleted file was in, following parent folders up to the root.
        /// When a parent's record has been reused by something else, the file goes under "Lost folders".
        /// </summary>
        private string FolderOf(long record, string fallback)
        {
            if (!_parents.TryGetValue(record, out var p)) return fallback;
            return Resolve(p.Parent, p.Sequence);
        }

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
            if (lost) parts.Insert(0, LostFolders);
            var folder = string.Join("\\", parts.Select(Clean));
            _folderCache[key] = folder;
            return folder;
        }

        public const string LostFolders = "Lost folders";

        // Windows changes a record's sequence number when it's freed, so a deleted folder may be one ahead.
        private static bool SequenceMatches(DirEntry d, int seq) =>
            seq == 0 || d.Sequence == seq || (!d.InUse && d.Sequence == seq + 1);

        private static string Clean(string part) => part.Replace('\\', '_').Replace('/', '_');

        /// <summary>Runs a whole scan and returns the files found. Used by the tests and the self-test.</summary>
        public static List<FoundFile> RunToEnd(NtfsVolume volume, string drive = "")
        {
            var scan = new QuickScan(volume, drive);
            scan.Run();
            if (scan.State == ScanState.Failed) throw new IOException(scan.Error);
            return scan.Files;
        }
    }
}
