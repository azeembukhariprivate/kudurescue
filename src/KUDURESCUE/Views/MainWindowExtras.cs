using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudurescue.Controls;
using Kudurescue.Core;

namespace Kudurescue.Views
{
    /// <summary>Where a scan's files came from, so a saved scan can be opened again.</summary>
    public sealed class ScanSource
    {
        public string Drive;
        public string ImagePath;
        public long PartOffset;
        public long PartLength;
        public Snapshot Snapshot;
    }

    /// <summary>Drive images, Previous Versions snapshots, and saving and opening scans.</summary>
    public sealed partial class MainWindow
    {
        private ScanSource _source;
        private readonly Dictionary<string, List<Snapshot>> _snapshotCache = new Dictionary<string, List<Snapshot>>();

        /// <summary>Opens with this drive chosen (from the drive right-click menu).</summary>
        public void ChooseDrive(string drive)
        {
            try
            {
                var letter = VolumeDevice.DriveLetter(drive) + ":";
                var d = DriveList.FirstOrDefault(x => x.Letter == letter);
                if (d == null) return;
                _drive = d;
                ShowDrives();
            }
            catch (ArgumentException) { /* not a drive letter */ }
        }

        // ================================================================== drive images

        private void OpenImage()
        {
            var path = Dialogs.PickFile(this, "Open a drive image", DriveImages.FileFilter);
            if (path != null) OpenImage(path, null);
        }

        /// <summary>Opens a drive image, lets the user pick a partition when there are several, and scans it.</summary>
        public void OpenImage(string path, bool? deep)
        {
            IBlockDevice device;
            List<ImagePart> parts;
            try
            {
                device = DriveImages.OpenFile(path);
                parts = DriveImages.Partitions(device);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, "That image can't be opened:\n\n" + ex.Message);
                return;
            }

            ImagePart part;
            if (parts.Count == 0) part = new ImagePart { Index = 1, Offset = 0, Length = device.Length, Format = "Unknown" };
            else if (parts.Count == 1) part = parts[0];
            else
            {
                part = Choose("Choose a partition", $"{Path.GetFileName(path)} holds {parts.Count} partitions. Which one were the files on?",
                    parts, p => p.Title);
                if (part == null) { device.Dispose(); return; }
            }
            if (part.Format == "BitLocker")
            {
                device.Dispose();
                Dialogs.Error(this, "That partition is encrypted with BitLocker, so it can't be read from an image.");
                return;
            }

            var volumeDevice = part.Offset == 0 && part.Length >= device.Length ? device
                : new OffsetDevice(device, part.Offset, part.Length, $"{Path.GetFileName(path)} (partition {part.Index})", true);
            var label = Path.GetFileName(path) + (parts.Count > 1 ? $" · partition {part.Index}" : string.Empty);
            var useDeep = deep ?? (part.Format == "Unknown" || _deep);
            _source = new ScanSource { ImagePath = path, PartOffset = part.Offset, PartLength = volumeDevice == device ? 0 : part.Length };
            BeginScan(label, useDeep, volumeDevice);
        }

        /// <summary>A small dialog to pick one of several things. Returns null when cancelled.</summary>
        private T Choose<T>(string title, string text, IList<T> items, Func<T, string> describe) where T : class
        {
            if (SelfTest.Running) return items.FirstOrDefault();
            var list = new StackPanel();
            T chosen = items.FirstOrDefault();
            foreach (var item in items)
            {
                var radio = new RadioButton { GroupName = "choose", IsChecked = item == chosen, Content = Ui.Text(describe(item), 13) }.Styled("OptionCard");
                radio.Checked += (s, e) => chosen = item;
                list.Children.Add(radio);
            }
            var ok = Ui.Button("Continue", null, "PrimaryButton");
            var cancel = Ui.Button("Cancel");
            var window = Dialogs.Dialog(this, title, Ui.Column(Ui.Heading(title, 17), Ui.Subtle(text, 12.5).Margin(0, 6, 0, 12), list), 520, cancel, ok);
            ok.IsDefault = true;
            cancel.IsCancel = true;
            ok.Click += (s, e) => window.DialogResult = true;
            return window.ShowDialog() == true ? chosen : null;
        }

        /// <summary>Copies the chosen drive into an image file on another drive, so a failing drive is read only once.</summary>
        private void CopyDrive()
        {
            if (_drive == null) return;
            if (!Admin.IsAdmin)
            {
                if (Dialogs.Confirm(this, "Copying a drive reads it directly, which needs administrator rights. Restart KUDURESCUE as administrator?") &&
                    Admin.RestartElevated(new[] { "--drive", _drive.Letter }))
                    Close();
                return;
            }
            var name = $"{_drive.Letter.TrimEnd(':')} drive {DateTime.Now:yyyy-MM-dd HH.mm}.img";
            var target = Dialogs.SaveFile(this, $"Save a copy of {_drive.Letter} (on a different drive)", name, "Drive image (*.img)|*.img");
            if (target == null) return;
            if (Drives.IsOnDrive(Path.GetDirectoryName(target), _drive.Letter))
            {
                Dialogs.Error(this, $"Save the copy on a different drive from {_drive.Letter}: writing to {_drive.Letter} could overwrite the files you want back.");
                return;
            }
            var free = Drives.FreeSpace(Path.GetDirectoryName(target));
            if (free >= 0 && free < _drive.Size)
            {
                Dialogs.Error(this, $"Not enough space: the copy needs {Format.Size(_drive.Size)}, but there's {Format.Size(free)} free there.");
                return;
            }
            if (File.Exists(target)) File.Delete(target);
            RunCopy(_drive.Letter, target);
        }

        /// <summary>Copies a drive to an image with a progress window, then offers to scan the copy. Returns the copy task (the self-test waits on it).</summary>
        public Task<int> RunCopy(string drive, string target)
        {
            var cancel = new CancellationTokenSource();
            var bar = new ProgressBar { Height = 10, Maximum = 1, Margin = new Thickness(0, 14, 0, 8) };
            var status = Ui.Subtle("Starting…", 12.5, false);
            var stop = Ui.Button("Stop");
            var window = Dialogs.Dialog(this, "Copying " + drive, Ui.Column(
                Ui.Heading($"Copying {drive} to an image", 17),
                Ui.Subtle("Every sector is read once. Unreadable sectors are skipped and written as zeros, so a failing drive doesn't get stuck.", 12.5).Margin(0, 6, 0, 0),
                bar, status), 520, stop);
            stop.Click += (s, e) => cancel.Cancel();
            window.Closing += (s, e) => cancel.Cancel();

            var device = VolumeDevice.Open(drive);
            var total = Math.Max(1, device.Length);
            var last = DateTime.MinValue;
            var task = Task.Run(() =>
            {
                try
                {
                    return DriveImages.Copy(device, target, (done, bad) =>
                    {
                        if ((DateTime.UtcNow - last).TotalMilliseconds < 200) return;
                        last = DateTime.UtcNow;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            bar.Value = (double)done / total;
                            status.Text = $"{Format.Size(done)} of {Format.Size(total)}" + (bad > 0 ? $" · {bad:N0} unreadable sectors skipped" : string.Empty);
                        }));
                    }, cancel.Token);
                }
                finally
                {
                    device.Dispose();
                }
            });
            task.ContinueWith(t =>
            {
                window.Close();
                if (t.IsCanceled || t.Exception?.GetBaseException() is OperationCanceledException)
                {
                    try { File.Delete(target); } catch { /* ignore */ }
                    return;
                }
                if (t.IsFaulted)
                {
                    Dialogs.Error(this, "The copy stopped:\n\n" + t.Exception.GetBaseException().Message);
                    return;
                }
                var message = $"{drive} has been copied to {target}." + (t.Result > 0 ? $"\n\n{t.Result:N0} unreadable sectors were skipped; the drive may be failing, so keep using the copy." : string.Empty) +
                              "\n\nScan the copy now?";
                if (!SelfTest.Running && Dialogs.Confirm(this, message)) OpenImage(target, null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            if (!SelfTest.Running) window.Show();
            return task;
        }

        // ================================================================== Previous Versions

        private void FillSnapshots(StackPanel panel)
        {
            if (_drive == null || _drive.Size <= 0) return;
            var row = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            row.Children.Add(Ui.Icon("IconHistory", 20).Margin(0, 0, 12, 0).At(0, 0));
            Button browse;
            if (!Admin.IsAdmin)
            {
                row.Children.Add(Ui.Column(Ui.Text($"Previous Versions of {_drive.Letter}", 13, bold: true),
                    Ui.Subtle("Looking for snapshots needs administrator rights", 12, false)).At(0, 1));
                browse = Ui.Button("Look", "IconShield", click: (s, e) =>
                {
                    if (Admin.RestartElevated(new[] { "--drive", _drive.Letter })) Close();
                });
            }
            else
            {
                var snapshots = Snapshots(_drive.Letter);
                row.Children.Add(Ui.Column(Ui.Text($"Previous Versions of {_drive.Letter}", 13, bold: true),
                    Ui.Subtle(snapshots.Count == 0 ? "No snapshots"
                        : $"{Format.Count(snapshots.Count, "snapshot")} · newest {snapshots[0].Created.ToLocalTime():d MMM yyyy}", 12, false)).At(0, 1));
                browse = Ui.Button("Browse", null, click: (s, e) => BrowseSnapshot(snapshots));
                browse.IsEnabled = snapshots.Count > 0;
            }
            row.Children.Add(browse.At(0, 2));
            panel.Children.Add(new Border { Child = row, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0) }
                .Res(Border.BorderBrushProperty, "BorderBrush"));
        }

        private List<Snapshot> Snapshots(string drive)
        {
            if (_snapshotCache.TryGetValue(drive, out var list)) return list;
            try { list = ShadowCopies.List(drive); }
            catch (Exception) { list = new List<Snapshot>(); } // WMI not available or not allowed
            _snapshotCache[drive] = list;
            return list;
        }

        private void BrowseSnapshot(List<Snapshot> snapshots)
        {
            var snapshot = snapshots.Count == 1 ? snapshots[0]
                : Choose("Choose a snapshot", "Windows kept these copies of the drive. Pick one from before the files were deleted.", snapshots, s => s.Title);
            if (snapshot != null) ScanSnapshot(snapshot);
        }

        /// <summary>Lists files that were in the snapshot but are gone or changed now.</summary>
        public void ScanSnapshot(Snapshot snapshot)
        {
            _source = new ScanSource { Drive = snapshot.Drive, Snapshot = snapshot };
            BeginScanWith(new SnapshotVolume(snapshot.Root, snapshot.Drive + "\\", snapshot.Created), snapshot.Drive, null);
        }

        // ================================================================== saving and opening scans

        /// <summary>Saves what's been found (pausing a running Deep scan while it saves).</summary>
        internal void SaveCurrentScan()
        {
            var files = _scan != null && !_scan.IsDone ? _scan.Files : _results?.Files;
            var volume = _scan != null && !_scan.IsDone ? _scan.Volume : _results?.Volume;
            if (files == null) return;
            var wasRunning = _scan != null && _scan.State == ScanState.Running;
            if (wasRunning) _scan.Pause();
            try
            {
                var name = $"KUDURESCUE {(_source?.Drive ?? "scan").TrimEnd(':')} {DateTime.Now:yyyy-MM-dd HH.mm}{SavedScan.Extension}";
                var path = Dialogs.SaveFile(this, "Save this scan", name, SavedScan.FileFilter);
                if (path == null) return;
                BuildSaved(files, volume).Save(path);
                Dialogs.Info(this, "Saved. Open it again with \"Open saved scan\"" + (volume is DeepVolume dv && !dv.Finished ? " to see these files or carry on scanning." : "."));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, "The scan couldn't be saved:\n\n" + ex.Message);
            }
            finally
            {
                if (wasRunning) _scan.Resume();
            }
        }

        public SavedScan BuildSaved(List<FoundFile> files, IVolume volume)
        {
            var saved = new SavedScan
            {
                Kind = volume is DeepVolume ? "Deep" : volume is SnapshotVolume ? "Snapshot" : volume == null ? "RecycleBin" : "Quick",
                Title = _results?.Title ?? "Scan",
                Drive = _source?.Drive,
                ImagePath = _source?.ImagePath,
                PartOffset = _source?.PartOffset ?? 0,
                PartLength = _source?.PartLength ?? 0,
                SnapshotPath = _source?.Snapshot?.Root,
                SnapshotTaken = _source?.Snapshot?.Created ?? DateTime.MinValue,
                Fingerprint = SavedScan.FingerprintOf(volume?.Device),
                Finished = !(volume is DeepVolume d) || d.Finished,
                ResumeAt = (volume as DeepVolume)?.Position ?? 0,
                Files = files
            };
            if (saved.Kind == "RecycleBin") saved.Drive = saved.Drive ?? _drive?.Letter;
            return saved;
        }

        private void PickSavedScan()
        {
            if (_results != null && _results.IsRecovering) return;
            var path = Dialogs.PickFile(this, "Open a saved scan", SavedScan.FileFilter);
            if (path != null) OpenSavedScan(path);
        }

        /// <summary>Opens a saved scan: its files are shown straight away, and a stopped Deep scan can carry on.</summary>
        public void OpenSavedScan(string path)
        {
            SavedScan saved;
            try { saved = SavedScan.Load(path); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, ex.Message);
                return;
            }

            if (saved.Kind == "RecycleBin")
            {
                _source = new ScanSource { Drive = saved.Drive };
                ShowSaved(saved, null);
                return;
            }
            if (!Admin.IsAdmin)
            {
                if (Admin.RestartElevated(new[] { "--open", path })) Close();
                else Dialogs.Info(this, "Opening this scan reads the drive, which needs administrator rights.");
                return;
            }
            if (saved.Kind == "Snapshot")
            {
                if (string.IsNullOrEmpty(saved.SnapshotPath) || !Directory.Exists(saved.SnapshotPath))
                {
                    Dialogs.Error(this, "That Previous Versions snapshot no longer exists. Windows removes old snapshots to make room.");
                    return;
                }
                var snapshot = new Snapshot { Drive = saved.Drive, DevicePath = saved.SnapshotPath, Created = saved.SnapshotTaken };
                _source = new ScanSource { Drive = saved.Drive, Snapshot = snapshot };
                ShowSaved(saved, new SnapshotVolume(snapshot.Root, saved.Drive + "\\", saved.SnapshotTaken));
                return;
            }

            IBlockDevice device;
            try
            {
                if (!string.IsNullOrEmpty(saved.ImagePath))
                {
                    var image = DriveImages.OpenFile(saved.ImagePath);
                    device = saved.PartLength > 0 ? new OffsetDevice(image, saved.PartOffset, saved.PartLength, Path.GetFileName(saved.ImagePath), true) : image;
                }
                else device = VolumeDevice.Open(saved.Drive);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidDataException)
            {
                Dialogs.Error(this, $"The drive or image this scan was made from can't be opened ({saved.Drive ?? saved.ImagePath}):\n\n{ex.Message}");
                return;
            }
            if (saved.Fingerprint != null && SavedScan.FingerprintOf(device) != saved.Fingerprint &&
                !Dialogs.Confirm(this, "This drive looks different from when the scan was saved (it may have been formatted, or it's another drive). " +
                                       "Recovered files could be wrong. Open the scan anyway?"))
            {
                device.Dispose();
                return;
            }

            IVolume volume;
            try { volume = saved.Kind == "Deep" ? OpenDeep(device) : Volumes.Open(device); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            {
                Dialogs.Error(this, "The drive can't be read as it was when the scan was saved:\n\n" + ex.Message);
                return;
            }
            _source = new ScanSource { Drive = saved.Drive, ImagePath = saved.ImagePath, PartOffset = saved.PartOffset, PartLength = saved.PartLength };
            ShowSaved(saved, volume);
        }

        private void ShowSaved(SavedScan saved, IVolume volume)
        {
            var label = saved.Drive ?? Path.GetFileName(saved.ImagePath ?? string.Empty);
            if (_scan != null) { _timer?.Stop(); _scan.Stop(); _scan.Volume?.Dispose(); _scan = null; }
            if (volume is DeepVolume deep)
            {
                deep.Seed(saved.Files);
                deep.ResumeAt = saved.ResumeAt;
            }
            var title = saved.Title + $" (saved {saved.Saved.ToLocalTime():d MMM yyyy, HH:mm})";
            ShowResults(saved.Files, volume, title, saved.Kind == "RecycleBin");
            if (saved.CanResume && volume is DeepVolume dv)
            {
                _results.Resume = () => BeginScanWith(dv, label, _results.Files);
                _results.RefreshFooter();
            }
        }
    }
}
