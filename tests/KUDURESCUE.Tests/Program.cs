using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kudurescue.Core;

namespace Kudurescue.Tests
{
    /// <summary>A tiny test runner: each check prints PASS or FAIL, and the exit code is the number of failures.</summary>
    internal static class Program
    {
        private static int _failures;
        private static int _passes;
        private static string _temp;

        private static int Main()
        {
            _temp = Path.Combine(Path.GetTempPath(), "kudurescue-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
            try
            {
                Run("Decodes runlists", DecodesRunlists);
                Run("Reads the boot sector", ReadsBootSector);
                Run("Applies fixups and spots torn records", Fixups);
                Run("Decompresses LZNT1", DecompressesLznt1);
                Run("Finds deleted files with names and folders", FindsDeletedFiles);
                Run("Works out recovery chances", RecoveryChances);
                Run("Recovers files byte for byte", RecoversFiles);
                Run("Recovery options and naming", RecoveryNaming);
                Run("Reads Recycle Bin entries", ReadsRecycleBin);
                Run("Groups files by type", GroupsFiles);
                Run("Counts used clusters", CountsClusters);
                Run("FAT32: deleted files, names, folders, guessed clusters", Fat32);
                Run("exFAT: deleted files, chains, folders", ExFat);
                Run("Recognises file types", RecognisesTypes);
                Run("Deep scan: finds files by their contents", DeepScanFindsFiles);
                Run("Deep scan: only free space on a readable drive", DeepScanFreeSpace);
            }
            finally
            {
                try { Directory.Delete(_temp, true); } catch { /* ignore */ }
            }
            Console.WriteLine();
            Console.WriteLine($"{_passes} passed, {_failures} failed");
            return _failures;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passes++;
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("FAIL  " + name + "\n      " + ex.Message + "\n" + ex.StackTrace?.Split('\n').FirstOrDefault());
            }
        }

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what}: expected <{expected}> but got <{actual}>");
        }

        // ================================================================== the test drive

        private static readonly byte[] PhotoData = NtfsImage.Pattern(10000, 1);
        private static readonly byte[] ReportData = NtfsImage.Pattern(12000, 2);
        private static readonly byte[] SparseHead = NtfsImage.Pattern(4096, 3);
        private static readonly byte[] SparseTail = NtfsImage.Pattern(4096, 4);
        private static readonly byte[] ListedData = NtfsImage.Pattern(8000, 5);
        private static readonly byte[] PartialData = NtfsImage.Pattern(16384, 6);
        private static byte[] _compressible;
        private static string _image;

        /// <summary>Builds (once) a drive image with one of every case the engine has to handle.</summary>
        private static string Image()
        {
            if (_image != null) return _image;
            var img = new NtfsImage();
            NtfsImage.Record R(long n, bool inUse = false, bool dir = false, int seq = 1) => new NtfsImage.Record { Number = n, InUse = inUse, Directory = dir, Sequence = seq };

            // 24: a folder still in use. 25: a deleted photo in it, all clusters free.
            var photos = R(24, true, true, 2);
            photos.Attributes.Add(NtfsImage.StandardInformation(0x10));
            photos.Attributes.Add(NtfsImage.FileName(5, 5, "Photos"));
            img.Add(photos);
            var photo = R(25);
            photo.Attributes.Add(NtfsImage.StandardInformation());
            photo.Attributes.Add(NtfsImage.FileName(24, 2, "IMG_0001.JPG"));
            photo.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (100L, 3L) }, PhotoData.Length));
            img.Add(photo);
            img.WriteClusters(100, PhotoData);

            // 26: a small deleted file kept inside its record (resident).
            var note = R(26);
            note.Attributes.Add(NtfsImage.StandardInformation());
            note.Attributes.Add(NtfsImage.FileName(5, 5, "note.txt"));
            note.Attributes.Add(NtfsImage.Resident(0x80, Encoding.ASCII.GetBytes("hello from inside the index")));
            img.Add(note);

            // 27: deleted, but its clusters now belong to another file.
            var over = R(27);
            over.Attributes.Add(NtfsImage.StandardInformation());
            over.Attributes.Add(NtfsImage.FileName(5, 5, "overwritten.bin"));
            over.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (200L, 2L) }, 8192));
            img.Add(over);
            img.MarkUsed(200, 2);

            // 28: half of its clusters reused.
            var partial = R(28);
            partial.Attributes.Add(NtfsImage.StandardInformation());
            partial.Attributes.Add(NtfsImage.FileName(5, 5, "partial.doc"));
            partial.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (300L, 4L) }, PartialData.Length));
            img.Add(partial);
            img.WriteClusters(300, PartialData);
            img.MarkUsed(302, 2);

            // 29: a file still in use must not be listed.
            var keep = R(29, true);
            keep.Attributes.Add(NtfsImage.StandardInformation());
            keep.Attributes.Add(NtfsImage.FileName(5, 5, "keep.txt"));
            keep.Attributes.Add(NtfsImage.Resident(0x80, Encoding.ASCII.GetBytes("still here")));
            img.Add(keep);

            // 30: a deleted folder (its sequence number moved on by one when it was freed).
            // 31: a fragmented file inside it; the second piece lies before the first on the drive.
            var old = R(30, false, true, 3);
            old.Attributes.Add(NtfsImage.StandardInformation(0x10));
            old.Attributes.Add(NtfsImage.FileName(5, 5, "Old"));
            img.Add(old);
            var report = R(31);
            report.Attributes.Add(NtfsImage.StandardInformation());
            report.Attributes.Add(NtfsImage.FileName(30, 2, "report.pdf"));
            report.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (400L, 1L), (350L, 2L) }, ReportData.Length));
            img.Add(report);
            img.WriteClusters(400, ReportData.Take(4096).ToArray());
            img.WriteClusters(350, ReportData.Skip(4096).ToArray());

            // 32: its folder's record (40) was reused by an ordinary file, so it goes to "Lost folders".
            var lost = R(32);
            lost.Attributes.Add(NtfsImage.StandardInformation());
            lost.Attributes.Add(NtfsImage.FileName(40, 1, "orphan.txt"));
            lost.Attributes.Add(NtfsImage.Resident(0x80, Encoding.ASCII.GetBytes("orphan")));
            img.Add(lost);
            var reused = R(40, true);
            reused.Attributes.Add(NtfsImage.StandardInformation());
            reused.Attributes.Add(NtfsImage.FileName(5, 5, "new file.txt"));
            img.Add(reused);

            // 33: a sparse file: data, two empty clusters, data.
            var sparse = R(33);
            sparse.Attributes.Add(NtfsImage.StandardInformation());
            sparse.Attributes.Add(NtfsImage.FileName(5, 5, "sparse.dat"));
            sparse.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (500L, 1L), (-1L, 2L), (501L, 1L) }, 16384, 0x8000));
            img.Add(sparse);
            img.WriteClusters(500, SparseHead);
            img.WriteClusters(501, SparseTail);

            // 34: an NTFS-compressed file: one compressed unit of 16 clusters, then an empty unit.
            var text = new StringBuilder();
            for (var i = 0; text.Length < 65536; i++) text.Append($"Line {i % 97}: KUDURESCUE gets deleted files back. ");
            _compressible = Encoding.ASCII.GetBytes(text.ToString().Substring(0, 65536)).Concat(new byte[100000 - 65536]).ToArray();
            var packed = NtfsImage.CompressLznt1(_compressible.Take(65536).ToArray());
            var packedClusters = (packed.Length + NtfsImage.Cluster - 1) / NtfsImage.Cluster;
            Check(packedClusters < 16, "test data compresses");
            var comp = R(34);
            comp.Attributes.Add(NtfsImage.StandardInformation());
            comp.Attributes.Add(NtfsImage.FileName(5, 5, "compressed.txt"));
            comp.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (600L, (long)packedClusters), (-1L, 16L - packedClusters), (-1L, 16L) }, 100000, 0x0001, 4));
            img.Add(comp);
            img.WriteClusters(600, packed);

            // 35 + 36: a file whose data attribute lives in an extension record (attribute list).
            var listed = R(35);
            listed.Attributes.Add(NtfsImage.StandardInformation());
            listed.Attributes.Add(NtfsImage.AttributeList((0x10, 35, 0), (0x30, 35, 0), (0x80, 36, 0)));
            listed.Attributes.Add(NtfsImage.FileName(24, 2, "split record.mp4"));
            img.Add(listed);
            var extension = new NtfsImage.Record { Number = 36, BaseRecord = 35 };
            extension.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (700L, 2L) }, ListedData.Length));
            img.Add(extension);
            img.WriteClusters(700, ListedData);

            // 37: encrypted with EFS.
            var secret = R(37);
            secret.Attributes.Add(NtfsImage.StandardInformation(0x4020));
            secret.Attributes.Add(NtfsImage.FileName(5, 5, "secret.docx"));
            secret.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (800L, 1L) }, 100, 0x4000));
            img.Add(secret);

            // 38: a record that was only partly written (its fixup doesn't match): skipped.
            var torn = R(38);
            torn.TearFixup = true;
            torn.Attributes.Add(NtfsImage.StandardInformation());
            torn.Attributes.Add(NtfsImage.FileName(5, 5, "torn.txt"));
            torn.Attributes.Add(NtfsImage.Resident(0x80, new byte[10]));
            img.Add(torn);

            // 39: a long name with its 8.3 short name listed first.
            var longName = R(39);
            longName.Attributes.Add(NtfsImage.StandardInformation());
            longName.Attributes.Add(NtfsImage.FileName(5, 5, "LONGFI~1.TXT", 2));
            longName.Attributes.Add(NtfsImage.FileName(5, 5, "Long file name.txt", 1));
            longName.Attributes.Add(NtfsImage.Resident(0x80, Encoding.ASCII.GetBytes("long")));
            img.Add(longName);

            // 41 + 42: a deleted folder whose folder flag was cleared; its directory index still marks it as a folder.
            var noFlag = R(41, false, false, 4);
            noFlag.Attributes.Add(NtfsImage.StandardInformation(0x10));
            noFlag.Attributes.Add(NtfsImage.FileName(5, 5, "Unflagged"));
            noFlag.Attributes.Add(NtfsImage.Resident(0x90, new byte[48], "$I30"));
            img.Add(noFlag);
            var inside = R(42);
            inside.Attributes.Add(NtfsImage.StandardInformation());
            inside.Attributes.Add(NtfsImage.FileName(41, 4, "inside.txt"));
            inside.Attributes.Add(NtfsImage.Resident(0x80, Encoding.ASCII.GetBytes("inside")));
            img.Add(inside);

            _image = img.Save(Path.Combine(_temp, "drive.img"));
            return _image;
        }

        private static List<FoundFile> _found;

        private static List<FoundFile> Scan()
        {
            if (_found != null) return _found;
            using (var volume = NtfsVolume.Open(new ImageDevice(Image())))
                _found = QuickScan.RunToEnd(volume, "T:");
            return _found;
        }

        private static FoundFile Find(string name) =>
            Scan().FirstOrDefault(f => f.Name == name) ?? throw new Exception("not found: " + name + " (found " + string.Join(", ", Scan().Select(f => f.RelativePath)) + ")");

        // ================================================================== tests

        private static void DecodesRunlists()
        {
            var runs = Runlist.Decode(NtfsImage.EncodeRuns(new[] { (1000L, 8L), (900L, 4L), (-1L, 16L), (70000L, 300L) }), 0, 64);
            Equal(4, runs.Count, "runs");
            Equal(1000L, runs[0].Lcn, "first lcn");
            Equal(900L, runs[1].Lcn, "negative offset");
            Equal(8L, runs[1].Vcn, "vcn of second run");
            Check(runs[2].IsSparse && runs[2].Length == 16, "sparse run");
            Equal(70000L, runs[3].Lcn, "offset relative to the last real run");
            Equal(28L, runs[3].Vcn, "vcn after a sparse run");
            var threw = false;
            try { Runlist.Decode(new byte[] { 0x11, 0x00, 0x05, 0x00 }, 0, 4); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "zero-length run rejected");
        }

        private static void ReadsBootSector()
        {
            var sector = File.ReadAllBytes(Image()).Take(512).ToArray();
            var boot = NtfsBoot.Parse(sector);
            Equal(4096, boot.ClusterSize, "cluster size");
            Equal(1024, boot.RecordSize, "record size");
            Equal(NtfsImage.MftLcn, boot.MftLcn, "MFT position");
            var bad = (byte[])sector.Clone();
            Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(bad, 3);
            Check(NtfsBoot.IsBitLocker(bad) && !NtfsBoot.IsNtfs(bad), "BitLocker detected");
        }

        private static void Fixups()
        {
            var data = File.ReadAllBytes(Image());
            var offset = (int)(NtfsImage.MftLcn * NtfsImage.Cluster + 25 * NtfsImage.RecordSize);
            var record = FileRecord.Parse(data, offset, NtfsImage.RecordSize, 25);
            Check(record != null && !record.InUse, "record 25 parsed");
            Equal("IMG_0001.JPG", record.FileName.Name, "name");
            var torn = File.ReadAllBytes(Image());
            Check(FileRecord.Parse(torn, (int)(NtfsImage.MftLcn * NtfsImage.Cluster + 38 * NtfsImage.RecordSize), NtfsImage.RecordSize, 38) == null, "torn record rejected");
        }

        private static void DecompressesLznt1()
        {
            var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabcabcabc-the quick brown fox-", 300)));
            var packed = NtfsImage.CompressLznt1(text);
            Check(packed.Length < text.Length / 3, "compresses well");
            var unpacked = Lznt1.Decompress(packed, packed.Length, 16384);
            Check(unpacked.Take(text.Length).SequenceEqual(text), "round trip");
            Check(unpacked.Skip(text.Length).All(b => b == 0), "padded with zeros");

            var random = NtfsImage.Pattern(5000, 9);
            var stored = NtfsImage.CompressLznt1(random); // random data: stored chunks
            Check(Lznt1.Decompress(stored, stored.Length, 8192).Take(5000).SequenceEqual(random), "stored chunks");
        }

        private static void FindsDeletedFiles()
        {
            var names = Scan().Select(f => f.RelativePath).ToList();
            Check(names.Contains(@"Photos\IMG_0001.JPG"), "file in a folder still in use");
            Check(names.Contains("note.txt"), "resident file");
            Check(names.Contains(@"Old\report.pdf"), "file in a deleted folder: " + string.Join(", ", names));
            Check(names.Contains(@"Lost folders\orphan.txt"), "lost folder");
            Check(names.Contains(@"Unflagged\inside.txt") && !names.Contains("Unflagged"), "folder recognised by its index");
            Check(names.Contains(@"Photos\split record.mp4"), "attribute list");
            Check(names.Contains("Long file name.txt") && !names.Contains("LONGFI~1.TXT"), "long name preferred");
            Check(!names.Contains("keep.txt"), "files in use aren't listed");
            Check(!names.Contains("torn.txt"), "torn records are skipped");
            Check(!names.Any(n => n.StartsWith("$")), "system files aren't listed");
            Check(!names.Contains("Photos") && !names.Contains("Old"), "folders aren't listed as files");

            var photo = Find("IMG_0001.JPG");
            Equal(10000L, photo.Size, "size");
            Equal(NtfsImage.Modified, photo.Modified, "modified time");
            Equal(NtfsImage.Created, photo.Created, "created time");
            Equal(FileKind.Photos, photo.Kind, "kind");
            Equal(FileKind.Videos, Find("split record.mp4").Kind, "kind from extension");
        }

        private static void RecoveryChances()
        {
            Equal(Chance.Excellent, Find("IMG_0001.JPG").Chance, "free clusters");
            Equal(Chance.Excellent, Find("note.txt").Chance, "resident");
            Equal(Chance.Overwritten, Find("overwritten.bin").Chance, "reused clusters");
            var partial = Find("partial.doc");
            Equal(Chance.Partial, partial.Chance, "half reused");
            Equal(50, partial.GoodPercent, "percentage left");
            Equal(Chance.Unrecoverable, Find("secret.docx").Chance, "encrypted");
            Equal(Chance.Excellent, Find("sparse.dat").Chance, "sparse runs don't count");
            Equal(Chance.Excellent, Find("compressed.txt").Chance, "compressed");
        }

        private static void RecoversFiles()
        {
            var dest = Path.Combine(_temp, "recovered");
            RecoverResult result;
            using (var volume = NtfsVolume.Open(new ImageDevice(Image())))
                result = Recovery.Run(volume, Scan(), new RecoverOptions { Destination = dest, KeepFolders = true, RecoverPartial = true });

            Check(File.ReadAllBytes(Path.Combine(dest, "Photos", "IMG_0001.JPG")).SequenceEqual(PhotoData), "photo");
            Equal("hello from inside the index", File.ReadAllText(Path.Combine(dest, "note.txt")), "resident");
            Check(File.ReadAllBytes(Path.Combine(dest, "Old", "report.pdf")).SequenceEqual(ReportData), "fragmented file in the right order");
            var sparse = File.ReadAllBytes(Path.Combine(dest, "sparse.dat"));
            Check(sparse.Take(4096).SequenceEqual(SparseHead) && sparse.Skip(4096).Take(8192).All(b => b == 0) && sparse.Skip(12288).SequenceEqual(SparseTail), "sparse file");
            Check(File.ReadAllBytes(Path.Combine(dest, "compressed.txt")).SequenceEqual(_compressible), "compressed file");
            Check(File.ReadAllBytes(Path.Combine(dest, "Photos", "split record.mp4")).SequenceEqual(ListedData), "attribute list file");
            Check(File.ReadAllBytes(Path.Combine(dest, "partial.doc".Replace(".doc", " (PARTIAL).doc"))).Take(8192).SequenceEqual(PartialData.Take(8192)), "partial file kept, marked");
            Check(File.Exists(Path.Combine(dest, QuickScan.LostFolders, "orphan.txt")), "lost folder");
            Check(!File.Exists(Path.Combine(dest, "overwritten.bin")) && !File.Exists(Path.Combine(dest, "secret.docx")), "overwritten and encrypted files skipped");
            Equal(2, result.Skipped, "skipped");
            Equal(1, result.Partial, "partial count");
            Equal(0, result.Problems.Count, "problems: " + string.Join("; ", result.Problems.Select(p => p.File + " " + p.Reason)));
            Equal(NtfsImage.Modified, File.GetLastWriteTimeUtc(Path.Combine(dest, "Photos", "IMG_0001.JPG")), "modified time restored");

            // Again: names don't collide, and without folders everything goes in one folder.
            using (var volume = NtfsVolume.Open(new ImageDevice(Image())))
                Recovery.Run(volume, Scan().Where(f => f.Name == "note.txt").ToList(), new RecoverOptions { Destination = dest, KeepFolders = false });
            Check(File.Exists(Path.Combine(dest, "note (2).txt")), "second copy gets a free name");

            using (var volume = NtfsVolume.Open(new ImageDevice(Image())))
            {
                var preview = Recovery.ReadStart(volume, Find("IMG_0001.JPG"), 100);
                Check(preview != null && preview.Length == 100 && preview.SequenceEqual(PhotoData.Take(100)), "preview reads the start");
            }
        }

        private static void RecoveryNaming()
        {
            Equal("a_b_c.txt", Recovery.SafeName("a:b?c.txt"), "reserved characters");
            Equal("_CON.txt", Recovery.SafeName("CON.txt"), "device names");
            Equal("name", Recovery.SafeName("name. . "), "trailing dots and spaces");
            Equal("_", Recovery.SafeName(""), "empty");
            var f = new FoundFile { Name = "x.jpg", Folder = @"A\B", Chance = Chance.Partial };
            Equal(Path.Combine("D", "A", "B", "x (PARTIAL).jpg"), Recovery.TargetPath(f, new RecoverOptions { Destination = "D", KeepFolders = true }), "target path");
            Equal(Path.Combine("D", "x (PARTIAL).jpg"), Recovery.TargetPath(f, new RecoverOptions { Destination = "D", KeepFolders = false }), "without folders");

            var pieces = Recovery.Pieces(new List<DataRun> { new DataRun(0, 100, 10), new DataRun(10, -1, 6), new DataRun(16, 200, 16) }, 8, 16);
            Equal(3, pieces.Count, "pieces");
            Equal(108L, pieces[0].Lcn, "cut start");
            Equal(2L, pieces[0].Length, "cut length");
            Check(pieces[1].IsSparse && pieces[1].Length == 6, "sparse piece");
            Equal(8L, pieces[2].Length, "cut end");
        }

        private static void ReadsRecycleBin()
        {
            var path = @"C:\Users\Azeem\Documents\Budget 2026.xlsx";
            var v2 = new List<byte>();
            v2.AddRange(BitConverter.GetBytes(2L));
            v2.AddRange(BitConverter.GetBytes(88123L));
            v2.AddRange(BitConverter.GetBytes(NtfsImage.Modified.ToFileTimeUtc()));
            v2.AddRange(BitConverter.GetBytes(path.Length + 1));
            v2.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
            var parsed = RecycleBin.Parse(v2.ToArray());
            Check(parsed != null, "version 2 parsed");
            Equal(path, parsed.Value.Path, "path");
            Equal(88123L, parsed.Value.Size, "size");
            Equal(NtfsImage.Modified, parsed.Value.Deleted, "deleted time");

            var v1 = new byte[24 + 520];
            BitConverter.GetBytes(1L).CopyTo(v1, 0);
            BitConverter.GetBytes(5L).CopyTo(v1, 8);
            Encoding.Unicode.GetBytes(@"D:\old.txt").CopyTo(v1, 24);
            Equal(@"D:\old.txt", RecycleBin.Parse(v1).Value.Path, "version 1");
            Check(RecycleBin.Parse(new byte[10]) == null && RecycleBin.Parse(new byte[40]) == null, "rubbish rejected");

            // A real pair of files, as Windows leaves them.
            var bin = Path.Combine(_temp, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "$IABC123.xlsx"), v2.ToArray());
            File.WriteAllText(Path.Combine(bin, "$RABC123.xlsx"), "sheet");
            var item = RecycleBin.Read(Path.Combine(bin, "$IABC123.xlsx"));
            Equal("Budget 2026.xlsx", item.Name, "name");
            Equal(@"Users\Azeem\Documents", item.Folder, "folder relative to the drive");
            Equal(Chance.Excellent, item.Chance, "content still there");
            Equal(FileKind.Documents, item.Kind, "kind");

            var dest = Path.Combine(_temp, "from-bin");
            Recovery.Run(null, new List<FoundFile> { item }, new RecoverOptions { Destination = dest });
            Equal("sheet", File.ReadAllText(Path.Combine(dest, "Users", "Azeem", "Documents", "Budget 2026.xlsx")), "restored from the bin");
        }

        private static void GroupsFiles()
        {
            Equal(FileKind.Photos, FileKinds.Of("IMG.CR2"), "camera raw");
            Equal(FileKind.Email, FileKinds.Of("Outlook.pst"), "email");
            Equal(FileKind.Design, FileKinds.Of("logo.psd"), "design");
            Equal(FileKind.Data, FileKinds.Of("book.epub"), "ebook");
            Equal(FileKind.Programs, FileKinds.Of("setup.msi"), "programs");
            Equal(FileKind.Archives, FileKinds.Of("backup.7z"), "archives");
            Equal(FileKind.Other, FileKinds.Of("noextension"), "other");
            Equal("Email & contacts", FileKinds.Label(FileKind.Email), "label");
        }

        private static void CountsClusters()
        {
            using (var volume = NtfsVolume.Open(new ImageDevice(Image())))
            {
                var bitmap = volume.Bitmap;
                Check(bitmap.IsAllocated(0) && bitmap.IsAllocated(50) && !bitmap.IsAllocated(51), "single clusters");
                Equal(51L, bitmap.CountAllocated(0, 100), "whole bytes and loose bits");
                Equal(1L, bitmap.CountAllocated(301, 2), "partly used range");
                Equal(5L, bitmap.CountAllocated(NtfsImage.Clusters - 3, 8), "past the end counts as used");
            }
        }

        private static void Fat32()
        {
            var img = new Fat32Image();
            var photo = NtfsImage.Pattern(3000, 11); photo[0] = 0xFF; photo[1] = 0xD8; photo[2] = 0xFF;
            img.Write(100, photo);
            img.Entry(2, "HOLIDA~1JPG", "Holiday photo.jpg", 100, photo.Length, deleted: true);

            img.Folder(50);
            img.Use(50, 1);
            img.Dots(50, 0);
            img.Entry(2, "DCIM       ", null, 50, 0, deleted: false, isFolder: true);
            var short1 = NtfsImage.Pattern(700, 12);
            img.Write(200, short1);
            img.Entry(50, "IMG_0002JPG", null, 200, short1.Length, deleted: true);
            var unicode = NtfsImage.Pattern(900, 13);
            img.Write(220, unicode);
            img.Entry(50, "UNICOD~1TXT", "Ünïcødé ファイル.txt", 220, unicode.Length, deleted: true);

            img.Folder(60);
            img.Dots(60, 0);
            img.FreeFolder(60);
            img.Entry(2, "OLD        ", "Old", 60, 0, deleted: true, isFolder: true);
            var report = NtfsImage.Pattern(1500, 14);
            img.Write(300, report);
            img.Entry(60, "REPORT  PDF", "report.pdf", 300, report.Length, deleted: true);

            img.Use(400, 2);
            img.Use(0x10000 + 400, 2); // both places it could have started are in use now
            img.Entry(2, "OVERWR~1BIN", "overwritten.bin", 400, 1024, deleted: true);
            img.Entry(2, "NEW     DAT", null, 400, 1024, deleted: false);
            img.Entry(2, "KEEP    TXT", "keep.txt", 410, 10, deleted: false);
            img.Use(410, 1);

            // Top half of the first cluster cleared: only one place fits (the low one is in use)…
            var big = NtfsImage.Pattern(2000, 15); Encoding.ASCII.GetBytes("ftyp").CopyTo(big, 4);
            img.Write(0x10000 + 500, big);
            img.Use(500, 4);
            img.Entry(2, "BIG     MP4", "big.mp4", 0x10000 + 500, big.Length, deleted: true);
            // …both places are free, and the data's signature decides…
            var clip = NtfsImage.Pattern(1000, 16); Encoding.ASCII.GetBytes("ftyp").CopyTo(clip, 4);
            img.Write(0x10000 + 700, clip);
            img.Entry(2, "CLIP    MP4", "clip.mp4", 0x10000 + 700, clip.Length, deleted: true);
            // …and with no way to tell, it's marked "probably".
            img.Entry(2, "NOTES   TXT", "notes.txt", 0x10000 + 900, 100, deleted: true);

            var path = img.Save(Path.Combine(_temp, "fat32.img"));
            List<FoundFile> found;
            var dest = Path.Combine(_temp, "fat32-recovered");
            using (var volume = Volumes.Open(new ImageDevice(path)))
            {
                Equal("FAT32", volume.FormatName, "format");
                found = QuickScan.RunToEnd(volume, "F:");
                Recovery.Run(volume, found, new RecoverOptions { Destination = dest });
            }
            FoundFile F(string rel) => found.FirstOrDefault(f => f.RelativePath == rel) ?? throw new Exception(rel + " not found in " + string.Join(", ", found.Select(f => f.RelativePath)));

            Equal(Chance.Excellent, F("Holiday photo.jpg").Chance, "long name rebuilt, free clusters");
            Equal(Fat32Image.Modified.ToUniversalTime(), F("Holiday photo.jpg").Modified, "modified time");
            Check(File.ReadAllBytes(Path.Combine(dest, "Holiday photo.jpg")).SequenceEqual(photo), "photo recovered");
            Check(F(@"DCIM\_MG_0002.JPG").Note.Contains("first letter"), "short name without its first letter, explained");
            Check(File.ReadAllBytes(Path.Combine(dest, "DCIM", "_MG_0002.JPG")).SequenceEqual(short1), "short-name file recovered");
            Check(File.ReadAllBytes(Path.Combine(dest, "DCIM", "Ünïcødé ファイル.txt")).SequenceEqual(unicode), "unicode long name");
            Check(File.ReadAllBytes(Path.Combine(dest, "Old", "report.pdf")).SequenceEqual(report), "file in a deleted folder");
            Equal(Chance.Overwritten, F("overwritten.bin").Chance, "reused clusters");
            Check(!found.Any(f => f.Name == "keep.txt" || f.Name == "NEW.DAT"), "live files aren't listed");
            Equal(Chance.Excellent, F("big.mp4").Chance, "only one place fits, and it's confirmed");
            Check(File.ReadAllBytes(Path.Combine(dest, "big.mp4")).SequenceEqual(big), "big.mp4 from the right place");
            Check(File.ReadAllBytes(Path.Combine(dest, "clip.mp4")).SequenceEqual(clip), "signature picks the right place");
            Equal(Chance.Unsure, F("notes.txt").Chance, "can't tell: probably");
        }

        private static void ExFat()
        {
            var img = new ExFatImage();
            var video = NtfsImage.Pattern(10000, 21);
            img.Write(10, video);
            img.Set(4, "video.mp4", 10, video.Length, deleted: true, noFatChain: true);

            var frag = NtfsImage.Pattern(3 * ExFatImage.Cluster, 22);
            img.Write(20, frag.Take(8192).ToArray());
            img.Write(30, frag.Skip(8192).ToArray());
            img.Chain(20, 21, 30); // the FAT still holds the pieces
            img.Set(4, "frag.bin", 20, frag.Length, deleted: true, noFatChain: false);

            var lost = NtfsImage.Pattern(5000, 23);
            img.Write(40, lost);
            img.Set(4, "lostmap.doc", 40, lost.Length, deleted: true, noFatChain: false); // FAT entries cleared

            img.Folder(50);
            img.Set(4, "Trip", 50, ExFatImage.Cluster, deleted: true, noFatChain: true, isFolder: true);
            var beach = NtfsImage.Pattern(4000, 24);
            img.Write(60, beach);
            img.Set(50, "beach.jpg", 60, beach.Length, deleted: true, noFatChain: true);

            img.Folder(70);
            img.Use(70, 1, true);
            img.Set(4, "Docs", 70, ExFatImage.Cluster, deleted: false, noFatChain: false, isFolder: true);
            var plan = Encoding.UTF8.GetBytes("the plan");
            img.Write(80, plan);
            img.Set(70, "plan.txt", 80, plan.Length, deleted: true, noFatChain: true);
            img.Use(81, 1, false);
            img.Set(70, "live.txt", 81, 10, deleted: false, noFatChain: true);

            img.MarkUsed(90);
            img.Set(4, "gone.bin", 90, 100, deleted: true, noFatChain: true);
            var longName = NtfsImage.Pattern(100, 25);
            img.Write(95, longName);
            img.Set(4, "A rather long file name.txt", 95, longName.Length, deleted: true, noFatChain: true);

            var path = img.Save(Path.Combine(_temp, "exfat.img"));
            List<FoundFile> found;
            var dest = Path.Combine(_temp, "exfat-recovered");
            using (var volume = Volumes.Open(new ImageDevice(path)))
            {
                Equal("exFAT", volume.FormatName, "format");
                found = QuickScan.RunToEnd(volume, "E:");
                Recovery.Run(volume, found, new RecoverOptions { Destination = dest });
            }
            FoundFile F(string rel) => found.FirstOrDefault(f => f.RelativePath == rel) ?? throw new Exception(rel + " not found in " + string.Join(", ", found.Select(f => f.RelativePath)));

            Equal(Chance.Excellent, F("video.mp4").Chance, "one-piece file");
            Equal(ExFatImage.Modified, F("video.mp4").Modified, "UTC time");
            Check(File.ReadAllBytes(Path.Combine(dest, "video.mp4")).SequenceEqual(video), "video recovered");
            Check(File.ReadAllBytes(Path.Combine(dest, "frag.bin")).SequenceEqual(frag), "pieces followed through the FAT");
            Check(F("lostmap.doc").Note != null, "missing chain explained");
            Check(File.ReadAllBytes(Path.Combine(dest, "lostmap.doc")).SequenceEqual(lost), "read on from the start");
            Check(File.ReadAllBytes(Path.Combine(dest, "Trip", "beach.jpg")).SequenceEqual(beach), "file in a deleted folder");
            Equal("the plan", File.ReadAllText(Path.Combine(dest, "Docs", "plan.txt")), "file in a live folder");
            Check(!found.Any(f => f.Name == "live.txt"), "live files aren't listed");
            Equal(Chance.Overwritten, F("gone.bin").Chance, "reused clusters");
            Check(File.ReadAllBytes(Path.Combine(dest, "A rather long file name.txt")).SequenceEqual(longName), "name over two entries");
        }

        private static void RecognisesTypes()
        {
            Equal(true, Signatures.Matches(".jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 }), "jpeg");
            Equal(false, Signatures.Matches(".pdf", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 }), "not a pdf");
            Equal((bool?)null, Signatures.Matches(".xyz", new byte[8]), "unknown type");
        }

        private static void DeepScanFindsFiles()
        {
            var samples = new List<(string Ext, byte[] Data)>
            {
                (".jpg", CarveSamples.Jpeg()), (".png", CarveSamples.Png()), (".gif", CarveSamples.Gif()), (".bmp", CarveSamples.Bmp()),
                (".pdf", CarveSamples.Pdf()), (".docx", CarveSamples.Docx()), (".mp4", CarveSamples.Mp4()), (".wav", CarveSamples.Wav()),
                (".mp3", CarveSamples.Mp3()), (".ogg", CarveSamples.Ogg()), (".7z", CarveSamples.SevenZip()), (".sqlite", CarveSamples.Sqlite()),
                (".exe", CarveSamples.Exe()), (".ttf", CarveSamples.Ttf()), (".webm", CarveSamples.Webm()), (".doc", CarveSamples.Doc()),
                (".vcf", CarveSamples.Vcf()), (".psd", CarveSamples.Psd())
            };
            // Each file starts on a 512-byte boundary, with filler between them (as on a real drive).
            var image = new List<byte>();
            var offsets = new List<long>();
            foreach (var (ext, data) in samples)
            {
                while (image.Count % 512 != 0) image.Add(0xAA);
                image.AddRange(Enumerable.Repeat((byte)0xAA, 1024));
                offsets.Add(image.Count);
                image.AddRange(data);
            }
            while (image.Count % 512 != 0) image.Add(0xAA);
            image.AddRange(Enumerable.Repeat((byte)0xAA, 2048));
            var tailPng = CarveSamples.Png();
            var tailAt = image.Count;
            image.AddRange(tailPng); // ends the open-ended PSD
            image.AddRange(Enumerable.Repeat((byte)0xAA, 4096));
            var path = Path.Combine(_temp, "raw.img");
            File.WriteAllBytes(path, image.ToArray());

            List<FoundFile> found;
            var dest = Path.Combine(_temp, "carved");
            using (var deep = new DeepVolume(new ImageDevice(path), null))
            {
                found = QuickScan.RunToEnd(deep);
                Recovery.Run(deep, found, new RecoverOptions { Destination = dest, KeepFolders = false });
            }
            var problems = new List<string>();
            for (var i = 0; i < samples.Count; i++)
            {
                var (ext, data) = samples[i];
                var f = found.FirstOrDefault(x => x.Offset == offsets[i]);
                if (f == null) { problems.Add(ext + " not found"); continue; }
                if (f.Extension != ext) problems.Add($"{ext} found as {f.Extension}");
                if (ext == ".psd")
                {
                    if (f.Chance != Chance.Unsure || f.Size != tailAt - offsets[i]) problems.Add($"psd should run to the next file as Probably, got {f.Chance} {f.Size}");
                    continue;
                }
                if (f.Chance != Chance.Excellent) problems.Add(ext + " chance " + f.Chance);
                if (f.Size != data.Length) { problems.Add($"{ext} length {f.Size}, expected {data.Length}"); continue; }
                if (!File.ReadAllBytes(Path.Combine(dest, f.Name)).SequenceEqual(data)) problems.Add(ext + " content differs");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            Check(!found.Any(f => f.Extension == ".jpg" && f.Offset != offsets[0]), "the thumbnail inside the JPEG isn't listed on its own");
            Check(found.Any(f => f.Offset == tailAt && f.Extension == ".png"), "the last file is found too");
            Check(found.First(f => f.Extension == ".jpg").Folder == @"Photos\JPG" && found.First(f => f.Extension == ".jpg").Name == "Photo 0001.jpg", "named and grouped by type");
        }

        private static void DeepScanFreeSpace()
        {
            var img = new NtfsImage();
            var free = CarveSamples.Png();
            var used = CarveSamples.Png();
            img.WriteClusters(900, free);
            img.WriteClusters(950, used);
            img.MarkUsed(950, 2);
            var path = img.Save(Path.Combine(_temp, "deep-ntfs.img"));
            using (var device = new ImageDevice(path))
            using (var deep = new DeepVolume(device, Volumes.Open(device, false), false))
            {
                Check(deep.FormatName.Contains("NTFS"), "searches the free space of NTFS");
                Check(deep.Free.Bytes < device.Length, "only part of the drive is searched");
                var found = QuickScan.RunToEnd(deep);
                Check(found.Any(f => f.Offset == 900L * NtfsImage.Cluster && f.Chance == Chance.Excellent), "file in free space found");
                Check(!found.Any(f => f.Offset == 950L * NtfsImage.Cluster), "used space is skipped");
            }
        }
    }
}
