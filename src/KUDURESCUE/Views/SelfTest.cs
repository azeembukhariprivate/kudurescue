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
            using (var volume = NtfsVolume.Open(VolumeDevice.Open(_drive)))
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
