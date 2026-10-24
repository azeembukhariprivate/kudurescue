using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudurescue.Core
{
    public sealed class RecoverOptions
    {
        public string Destination { get; set; }
        public bool KeepFolders { get; set; } = true;
        public bool RecoverPartial { get; set; } = true;
    }

    public sealed class RecoverProblem
    {
        public string File { get; set; }
        public string Reason { get; set; }
    }

    public sealed class RecoverResult
    {
        public int Recovered;
        public int Partial;
        public int Skipped;
        public long Bytes;
        public int BadSectors;
        public readonly List<RecoverProblem> Problems = new List<RecoverProblem>();
        public bool Cancelled;
    }

    /// <summary>Writes deleted files back out, to a folder on another drive.</summary>
    public static class Recovery
    {
        /// <summary>Recovers files one after another. <paramref name="progress"/> gets the file being recovered and the bytes done so far.</summary>
        public static RecoverResult Run(IVolume volume, IList<FoundFile> files, RecoverOptions options,
            Action<FoundFile, long> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            var result = new RecoverResult();
            Directory.CreateDirectory(options.Destination);
            long bytes = 0;
            foreach (var f in files)
            {
                if (cancel.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }
                if (f.Chance == Chance.Unrecoverable || f.Chance == Chance.Overwritten || (f.Chance == Chance.Partial && !options.RecoverPartial))
                {
                    result.Skipped++;
                    continue;
                }
                progress?.Invoke(f, bytes);
                var target = TargetPath(f, options);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (f.IsCopy)
                    {
                        CopyFromRecycleBin(f, target);
                    }
                    else
                    {
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            var start = bytes;
                            result.BadSectors += Write(volume, f, output, done => progress?.Invoke(f, start + done), cancel);
                        }
                        SetTimes(target, f);
                    }
                    bytes += f.Size;
                    result.Bytes += f.Size;
                    result.Recovered++;
                    if (f.Chance == Chance.Partial) result.Partial++;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(target);
                    result.Cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    TryDelete(target);
                    result.Problems.Add(new RecoverProblem { File = f.RelativePath, Reason = ex.Message });
                }
            }
            return result;
        }

        /// <summary>Where a file will be saved: keeping its folders (or not), with a free name, marked when only partly there.</summary>
        public static string TargetPath(FoundFile f, RecoverOptions options)
        {
            var folder = options.Destination;
            if (options.KeepFolders && f.Folder.Length > 0)
                folder = f.Folder.Split('\\').Where(p => p.Length > 0).Aggregate(folder, (acc, part) => Path.Combine(acc, SafeName(part)));
            var name = SafeName(f.Name);
            if (f.Chance == Chance.Partial) name = Path.GetFileNameWithoutExtension(name) + " (PARTIAL)" + Path.GetExtension(name);
            return FreeName(Path.Combine(folder, name));
        }

        /// <summary>Makes a name safe for Windows: no reserved characters or device names, no trailing dots or spaces.</summary>
        public static string SafeName(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));
            var chars = (name ?? string.Empty).Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray();
            var safe = new string(chars).TrimEnd('.', ' ');
            if (safe.Length == 0) safe = "_";
            var stem = safe.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                safe = "_" + safe;
            return safe.Length > 200 ? safe.Substring(0, 200 - Path.GetExtension(safe).Length) + Path.GetExtension(safe) : safe;
        }

        /// <summary>"photo.jpg", or "photo (2).jpg" if that's taken, and so on.</summary>
        public static string FreeName(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 2; ; i++)
            {
                var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>Reads the start of a file, for previews. Returns null when there's nothing to show.</summary>
        public static byte[] ReadStart(IVolume volume, FoundFile f, int max)
        {
            if (f.Chance == Chance.Unrecoverable) return null;
            try
            {
                if (f.IsCopy)
                {
                    if (f.IsFolder || !File.Exists(f.RecyclePath)) return null;
                    using (var s = new FileStream(f.RecyclePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var buffer = new byte[(int)Math.Min(max, s.Length)];
                        var n = s.Read(buffer, 0, buffer.Length);
                        return n == buffer.Length ? buffer : buffer.Take(n).ToArray();
                    }
                }
                if (volume == null) return null;
                using (var ms = new MemoryStream())
                {
                    Write(volume, f, ms, null, CancellationToken.None, max);
                    return ms.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>Writes a file's data from the drive into <paramref name="output"/>. Returns the number of unreadable sectors (written as zeros).</summary>
        public static int Write(IVolume volume, FoundFile f, Stream output, Action<long> progress, CancellationToken cancel, long limit = long.MaxValue)
        {
            var total = Math.Min(f.Size, limit);
            if (f.ResidentData != null)
            {
                output.Write(f.ResidentData, 0, (int)Math.Min(total, f.ResidentData.Length));
                progress?.Invoke(total);
                return 0;
            }
            return f.Compressed ? WriteCompressed(volume, f, output, total, progress, cancel) : WritePlain(volume, f, output, total, progress, cancel);
        }

        private static int WritePlain(IVolume volume, FoundFile f, Stream output, long total, Action<long> progress, CancellationToken cancel)
        {
            var cluster = volume.ClusterSize;
            var buffer = new byte[1 << 20];
            var initialized = f.InitializedSize > 0 ? f.InitializedSize : f.Size;
            long written = 0;
            var bad = 0;
            foreach (var run in f.Runs)
            {
                if (written >= total) break;
                var runBytes = Math.Min(run.Length * cluster, total - written);
                long done = 0;
                while (done < runBytes)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(buffer.Length, runBytes - done);
                    var position = written + done;
                    if (run.IsSparse || position >= initialized)
                    {
                        Array.Clear(buffer, 0, n);
                    }
                    else
                    {
                        var offset = volume.ClusterOffset(run.Lcn) + done;
                        if (offset + n > volume.Device.Length) throw new InvalidDataException("Part of the file lies past the end of the drive.");
                        bad += SafeRead.Read(volume.Device, offset, buffer, 0, n);
                        if (position + n > initialized) Array.Clear(buffer, (int)(initialized - position), (int)(position + n - initialized));
                    }
                    output.Write(buffer, 0, n);
                    done += n;
                    progress?.Invoke(written + done);
                }
                written += runBytes;
            }
            if (written < total) throw new InvalidDataException("The file's map of where its data is stored is incomplete.");
            return bad;
        }

        private static int WriteCompressed(IVolume volume, FoundFile f, Stream output, long total, Action<long> progress, CancellationToken cancel)
        {
            var cluster = volume.ClusterSize;
            var unitClusters = 1L << f.CompressionUnit;
            var unitBytes = (int)(unitClusters * cluster);
            var raw = new byte[unitBytes];
            long written = 0;
            var bad = 0;
            for (long unit = 0; written < total; unit++)
            {
                cancel.ThrowIfCancellationRequested();
                var startVcn = unit * unitClusters;
                var pieces = Pieces(f.Runs, startVcn, unitClusters);
                if (pieces.Sum(p => p.Length) < unitClusters) throw new InvalidDataException("The file's map of where its data is stored is incomplete.");

                var allocated = pieces.Where(p => p.Lcn >= 0).Sum(p => p.Length);
                byte[] data;
                if (allocated == 0)
                {
                    data = new byte[unitBytes]; // all zeros
                }
                else
                {
                    var pos = 0;
                    foreach (var p in pieces.Where(p => p.Lcn >= 0))
                    {
                        var n = (int)(p.Length * cluster);
                        bad += SafeRead.Read(volume.Device, volume.ClusterOffset(p.Lcn), raw, pos, n);
                        pos += n;
                    }
                    // A unit that fills all its clusters is stored as-is; a shorter one is compressed.
                    data = allocated == unitClusters ? raw : Lznt1.Decompress(raw, pos, unitBytes);
                }
                var count = (int)Math.Min(unitBytes, total - written);
                output.Write(data, 0, count);
                written += count;
                progress?.Invoke(written);
            }
            return bad;
        }

        /// <summary>The runs covering clusters [vcn, vcn + count), cut to that range. Sparse pieces have Lcn = -1.</summary>
        public static List<DataRun> Pieces(List<DataRun> runs, long vcn, long count)
        {
            var list = new List<DataRun>();
            var end = vcn + count;
            foreach (var r in runs)
            {
                var rEnd = r.Vcn + r.Length;
                if (rEnd <= vcn || r.Vcn >= end) continue;
                var s = Math.Max(vcn, r.Vcn);
                var e = Math.Min(end, rEnd);
                list.Add(new DataRun(s, r.IsSparse ? -1 : r.Lcn + (s - r.Vcn), e - s));
            }
            return list;
        }

        private static void CopyFromRecycleBin(FoundFile f, string target)
        {
            if (f.IsFolder)
            {
                CopyFolder(f.RecyclePath, target);
                return;
            }
            File.Copy(f.RecyclePath, target, false);
        }

        private static void CopyFolder(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
            foreach (var dir in Directory.GetDirectories(source)) CopyFolder(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        private static void SetTimes(string path, FoundFile f)
        {
            try
            {
                if (f.Created > new DateTime(1980, 1, 1)) File.SetCreationTimeUtc(path, f.Created);
                if (f.Modified > new DateTime(1980, 1, 1)) File.SetLastWriteTimeUtc(path, f.Modified);
            }
            catch (IOException) { /* times are nice to have */ }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* ignore */ }
        }
    }
}
