using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kudurescue.Core;

namespace Kudurescue.Views
{
    /// <summary>
    /// KUDURESCUE.exe --self-test: creates a small virtual drive (VHD), writes files to it, deletes them, then finds and
    /// recovers them with the real engine and compares every byte; checks the Recycle Bin reader; and opens every screen
    /// in both themes. Needs administrator rights (the GitHub build has them). Writes self-test.log next to the exe and
    /// exits with 0 when everything passed. Never touches the user's own drives or files.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;
        private static string _vhd;
        private static string _drive;
        private static readonly Dictionary<string, byte[]> Expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public static int Run()
        {
            Running = true;
            _log = new StringBuilder();
            _temp = Path.Combine(Path.GetTempPath(), "kudurescue-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);

            Step("Running as administrator", () => Expect(Admin.IsAdmin, "administrator rights (needed to read drives and attach a VHD)"));
            Step("Create a test drive (VHD, NTFS)", CreateDrive);
            Step("Write files, then delete them", WriteAndDelete);
            Step("Quick scan finds the deleted files", ScanFindsFiles);
            Step("Recover files byte for byte", RecoverFiles);
            Step("Drive image: copy the drive, open the copy, recover", ImageRound);
            Step("VHD files with a partition table (fixed and dynamic)", () => { VhdRound("fixed"); VhdRound("expandable"); });
            Step("Saved scan: save, open, recover", SavedRound);
            Step("FAT32 drive: delete, find and recover files", () => OtherFormat("fat32"));
            Step("exFAT drive: delete, find and recover files", () => OtherFormat("exfat"));
            Step("Deep scan: deleted files, then a formatted drive", DeepScanRound);

            Step("Load themes and styles", () => ThemeManager.Init(ThemeMode.Light));
            Step("Theme keys exist", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "Text", "SubtleText", "Accent", "AccentText", "LinkText", "OkBg", "OkText", "WarnBg", "WarnText",
                                            "BadBg", "BadText", "NeutralChipBg", "SelectionBg", "OffDot", "UiFont", "MonoFont", "PrimaryButton", "SecondaryButton",
                                            "GhostButton", "LinkButton", "Card", "OptionCard", "ChipRadio", "ChipToggle", "TableList", "IconCheck", "IconWarn",
                                            "IconTrash", "IconFolder", "IconFile", "IconSearch", "IconPause", "IconPlay", "IconStop", "IconBack", "IconShield",
                                            "IconSettings", "IconInfo" })
                    Application.Current.FindResource(key);
            });

            MainWindow main = null;
            Step("Main window: choose a drive", () =>
            {
                main = new MainWindow();
                OffScreen(main);
                Application.Current.MainWindow = main;
                main.Show();
                Pump();
                Expect(main.DriveList.Any(d => d.Letter == _drive), "the test drive is listed");
                Expect(main.DriveList.First(d => d.Letter == _drive).IsNtfs, "the test drive is NTFS");
            });
            Step("Main window: scan, pick files, recover", () => ScanThroughUi(main));
            Step("Recycle Bin", RecycleBinCheck); // writes to the test drive, so it comes after every recovery from it
            Step("Previous Versions snapshot", SnapshotRound);
            Step("Right-click menu on drives", () =>
            {
                ShellIntegration.Register();
                Expect(ShellIntegration.IsRegistered, "the menu is added");
                ShellIntegration.Remove();
                Expect(!ShellIntegration.IsRegistered, "the menu is removed");
            });
            Step("Main window: Recycle Bin results", () =>
            {
                main.ShowResults(RecycleBin.List(_drive), null, "Recycle Bin on " + _drive, true);
                Pump();
                Expect(main.Step == 2, "results shown");
            });
            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                main.ShowDrives();
                Pump();
                main.ShowResultsPage();
                Pump();
                ThemeManager.Apply(ThemeMode.Light);
                Pump();
            });
            Step("Settings window", () => ShowAndClose(new SettingsWindow(main)));
            Step("About window", () => ShowAndClose(About.Build(main)));
            Step("Close main window", () =>
            {
                main?.Close();
                Pump();
            });
            Step("Remove the test drive", RemoveDrive);

            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); } catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        private static void Step(string name, Action action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                action();
                Pump();
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        // ================================================================== the test drive

        private static void CreateDrive()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            var letter = "RSTUVWXYZQPONMLKJ".First(c => !used.Contains(c));
            _drive = letter + ":";
            _vhd = Path.Combine(_temp, "kudurescue-test.vhdx");
            Diskpart($"create vdisk file=\"{_vhd}\" maximum=256 type=expandable",
                $"select vdisk file=\"{_vhd}\"",
                "attach vdisk",
                "create partition primary",
                "format fs=ntfs quick label=KRTEST",
                $"assign letter={letter}");
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!Directory.Exists(_drive + "\\") && DateTime.UtcNow < until) Thread.Sleep(250);
            Expect(Directory.Exists(_drive + "\\"), $"drive {_drive} to appear");
            _log.AppendLine($"      test drive {_drive} ({new DriveInfo(_drive).DriveFormat})");
        }

        /// <summary>The same round trip on a FAT32 or exFAT virtual drive, on its own drive letter.</summary>
        private static void OtherFormat(string fs)
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            var letter = "RSTUVWXYZQPONMLKJ".First(c => !used.Contains(c));
            var drive = letter + ":";
            var vhd = Path.Combine(_temp, "kudurescue-" + fs + ".vhdx");
            Diskpart($"create vdisk file=\"{vhd}\" maximum=200 type=expandable", $"select vdisk file=\"{vhd}\"", "attach vdisk",
                "create partition primary", $"format fs={fs} quick label=KR{fs.ToUpperInvariant()}", $"assign letter={letter}");
            try
            {
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!Directory.Exists(drive + "\\") && DateTime.UtcNow < until) Thread.Sleep(250);
                Expect(Directory.Exists(drive + "\\"), $"drive {drive} to appear");

                var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                void Write(string relative, byte[] data)
                {
                    var path = Path.Combine(drive + "\\", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, data);
                    expected[relative] = data;
                }
                var jpeg = Pattern(250_000, 31);
                jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF;
                Write(@"DCIM\100CAMERA\IMG_0001.JPG", jpeg);
                Write(@"Holiday photos from Goa 2026.jpg", jpeg.Take(120_000).ToArray());
                Write(@"note.txt", Encoding.UTF8.GetBytes("a short note"));
                Write(@"Ünïcødé ファイル.txt", Pattern(3_000, 32));
                Write(@"Video\clip.mp4", Pattern(4_000_000, 33));
                Write(@"Old folder\report.pdf", Pattern(60_000, 34));

                FlushDrive(drive);
                foreach (var relative in expected.Keys) File.Delete(Path.Combine(drive + "\\", relative));
                Directory.Delete(Path.Combine(drive + "\\", "Old folder"), true);
                FlushDrive(drive);

                List<FoundFile> found;
                RecoverResult result;
                var recoveredAs = new Dictionary<string, string>();
                var dest = Path.Combine(_temp, "recovered-" + fs);
                using (var volume = Volumes.Open(VolumeDevice.Open(drive)))
                {
                    _log.AppendLine($"      {fs} drive {drive}: {volume.FormatName}, {volume.ClusterSize} byte clusters");
                    found = QuickScan.RunToEnd(volume, drive);
                    // On FAT, a name that fits 8.3 has no long name, and deleting it overwrites its first letter: it comes back as "_".
                    bool Same(FoundFile f, string relative) =>
                        f.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
                        (f.Name.StartsWith("_") && f.Folder.Equals(Path.GetDirectoryName(relative) ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                         f.Name.Substring(1).Equals(Path.GetFileName(relative).Substring(1), StringComparison.OrdinalIgnoreCase));
                    var wanted = found.Where(f => expected.Keys.Any(k => Same(f, k))).ToList();
                    foreach (var f in wanted) _log.AppendLine($"      {f.RelativePath}: {f.Chance}{(f.Note != null ? " (" + f.Note + ")" : string.Empty)}");
                    foreach (var relative in expected.Keys)
                    {
                        var f = wanted.FirstOrDefault(x => Same(x, relative));
                        Expect(f != null, relative + " found (found: " + string.Join(", ", found.Select(x => x.RelativePath).Take(30)) + ")");
                        Expect(f.Size == expected[relative].Length, $"{relative} size");
                        Expect(f.Chance == Chance.Excellent || f.Chance == Chance.Unsure, $"{relative} still there, got {f.Chance}");
                        recoveredAs[relative] = f.RelativePath;
                    }
                    result = Recovery.Run(volume, wanted, new RecoverOptions { Destination = dest });
                }
                Expect(result.Problems.Count == 0, "no problems: " + string.Join("; ", result.Problems.Select(p => p.File + ": " + p.Reason)));
                foreach (var pair in expected)
                    Expect(File.ReadAllBytes(Path.Combine(dest, recoveredAs[pair.Key])).SequenceEqual(pair.Value), pair.Key + " identical");
            }
            finally
            {
                Diskpart($"select vdisk file=\"{vhd}\"", "detach vdisk");
            }
        }

        private static void FlushDrive(string drive)
        {
            using (var handle = Native.CreateFile(@"\\.\" + drive, Native.GENERIC_READ | Native.GENERIC_WRITE,
                       Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (!handle.IsInvalid) Native.FlushFileBuffers(handle);
            }
            Thread.Sleep(1500);
        }

        private static void RemoveDrive()
        {
            if (_vhd == null || !File.Exists(_vhd)) return;
            Diskpart($"select vdisk file=\"{_vhd}\"", "detach vdisk");
        }

        private static void Diskpart(params string[] commands)
        {
            var script = Path.Combine(_temp, "diskpart-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllLines(script, commands);
            var info = new ProcessStartInfo("diskpart.exe", "/s \"" + script + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(info))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(180000)) { p.Kill(); throw new Exception("diskpart didn't finish"); }
                if (p.ExitCode != 0) throw new Exception("diskpart failed (" + p.ExitCode + "): " + output.Result);
            }
        }

        /// <summary>Makes Windows write everything it has cached for the drive, so a raw read sees the latest state.</summary>
        private static void Flush()
        {
            using (var handle = Native.CreateFile(@"\\.\" + _drive, Native.GENERIC_READ | Native.GENERIC_WRITE,
                       Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (!handle.IsInvalid) Native.FlushFileBuffers(handle);
            }
            Thread.Sleep(1500);
        }

        private static void Put(string relative, byte[] data)
        {
            var path = Path.Combine(_drive + "\\", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            Expected[relative] = data;
        }

        private static byte[] Pattern(int length, int seed)
        {
            var b = new byte[length];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static void WriteAndDelete()
        {
            Put(@"Photos\2026\Goa trip\IMG_4021.JPG", Pattern(300_000, 1));
            Put(@"Docs\note.txt", Encoding.UTF8.GetBytes("A small note, kept inside the drive's index."));
            Put(@"Docs\Ünïcødé ファイル.txt", Pattern(5_000, 2));
            Put(@"Video\clip.mp4", Pattern(6_000_000, 3));
            Put(@"Old folder\Inner\report.docx", Pattern(80_000, 4));

            // Two files written in turns, so they're likely to end up in several pieces.
            var a = new List<byte>();
            var b = new List<byte>();
            Directory.CreateDirectory(Path.Combine(_drive + "\\", "Fragments"));
            using (var fa = new FileStream(Path.Combine(_drive + "\\", @"Fragments\a.bin"), FileMode.Create))
            using (var fb = new FileStream(Path.Combine(_drive + "\\", @"Fragments\b.bin"), FileMode.Create))
            {
                for (var i = 0; i < 24; i++)
                {
                    var ca = Pattern(65536, 100 + i);
                    var cb = Pattern(65536, 200 + i);
                    fa.Write(ca, 0, ca.Length); fa.Flush(true);
                    fb.Write(cb, 0, cb.Length); fb.Flush(true);
                    a.AddRange(ca);
                    b.AddRange(cb);
                }
            }
            Expected[@"Fragments\a.bin"] = a.ToArray();
            Expected[@"Fragments\b.bin"] = b.ToArray();

            // An NTFS-compressed folder.
            Directory.CreateDirectory(Path.Combine(_drive + "\\", "Compressed"));
            Run("compact.exe", $"/c \"{_drive}\\Compressed\"");
            var text = new StringBuilder();
            for (var i = 0; text.Length < 400_000; i++) text.Append($"Row {i}: KUDURESCUE recovers NTFS-compressed files too. ");
            Put(@"Compressed\packed.txt", Encoding.ASCII.GetBytes(text.ToString()));
            var compressed = (File.GetAttributes(Path.Combine(_drive + "\\", @"Compressed\packed.txt")) & FileAttributes.Compressed) != 0;
            _log.AppendLine("      packed.txt is " + (compressed ? "compressed" : "NOT compressed (compression unavailable)"));

            Flush();
            foreach (var relative in Expected.Keys) File.Delete(Path.Combine(_drive + "\\", relative));
            Directory.Delete(Path.Combine(_drive + "\\", "Old folder"), true);
            Flush();
        }

        private static void Run(string exe, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
            }
        }

        private static List<FoundFile> _found;

        private static void ScanFindsFiles()
        {
            var watch = Stopwatch.StartNew();
            using (var volume = Volumes.Open(VolumeDevice.Open(_drive)))
                _found = QuickScan.RunToEnd(volume, _drive);
            _log.AppendLine($"      {_found.Count} deleted files found in {watch.Elapsed.TotalSeconds:0.0}s");
            foreach (var relative in Expected.Keys)
            {
                var f = _found.FirstOrDefault(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                Expect(f != null, relative + " found (found: " + string.Join(", ", _found.Select(x => x.RelativePath).Take(30)) + ")");
                Expect(f.Size == Expected[relative].Length, $"{relative} size {Expected[relative].Length}, got {f.Size}");
                Expect(f.Chance == Chance.Excellent, $"{relative} Excellent, got {f.Chance}");
                _log.AppendLine($"      {relative}: {f.Runs?.Count ?? 0} piece(s){(f.Compressed ? ", compressed" : string.Empty)}{(f.ResidentData != null ? ", inside the index" : string.Empty)}");
            }
        }

        private static void RecoverFiles()
        {
            var dest = Path.Combine(_temp, "recovered");
            var wanted = _found.Where(f => Expected.ContainsKey(f.RelativePath)).ToList();
            RecoverResult result;
            using (var volume = NtfsVolume.Open(VolumeDevice.Open(_drive)))
                result = Recovery.Run(volume, wanted, new RecoverOptions { Destination = dest, KeepFolders = true });
            Expect(result.Problems.Count == 0, "no problems: " + string.Join("; ", result.Problems.Select(p => p.File + ": " + p.Reason)));
            Expect(result.Recovered == Expected.Count, $"{Expected.Count} recovered, got {result.Recovered}");
            foreach (var pair in Expected)
            {
                var path = Path.Combine(dest, pair.Key);
                Expect(File.Exists(path), pair.Key + " recovered");
                Expect(File.ReadAllBytes(path).SequenceEqual(pair.Value), pair.Key + " identical");
            }
        }

        private static void RecycleBinCheck()
        {
            // A Recycle Bin entry laid out exactly as Windows writes it ($I with the details, $R with the content).
            var bin = Path.Combine(_drive + "\\", "$Recycle.Bin", "S-1-5-21-1000-1000-1000-1001");
            Directory.CreateDirectory(bin);
            var original = _drive + @"\Work\Budget 2026.xlsx";
            var info = new List<byte>();
            info.AddRange(BitConverter.GetBytes(2L));
            info.AddRange(BitConverter.GetBytes(11L));
            info.AddRange(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
            info.AddRange(BitConverter.GetBytes(original.Length + 1));
            info.AddRange(Encoding.Unicode.GetBytes(original + "\0"));
            File.WriteAllBytes(Path.Combine(bin, "$IKR0001.xlsx"), info.ToArray());
            File.WriteAllText(Path.Combine(bin, "$RKR0001.xlsx"), "spreadsheet");

            // And a real one, if this Windows session can use the Recycle Bin.
            var real = Path.Combine(_drive + "\\", "Bin test", "in the bin.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(real));
            File.WriteAllText(real, "recycled");
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(real, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                _log.AppendLine("      (sending to the Recycle Bin isn't available here: " + ex.Message + ")");
            }

            var items = RecycleBin.List(_drive);
            var budget = items.FirstOrDefault(i => i.Name == "Budget 2026.xlsx");
            Expect(budget != null, "the Recycle Bin entry is listed (found: " + string.Join(", ", items.Select(i => i.Name)) + ")");
            Expect(budget.Folder == "Work" && budget.Chance == Chance.Excellent && budget.Size == 11, "its folder, chance and size");
            var recycled = items.FirstOrDefault(i => i.Name == "in the bin.txt");
            _log.AppendLine("      real Recycle Bin item " + (recycled != null ? "found in " + recycled.Folder : "not found"));

            var dest = Path.Combine(_temp, "from-bin");
            var result = Recovery.Run(null, items, new RecoverOptions { Destination = dest });
            Expect(File.ReadAllText(Path.Combine(dest, "Work", "Budget 2026.xlsx")) == "spreadsheet", "restored from the Recycle Bin");
            if (recycled != null) Expect(File.ReadAllText(Path.Combine(dest, "Bin test", "in the bin.txt")) == "recycled", "real item restored");
            Expect(result.Problems.Count == 0, "no problems");
        }

        private static void ScanThroughUi(MainWindow main)
        {
            main.BeginScan(_drive);
            var until = DateTime.UtcNow.AddSeconds(120);
            while (main.Step != 2 && DateTime.UtcNow < until) Pump();
            Expect(main.Step == 2, "results shown after the scan");
            var results = main.Results;
            Expect(results.Files.Count >= Expected.Count, $"at least {Expected.Count} files listed, got {results.Files.Count}");

            var wanted = results.Files.Where(f => Expected.ContainsKey(f.RelativePath)).ToList();
            results.TickAll(wanted, true);
            Expect(results.Ticked == wanted.Count, "files ticked");
            main.ShowRecoverPage();
            Pump();

            results.Destination = _drive + @"\Recovered";
            Expect(results.CheckDestination() != null, "saving to the scanned drive is refused");
            results.Destination = Path.Combine(_temp, "ui-recovered");
            Expect(results.CheckDestination() == null, "a folder on another drive is accepted: " + results.CheckDestination());

            var task = results.StartRecovery();
            until = DateTime.UtcNow.AddSeconds(120);
            while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
            Pump();
            Expect(task.IsCompleted, "recovery finished");
            Expect(results.LastResult != null && results.LastResult.Recovered == wanted.Count,
                $"{wanted.Count} recovered through the UI, got {results.LastResult?.Recovered}: " + string.Join("; ", results.LastResult?.Problems.Select(p => p.Reason) ?? new string[0]));
            Expect(File.ReadAllBytes(Path.Combine(_temp, "ui-recovered", @"Photos\2026\Goa trip\IMG_4021.JPG")).SequenceEqual(Expected[@"Photos\2026\Goa trip\IMG_4021.JPG"]), "photo identical");

            // Back and forth between the steps, as people do.
            for (var i = 0; i < 2; i++)
            {
                main.ShowResultsPage();
                Pump();
                main.ShowRecoverPage();
                Pump();
            }
            main.ShowResultsPage();
            Pump();

            // A drive image and a saved scan through the screens.
            main.OpenImage(_imagePath, false);
            until = DateTime.UtcNow.AddSeconds(120);
            while (main.Step != 2 && DateTime.UtcNow < until) Pump();
            Expect(main.Step == 2 && main.Results.Files.Any(f => f.Name == "IMG_4021.JPG"), "the image's files are listed");
            main.OpenSavedScan(_savedPath);
            Pump();
            Expect(main.Step == 2 && main.Results.Files.Count == _found.Count, "the saved scan's files are listed");
            main.ShowRecoverPage();
            Pump();
            main.ShowResultsPage();
            Pump();

            // A Deep scan through the screens too.
            main.ShowDrives();
            Pump();
            main.BeginScan(_drive, true);
            until = DateTime.UtcNow.AddSeconds(300);
            while (main.Step != 2 && DateTime.UtcNow < until) Pump();
            Expect(main.Step == 2, "results shown after a Deep scan");
            _log.AppendLine($"      Deep scan through the screens: {main.Results.Files.Count} files");
        }

        // ================================================================== images, saved scans, snapshots

        private static string _imagePath, _savedPath;

        private static void ImageRound()
        {
            _imagePath = Path.Combine(_temp, "kudurescue-copy.img");
            int bad;
            using (var device = VolumeDevice.Open(_drive))
                bad = DriveImages.Copy(device, _imagePath, null, CancellationToken.None);
            Expect(bad == 0, "no unreadable sectors on the test drive");
            List<FoundFile> found;
            var dest = Path.Combine(_temp, "from-image");
            using (var image = DriveImages.OpenFile(_imagePath))
            {
                var parts = DriveImages.Partitions(image);
                Expect(parts.Count == 1 && parts[0].Format == "NTFS", "the copy is one NTFS volume");
                using (var volume = Volumes.Open(image, false))
                {
                    found = QuickScan.RunToEnd(volume, "image");
                    Recovery.Run(volume, found.Where(f => Expected.ContainsKey(f.RelativePath)).ToList(), new RecoverOptions { Destination = dest });
                }
            }
            foreach (var pair in Expected)
                Expect(File.Exists(Path.Combine(dest, pair.Key)) && File.ReadAllBytes(Path.Combine(dest, pair.Key)).SequenceEqual(pair.Value), pair.Key + " recovered from the image");
        }

        /// <summary>A real .vhd made by Windows (with a partition table inside), opened as an image after it's detached.</summary>
        private static void VhdRound(string type)
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            var letter = "RSTUVWXYZQPONMLKJ".First(c => !used.Contains(c));
            var vhd = Path.Combine(_temp, "kudurescue-" + type + ".vhd");
            Diskpart($"create vdisk file=\"{vhd}\" maximum=64 type={type}", $"select vdisk file=\"{vhd}\"", "attach vdisk",
                "create partition primary", "format fs=fat32 quick label=KRVHD", $"assign letter={letter}");
            var data = Pattern(200_000, 71);
            var detached = false;
            try
            {
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!Directory.Exists(letter + ":\\") && DateTime.UtcNow < until) Thread.Sleep(250);
                var file = Path.Combine(letter + ":\\", "Inside a VHD file.bin");
                File.WriteAllBytes(file, data);
                FlushDrive(letter + ":");
                File.Delete(file);
                FlushDrive(letter + ":");
                Diskpart($"select vdisk file=\"{vhd}\"", "detach vdisk");
                detached = true;

                using (var image = DriveImages.OpenFile(vhd))
                {
                    Expect(image is VhdDevice, type + " VHD recognised");
                    var parts = DriveImages.Partitions(image);
                    Expect(parts.Count == 1 && parts[0].Format.StartsWith("FAT"), $"one FAT partition inside ({string.Join(", ", parts.Select(p => p.Title))})");
                    using (var volume = Volumes.Open(new OffsetDevice(image, parts[0].Offset, parts[0].Length, "vhd", false)))
                    {
                        var found = QuickScan.RunToEnd(volume, "vhd");
                        var f = found.FirstOrDefault(x => x.Name == "Inside a VHD file.bin");
                        Expect(f != null, "deleted file found inside the VHD");
                        var dest = Path.Combine(_temp, "from-vhd-" + type);
                        Recovery.Run(volume, new List<FoundFile> { f }, new RecoverOptions { Destination = dest });
                        Expect(File.ReadAllBytes(Path.Combine(dest, f.Name)).SequenceEqual(data), "identical");
                    }
                }
                _log.AppendLine($"      {type} VHD: partition found, deleted file recovered");
            }
            finally
            {
                if (!detached) Diskpart($"select vdisk file=\"{vhd}\"", "detach vdisk");
            }
        }

        private static void SavedRound()
        {
            _savedPath = Path.Combine(_temp, "test" + SavedScan.Extension);
            using (var device = VolumeDevice.Open(_drive))
                new SavedScan { Kind = "Quick", Title = "Quick scan of " + _drive, Drive = _drive, Fingerprint = SavedScan.FingerprintOf(device), Finished = true, Files = _found }.Save(_savedPath);
            var loaded = SavedScan.Load(_savedPath);
            Expect(loaded.Files.Count == _found.Count, "all files kept");
            var pdf = loaded.Files.First(f => f.RelativePath == @"Old folder\Inner\report.docx");
            var dest = Path.Combine(_temp, "from-saved");
            using (var device = VolumeDevice.Open(_drive))
            {
                Expect(SavedScan.FingerprintOf(device) == loaded.Fingerprint, "same drive recognised");
                using (var volume = Volumes.Open(device, false))
                    Recovery.Run(volume, new List<FoundFile> { pdf }, new RecoverOptions { Destination = dest });
            }
            Expect(File.ReadAllBytes(Path.Combine(dest, @"Old folder\Inner\report.docx")).SequenceEqual(Expected[@"Old folder\Inner\report.docx"]), "recovered from the saved scan");
        }

        private static void SnapshotRound()
        {
            var file = Path.Combine(_drive + "\\", "Snapshot test", "keep me.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "kept by Previous Versions");
            FlushDrive(_drive);
            string id;
            try { id = ShadowCopies.Create(_drive); }
            catch (Exception ex)
            {
                _log.AppendLine("      (snapshots aren't available on this machine: " + ex.Message + ")");
                return;
            }
            try
            {
                File.Delete(file);
                var snapshot = ShadowCopies.List(_drive).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                Expect(snapshot != null, "the new snapshot is listed");
                List<FoundFile> found;
                using (var v = new SnapshotVolume(snapshot.Root, _drive + "\\", snapshot.Created)) found = QuickScan.RunToEnd(v, _drive);
                var f = found.FirstOrDefault(x => x.Name == "keep me.txt");
                Expect(f != null && f.Folder == "Snapshot test", "the deleted file is found in the snapshot");
                var dest = Path.Combine(_temp, "from-snapshot");
                Recovery.Run(null, new List<FoundFile> { f }, new RecoverOptions { Destination = dest });
                Expect(File.ReadAllText(Path.Combine(dest, "Snapshot test", "keep me.txt")) == "kept by Previous Versions", "copied back from the snapshot");
                _log.AppendLine($"      snapshot of {_drive} taken {snapshot.Created.ToLocalTime():HH:mm:ss}: {found.Count} files deleted or changed since");
            }
            finally
            {
                try { ShadowCopies.Delete(id); } catch { /* ignore */ }
            }
        }

        // ================================================================== deep scan

        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        private static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] BE32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] NoFF(int n, int seed) => Pattern(n, seed).Select(b => b == 0xFF ? (byte)0x7E : b).ToArray();

        /// <summary>Real files of the main types, with valid structures, for Deep scan to find.</summary>
        private static Dictionary<string, byte[]> CarveSet()
        {
            var jpeg = Cat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, BE16(16), Encoding.ASCII.GetBytes("JFIF\0"), new byte[9],
                new byte[] { 0xFF, 0xDB }, BE16(67), NoFF(65, 51), new byte[] { 0xFF, 0xDA }, BE16(12), NoFF(10, 52),
                NoFF(400_000, 53), new byte[] { 0xFF, 0xD9 });
            byte[] Chunk(string type, byte[] data) => Cat(BE32(data.Length), Encoding.ASCII.GetBytes(type), data, new byte[4]);
            var png = Cat(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, Chunk("IHDR", new byte[13]), Chunk("IDAT", Pattern(250_000, 54)), Chunk("IEND", new byte[0]));
            var pdf = Cat(Encoding.ASCII.GetBytes("%PDF-1.7\n"), Pattern(150_000, 55).Select(b => (byte)(32 + b % 90)).ToArray(), Encoding.ASCII.GetBytes("\ntrailer << >>\n%%EOF\n"));
            var mp4 = Cat(BE32(20), Encoding.ASCII.GetBytes("ftypisom"), BE32(512), Encoding.ASCII.GetBytes("isom"), BE32(16), Encoding.ASCII.GetBytes("moov"), new byte[8],
                BE32(3_000_008), Encoding.ASCII.GetBytes("mdat"), Pattern(3_000_000, 56));
            byte[] docx;
            using (var ms = new MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    foreach (var name in new[] { "[Content_Types].xml", "word/document.xml", "word/media/image1.bin" })
                    {
                        var entry = zip.CreateEntry(name, name.EndsWith(".bin") ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Optimal);
                        using (var w = entry.Open())
                        {
                            var data = name.EndsWith(".bin") ? Pattern(200_000, 57) : Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<w:p>KUDURESCUE</w:p>", 2000)));
                            w.Write(data, 0, data.Length);
                        }
                    }
                }
                docx = ms.ToArray();
            }
            return new Dictionary<string, byte[]> { [".jpg"] = jpeg, [".png"] = png, [".pdf"] = pdf, [".mp4"] = mp4, [".docx"] = docx };
        }

        private static void DeepScanRound()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            var letter = "RSTUVWXYZQPONMLKJ".First(c => !used.Contains(c));
            var drive = letter + ":";
            var vhd = Path.Combine(_temp, "kudurescue-deep.vhdx");
            Diskpart($"create vdisk file=\"{vhd}\" maximum=200 type=expandable", $"select vdisk file=\"{vhd}\"", "attach vdisk",
                "create partition primary", "format fs=ntfs quick label=KRDEEP", $"assign letter={letter}");
            try
            {
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!Directory.Exists(drive + "\\") && DateTime.UtcNow < until) Thread.Sleep(250);
                var set = CarveSet();
                foreach (var pair in set) File.WriteAllBytes(Path.Combine(drive + "\\", "sample" + pair.Key), pair.Value);
                FlushDrive(drive);
                foreach (var pair in set) File.Delete(Path.Combine(drive + "\\", "sample" + pair.Key));
                FlushDrive(drive);

                // 1. Deleted files: every one must be found and come back identical.
                var found = DeepFind(drive, set, "after deleting");
                foreach (var ext in set.Keys) Expect(found.Contains(ext), ext + " found and identical after deleting");

                // 2. The drive is formatted (quick format: a new, empty file index). Deep scan should still find the files.
                Diskpart($"select volume {letter}", "format fs=ntfs quick label=FORMATTED");
                Thread.Sleep(2000);
                found = DeepFind(drive, set, "after formatting");
                Expect(found.Count >= 3, $"most files found after formatting, got {found.Count} of {set.Count}");
            }
            finally
            {
                Diskpart($"select vdisk file=\"{vhd}\"", "detach vdisk");
            }
        }

        /// <summary>Deep scans a drive and returns the types whose sample came back byte for byte.</summary>
        private static HashSet<string> DeepFind(string drive, Dictionary<string, byte[]> set, string when)
        {
            var ok = new HashSet<string>();
            var watch = Stopwatch.StartNew();
            var dest = Path.Combine(_temp, "deep-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var device = VolumeDevice.Open(drive);
            using (var deep = new DeepVolume(device, Volumes.Open(device, false)))
            {
                var files = QuickScan.RunToEnd(deep, drive);
                _log.AppendLine($"      Deep scan {when}: {files.Count} files in {watch.Elapsed.TotalSeconds:0.0}s, searched {deep.Free.Bytes / (1024 * 1024)} MB of {deep.FormatName}");
                Recovery.Run(deep, files, new RecoverOptions { Destination = dest, KeepFolders = false });
                foreach (var pair in set)
                {
                    var match = files.FirstOrDefault(f => f.Extension == pair.Key && f.Size == pair.Value.Length &&
                                                          File.Exists(Path.Combine(dest, f.Name)) && File.ReadAllBytes(Path.Combine(dest, f.Name)).SequenceEqual(pair.Value));
                    _log.AppendLine($"      {pair.Key}: " + (match != null ? $"found, identical ({match.Chance})" :
                        "not identical (" + string.Join(", ", files.Where(f => f.Extension == pair.Key).Select(f => $"{f.Size} bytes {f.Chance}")) + ")"));
                    if (match != null) ok.Add(pair.Key);
                }
            }
            return ok;
        }

        // ================================================================== windows

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void ShowAndClose(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            w.Close();
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }
}
