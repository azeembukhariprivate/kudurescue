using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudurescue.Core
{
    /// <summary>
    /// Files in a drive's Recycle Bin. Each deleted item has two entries in X:\$Recycle.Bin\&lt;user&gt;\:
    /// "$I…" holds the original path, size and time, and "$R…" is the file (or folder) itself.
    /// </summary>
    public static class RecycleBin
    {
        public static List<FoundFile> List(string drive)
        {
            var files = new List<FoundFile>();
            var root = Path.Combine(VolumeDevice.DriveLetter(drive) + @":\", "$Recycle.Bin");
            if (!Directory.Exists(root)) return files;

            string[] users;
            try { users = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return files; }

            foreach (var user in users)
            {
                string[] infos;
                try { infos = Directory.GetFiles(user, "$I*"); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; } // another user's bin

                foreach (var info in infos)
                {
                    var item = Read(info);
                    if (item != null) files.Add(item);
                }
            }
            return files.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Reads one "$I" file. Returns null if it isn't one.</summary>
        public static FoundFile Read(string infoPath)
        {
            byte[] data;
            try { data = File.ReadAllBytes(infoPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }

            var parsed = Parse(data);
            if (parsed == null) return null;
            var (original, size, deleted) = parsed.Value;

            var dir = Path.GetDirectoryName(infoPath) ?? string.Empty;
            var content = Path.Combine(dir, "$R" + Path.GetFileName(infoPath).Substring(2));
            var isFolder = Directory.Exists(content);
            var exists = isFolder || File.Exists(content);

            // The original path is a Windows path ("C:\Users\...\file.txt"); split it by hand so this also works in the tests.
            var parts = original.TrimEnd('\\').Split('\\');
            var name = parts[parts.Length - 1];
            var folder = parts.Length > 2 && parts[0].EndsWith(":") ? string.Join("\\", parts.Skip(1).Take(parts.Length - 2)) : string.Empty;

            DateTime modified = deleted;
            try { if (exists) modified = isFolder ? Directory.GetLastWriteTimeUtc(content) : File.GetLastWriteTimeUtc(content); }
            catch { /* keep the deletion time */ }

            return new FoundFile
            {
                Source = FoundSource.RecycleBin,
                Name = string.IsNullOrEmpty(name) ? original : name,
                Folder = folder.Trim('\\'),
                Size = size,
                Deleted = deleted,
                Modified = modified,
                Created = modified,
                RecyclePath = content,
                IsFolder = isFolder,
                Kind = isFolder ? FileKind.Other : FileKinds.Of(name),
                Chance = exists ? Chance.Excellent : Chance.Overwritten,
                GoodPercent = exists ? 100 : 0
            };
        }

        /// <summary>
        /// Version 1 (Windows Vista to 8.1): version, size, deletion time, then the path in 520 bytes.
        /// Version 2 (Windows 10 and 11): version, size, deletion time, the path's length in characters, then the path.
        /// </summary>
        public static (string Path, long Size, DateTime Deleted)? Parse(byte[] d)
        {
            if (d.Length < 28) return null;
            var version = BitConverter.ToInt64(d, 0);
            var size = BitConverter.ToInt64(d, 8);
            var deleted = FileRecord.FileTime(BitConverter.ToInt64(d, 16));
            string path;
            if (version == 1)
            {
                if (d.Length < 24 + 2) return null;
                path = Encoding.Unicode.GetString(d, 24, Math.Min(520, d.Length - 24));
            }
            else if (version == 2)
            {
                var chars = BitConverter.ToInt32(d, 24);
                if (chars <= 0 || 28 + chars * 2 > d.Length) return null;
                path = Encoding.Unicode.GetString(d, 28, chars * 2);
            }
            else return null;

            var nul = path.IndexOf('\0');
            if (nul >= 0) path = path.Substring(0, nul);
            if (path.Length < 3 || size < 0) return null;
            return (path, size, deleted);
        }
    }
}
