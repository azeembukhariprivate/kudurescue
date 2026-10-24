using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>A Windows "Previous Versions" snapshot (Volume Shadow Copy) of a drive.</summary>
    public sealed class Snapshot
    {
        public string Drive { get; set; }        // "C:"
        public string DevicePath { get; set; }   // \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3
        public DateTime Created { get; set; }
        public string Id { get; set; }

        public string Root => DevicePath.TrimEnd('\\') + "\\";
        public string Title => Created.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm");
    }

    /// <summary>
    /// Compares a snapshot with the drive as it is now: files that were in the snapshot but are gone now (deleted since),
    /// and older versions of files that have changed since. Both are copied straight from the snapshot, so they come back perfectly.
    /// </summary>
    public sealed class SnapshotVolume : IVolume
    {
        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "$Recycle.Bin",
            "Recovery", "$WINDOWS.~BT", "$Windows.~WS", "$WinREAgent", "AppData", "PerfLogs", "Config.Msi", "MSOCache"
        };

        public SnapshotVolume(string snapshotRoot, string liveRoot, DateTime taken)
        {
            SnapshotRoot = snapshotRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            LiveRoot = liveRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            Taken = taken;
        }

        public string SnapshotRoot { get; }
        public string LiveRoot { get; }
        public DateTime Taken { get; }

        // Files are copied from the snapshot, never read from the drive directly.
        public IBlockDevice Device => null;
        public string FormatName => "Previous Versions";
        public int ClusterSize => 4096;
        public long ClusterOffset(long cluster) => cluster * ClusterSize;

        public void Scan(QuickScan scan, CancellationToken cancel)
        {
            scan.ProgressLabel = "Folders";
            var queue = new Queue<string>();
            queue.Enqueue(string.Empty);
            long done = 0, total = 1;
            scan.SetTotal(total);
            while (queue.Count > 0)
            {
                scan.Checkpoint(cancel);
                var relative = queue.Dequeue();
                var folder = Path.Combine(SnapshotRoot, relative);
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(folder))
                    {
                        var name = Path.GetFileName(dir);
                        if (Skipped.Contains(name)) continue;
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue; // links back into the drive
                        queue.Enqueue(Join(relative, name));
                        total++;
                    }
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        var found = Compare(file, Join(relative, Path.GetFileName(file)), relative);
                        if (found != null) scan.Add(found);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { scan.Unreadable++; }
                scan.SetDone(++done);
                scan.SetTotal(total);
            }
        }

        private static string Join(string a, string b) => a.Length == 0 ? b : a + Path.DirectorySeparatorChar + b;

        private FoundFile Compare(string snapshotFile, string relative, string folder)
        {
            var old = new FileInfo(snapshotFile);
            var live = new FileInfo(Path.Combine(LiveRoot, relative));
            string note;
            if (!live.Exists) note = $"Deleted since this snapshot ({Taken.ToLocalTime():d MMM yyyy, HH:mm}). Copied from the snapshot, so it comes back perfectly.";
            else if (live.Length != old.Length || Math.Abs((live.LastWriteTimeUtc - old.LastWriteTimeUtc).TotalSeconds) > 2)
                note = $"An older version: the file still exists but has changed since this snapshot ({Taken.ToLocalTime():d MMM yyyy, HH:mm}).";
            else return null;

            return new FoundFile
            {
                Source = FoundSource.Snapshot,
                Name = old.Name,
                Folder = folder.Replace(Path.DirectorySeparatorChar, '\\'),
                Size = old.Length,
                Created = old.CreationTimeUtc,
                Modified = old.LastWriteTimeUtc,
                Kind = FileKinds.Of(old.Name),
                RecyclePath = snapshotFile,
                Chance = Chance.Excellent,
                Note = note
            };
        }

        public void Dispose() { }
    }
}
