using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace Kudurescue.Core
{
    /// <summary>
    /// A scan saved to a file (.kudurescue), to look at again or recover from later without scanning again.
    /// A Deep scan that was stopped can carry on from where it got to. Stored as compressed JSON.
    /// </summary>
    [DataContract]
    public sealed class SavedScan
    {
        public const string Extension = ".kudurescue";
        public const string FileFilter = "KUDURESCUE scans (*.kudurescue)|*.kudurescue";

        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string Kind { get; set; }           // "Quick", "Deep", "RecycleBin", "Snapshot"
        [DataMember] public string Title { get; set; }
        [DataMember] public string Drive { get; set; }          // "D:" for a drive
        [DataMember] public string ImagePath { get; set; }      // or a drive image…
        [DataMember] public long PartOffset { get; set; }       // …and where the volume starts in it
        [DataMember] public long PartLength { get; set; }
        [DataMember] public string SnapshotPath { get; set; }
        [DataMember] public DateTime SnapshotTaken { get; set; }
        [DataMember] public string Fingerprint { get; set; }    // to notice when the drive is a different one, or has changed
        [DataMember] public bool Finished { get; set; }
        [DataMember] public long ResumeAt { get; set; }         // Deep scan: where to carry on from
        [DataMember] public DateTime Saved { get; set; }
        [DataMember] public List<FoundFile> Files { get; set; } = new List<FoundFile>();

        public bool CanResume => Kind == "Deep" && !Finished;

        public void Save(string path)
        {
            Saved = DateTime.UtcNow;
            var temp = path + ".saving";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new GZipStream(file, CompressionLevel.Optimal))
                Serializer().WriteObject(zip, this);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static SavedScan Load(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new GZipStream(file, CompressionMode.Decompress))
                {
                    var scan = (SavedScan)Serializer().ReadObject(zip);
                    if (scan?.Files == null || string.IsNullOrEmpty(scan.Kind)) throw new InvalidDataException("This isn't a KUDURESCUE scan file.");
                    foreach (var f in scan.Files) f.Folder = f.Folder ?? string.Empty;
                    return scan;
                }
            }
            catch (Exception ex) when (ex is SerializationException || ex is InvalidDataException || ex is IOException && !(ex is FileNotFoundException))
            {
                throw new InvalidDataException("This scan file can't be read: " + ex.Message);
            }
        }

        private static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(SavedScan), new DataContractJsonSerializerSettings { DateTimeFormat = new DateTimeFormat("o") });

        /// <summary>A short fingerprint of a drive's first 64 KB (its boot area), which changes when it's formatted or replaced.</summary>
        public static string FingerprintOf(IBlockDevice device)
        {
            if (device == null) return null;
            var n = (int)Math.Min(64 * 1024, device.Length);
            var data = new byte[n];
            SafeRead.Read(device, 0, data, 0, n);
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Take(12).Select(b => b.ToString("x2")));
        }
    }
}
