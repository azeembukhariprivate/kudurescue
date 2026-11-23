// A scan in progress: the files found, how far it is, pausing and stopping; the file systems' own scans and the deep
// scan add to it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kudurescue.Core
{
    /// <summary>Where a scan is up to.</summary>
    public enum ScanState { Running, Paused, Finished, Stopped, Failed }

    /// <summary>
    /// Quick scan: reads the drive's own index of files (the MFT on NTFS, the folders on FAT and exFAT) and lists the
    /// deleted files, with their names, original folders and how much of each is still on the drive.
    /// Nothing is written to the drive. The format-specific work is done by the volume; this keeps the progress and results.
    /// </summary>
    public sealed class QuickScan
    {
        /// <summary>The made-up folder for files whose folder is lost.</summary>
        public const string LostFolders = "Lost folders";

        /// <summary>Guards the list (added to from the scan's thread).</summary>
        private readonly object _lock = new object();
        /// <summary>The files found.</summary>
        private readonly List<FoundFile> _files = new List<FoundFile>();
        /// <summary>Files found since the window last took them.</summary>
        private readonly Queue<FoundFile> _recent = new Queue<FoundFile>();
        /// <summary>Stops the scan.</summary>
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        /// <summary>How long it has run (paused time left out).</summary>
        private readonly Stopwatch _clock = new Stopwatch();
        /// <summary>True while paused.</summary>
        private volatile bool _paused;
        /// <summary>How much is done, of how much.</summary>
        private long _done, _total;

        /// <summary>A scan of this drive.</summary>
        public QuickScan(IVolume volume, string drive)
        {
            Volume = volume;
            Drive = drive;
        }

        /// <summary>The drive read.</summary>
        public IVolume Volume { get; }
        /// <summary>The drive's letter or image's name.</summary>
        public string Drive { get; }
        /// <summary>Where it's up to.</summary>
        public ScanState State { get; private set; } = ScanState.Running;
        /// <summary>What went wrong, when it failed.</summary>
        public string Error { get; private set; }
        /// <summary>How much is done.</summary>
        public long Done => Interlocked.Read(ref _done);
        /// <summary>How much there is to do.</summary>
        public long Total => Interlocked.Read(ref _total);

        /// <summary>What <see cref="Done"/> and <see cref="Total"/> count, like "Index entries" or "Folders".</summary>
        public string ProgressLabel { get; set; } = "Index entries";

        /// <summary>True when <see cref="Done"/> and <see cref="Total"/> are bytes (Deep scan).</summary>
        public bool ProgressInBytes { get; set; }

        /// <summary>How long it has run.</summary>
        public TimeSpan Elapsed => _clock.Elapsed;
        /// <summary>How many places couldn't be read.</summary>
        public int Unreadable { get; set; }
        /// <summary>True once it's finished, stopped or failed.</summary>
        public bool IsDone => State == ScanState.Finished || State == ScanState.Stopped || State == ScanState.Failed;

        /// <summary>How far it is, 0 to 1.</summary>
        public double Fraction => Total == 0 ? 0 : Math.Min(1, (double)Done / Total);

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

        /// <summary>Runs the scan in the background.</summary>
        public Task RunAsync() => Task.Run(() => Run());

        /// <summary>Pauses the scan.</summary>
        public void Pause()
        {
            if (State != ScanState.Running) return;
            _paused = true;
            State = ScanState.Paused;
            _clock.Stop();
        }

        /// <summary>Goes on after a pause.</summary>
        public void Resume()
        {
            if (State != ScanState.Paused) return;
            _paused = false;
            State = ScanState.Running;
            _clock.Start();
        }

        /// <summary>Stops the scan.</summary>
        public void Stop()
        {
            _paused = false;
            _cancel.Cancel();
        }

        // ------------------------------------------------------------------ for the volumes

        /// <summary>Sets how much there is to do.</summary>
        public void SetTotal(long total) => Interlocked.Exchange(ref _total, total);
        /// <summary>Sets how much is done.</summary>
        public void SetDone(long done) => Interlocked.Exchange(ref _done, done);

        /// <summary>Adds a file found.</summary>
        public void Add(FoundFile file)
        {
            lock (_lock)
            {
                _files.Add(file);
                _recent.Enqueue(file);
                while (_recent.Count > 12) _recent.Dequeue();
            }
        }

        /// <summary>Waits while paused, and stops when the scan is stopped.</summary>
        public void Checkpoint(CancellationToken cancel)
        {
            while (_paused)
            {
                cancel.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }
            cancel.ThrowIfCancellationRequested();
        }

        /// <summary>Lets a volume change the files it found (e.g. fill in folders) while no one else reads them.</summary>
        public void Update(Action<List<FoundFile>> change)
        {
            lock (_lock) change(_files);
        }

        // ------------------------------------------------------------------ run

        /// <summary>Runs the scan on this thread.</summary>
        public void Run()
        {
            _clock.Start();
            try
            {
                Volume.Scan(this, _cancel.Token);
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
                    _files.Sort((a, b) =>
                    {
                        var c = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    });
                }
                _clock.Stop();
            }
        }

        /// <summary>Runs a whole scan and returns the files found. Used by the tests and the self-test.</summary>
        public static List<FoundFile> RunToEnd(IVolume volume, string drive = "")
        {
            var scan = new QuickScan(volume, drive);
            scan.Run();
            if (scan.State == ScanState.Failed) throw new IOException(scan.Error);
            return scan.Files;
        }

        /// <summary>Compares a deleted file's clusters with the drive's map of used clusters, and sets its chance.</summary>
        public static void SetChance(FoundFile f, int clusterSize, Func<long, long, long> countUsed)
        {
            var needed = (f.Size + clusterSize - 1) / clusterSize;
            long used = 0, taken = 0;
            foreach (var run in f.Runs ?? new List<DataRun>())
            {
                if (run.IsSparse || run.Vcn >= needed) continue;
                var length = Math.Min(run.Length, needed - run.Vcn);
                used += length;
                taken += countUsed(run.Lcn, length);
            }
            if (used == 0 || taken == 0)
            {
                f.GoodPercent = 100;
                if (f.Chance != Chance.Unsure) f.Chance = Chance.Excellent;
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
                f.Damaged = DamagedParts(f, clusterSize, needed, countUsed);
            }
        }

        /// <summary>Where in a partly whole file other files' data is now: halving each run until the used pieces are found.</summary>
        public static List<long> DamagedParts(FoundFile f, int clusterSize, long needed, Func<long, long, long> countUsed)
        {
            var ranges = new List<(long Start, long Length)>();
            void Look(long vcn, long lcn, long length)
            {
                if (ranges.Count > 4000) return; // a file this broken is cut at its first damage anyway
                var taken = countUsed(lcn, length);
                if (taken == 0) return;
                if (taken >= length || length == 1)
                {
                    ranges.Add((vcn * clusterSize, length * clusterSize));
                    return;
                }
                var half = length / 2;
                Look(vcn, lcn, half);
                Look(vcn + half, lcn + half, length - half);
            }
            foreach (var run in f.Runs ?? new List<DataRun>())
            {
                if (run.IsSparse || run.Vcn >= needed) continue;
                Look(run.Vcn, run.Lcn, Math.Min(run.Length, needed - run.Vcn));
            }
            // Next to each other: one piece.
            var merged = new List<long>();
            foreach (var r in ranges.OrderBy(r => r.Start))
            {
                var n = merged.Count;
                if (n >= 2 && merged[n - 2] + merged[n - 1] == r.Start) merged[n - 1] += r.Length;
                else { merged.Add(r.Start); merged.Add(r.Length); }
            }
            return merged;
        }
    }
}
