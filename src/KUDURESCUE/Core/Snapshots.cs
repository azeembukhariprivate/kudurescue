// A Previous Version as something to scan: files that are in it but deleted or changed now.
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
        /// <summary>The drive, like "C:".</summary>
        public string Drive { get; set; }        // "C:"
        /// <summary>Where Windows keeps it.</summary>
        public string DevicePath { get; set; }   // \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3
        /// <summary>When it was made.</summary>
        public DateTime Created { get; set; }
        /// <summary>Its id.</summary>
        public string Id { get; set; }

        /// <summary>Its top folder.</summary>
        public string Root => DevicePath.TrimEnd('\\') + "\\";
        /// <summary>When it was made, in words.</summary>
        public string Title => Created.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm");
    }

    /// <summary>
    /// Compares a snapshot with the drive as it is now: files that were in the snapshot but are gone now (deleted since),
    /// and older versions of files that have changed since. Both are copied straight from the snapshot, so they come back perfectly.
    /// </summary>
    public sealed class SnapshotVolume : IVolume
    {
        /// <summary>Folders left out (Windows and programs' own).</summary>
        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "$Recycle.Bin",
            "Recovery", "$WINDOWS.~BT", "$Windows.~WS", "$WinREAgent", "AppData", "PerfLogs", "Config.Msi", "MSOCache"
        };

        /// <summary>A Previous Version compared with the drive now.</summary>
        public SnapshotVolume(string snapshotRoot, string liveRoot, DateTime taken)
        {
            SnapshotRoot = snapshotRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            LiveRoot = liveRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            Taken = taken;
        }

        /// <summary>The Previous Version's top folder.</summary>
        public string SnapshotRoot { get; }
        /// <summary>The drive's top folder now.</summary>
        public string LiveRoot { get; }
        /// <summary>When it was made.</summary>
        public DateTime Taken { get; }

        // Files are copied from the snapshot, never read from the drive directly.
        public IBlockDevice Device => null;
        /// <summary>The kind's name.</summary>
        public string FormatName => "Previous Versions";
        /// <summary>Not used (files are read whole).</summary>
        public int ClusterSize => 4096;
        /// <summary>Not used.</summary>
        public long ClusterOffset(long cluster) => cluster * ClusterSize;

        /// <summary>Lists the files that are deleted or changed since.</summary>
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

        /// <summary>A folder and a name joined.</summary>
        private static string Join(string a, string b) => a.Length == 0 ? b : a + Path.DirectorySeparatorChar + b;

        /// <summary>A file in the Previous Version, when it's deleted or different now (null when the same).</summary>
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

        /// <summary>Nothing to close.</summary>
        public void Dispose() { }
    }
}
