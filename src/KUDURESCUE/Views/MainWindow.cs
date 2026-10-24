using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Kudurescue.Controls;
using Kudurescue.Core;

namespace Kudurescue.Views
{
    /// <summary>The main window: four steps (choose a drive, scan, pick files, recover) in one window.</summary>
    public sealed class MainWindow : Window
    {
        private static readonly string[] StepNames = { "Choose a drive", "Scan", "Pick files", "Recover" };

        private readonly StackPanel _steps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly Grid _footer = new Grid();
        private readonly Border _footerBar;
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private int _step;

        // Choose a drive
        private List<DriveDetails> _drives = new List<DriveDetails>();
        private DriveDetails _drive;
        private readonly Dictionary<string, List<FoundFile>> _binCache = new Dictionary<string, List<FoundFile>>();

        // Scan
        private QuickScan _scan;
        private DispatcherTimer _timer;

        // Pick files and recover
        private ResultsView _results;

        public MainWindow()
        {
            Width = 1120;
            Height = 740;
            MinWidth = 920;
            MinHeight = 620;
            Dialogs.Prepare(this, "KUDURESCUE");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            header.Children.Add(_steps.At(0, 0));
            _update.Visibility = Visibility.Collapsed;
            header.Children.Add(_update.Margin(0, 0, 14, 0).At(0, 2));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this).ShowDialog()).At(0, 3));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 4));
            root.Children.Add(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));

            root.Children.Add(_page.At(1));

            _footerBar = new Border { Child = _footer, Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush");
            root.Children.Add(_footerBar.At(2));
            Content = root;

            ShowDrives();
            Loaded += async (s, e) => await CheckForUpdates();
            Closing += (s, e) =>
            {
                if (_results != null && _results.IsRecovering &&
                    !Dialogs.Confirm(this, "Files are still being recovered. Stop and close KUDURESCUE?"))
                {
                    e.Cancel = true;
                    return;
                }
                _results?.Cancel();
                _scan?.Stop();
            };
            Closed += (s, e) => _scan?.Volume.Dispose();
        }

        public int Step => _step;
        public ResultsView Results => _results;
        public QuickScan CurrentScan => _scan;
        public IReadOnlyList<DriveDetails> DriveList => _drives;

        // ================================================================== frame

        private void SetStep(int step)
        {
            _step = step;
            _steps.Children.Clear();
            for (var i = 0; i < StepNames.Length; i++)
            {
                if (i > 0) _steps.Children.Add(Ui.Text("›", 14, "OffDot").Margin(8, 0, 8, 0));
                var done = i < step;
                var current = i == step;
                var number = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                    Child = done ? (UIElement)Ui.Icon("IconCheck", 12).Res(TextElement.ForegroundProperty, "AccentText")
                                 : new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                }.Res(Border.BackgroundProperty, done ? "OkText" : current ? "Accent" : "NeutralChipBg");
                ((FrameworkElement)number.Child).HorizontalAlignment = HorizontalAlignment.Center;
                if (!done) ((FrameworkElement)number.Child).SetResourceReference(TextElement.ForegroundProperty, current ? "AccentText" : "SubtleText");

                var label = Ui.Text(StepNames[i], 13, current ? "LinkText" : done ? "Text" : "SubtleText", bold: current || done);
                var pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 10, 4), Child = Ui.Row(number, label.Margin(7, 0, 0, 0)) };
                if (current) pill.Res(Border.BackgroundProperty, "SelectionBg");
                if (i == 0 && step > 0)
                {
                    // Going back to the start: a new scan or a different drive.
                    pill.Cursor = System.Windows.Input.Cursors.Hand;
                    pill.ToolTip = "Start again with another drive";
                    pill.MouseLeftButtonUp += (s, e) => StartAgain();
                }
                _steps.Children.Add(pill);
            }
        }

        /// <summary>Fills the bottom bar: something on the left, buttons on the right.</summary>
        internal void SetFooter(UIElement left, params Button[] buttons)
        {
            _footer.Children.Clear();
            _footer.ColumnDefinitions.Clear();
            _footer.ColumnDefinitions.Add(new ColumnDefinition());
            if (left != null) _footer.Children.Add(left.At(0, 0));
            for (var i = 0; i < buttons.Length; i++)
            {
                _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _footer.Children.Add(buttons[i].Margin(8, 0, 0, 0).At(0, i + 1));
            }
        }

        private static ScrollViewer Scroll(UIElement content) =>
            new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };

        private void StartAgain()
        {
            if (_results != null && _results.IsRecovering) return;
            if (_scan != null && !_scan.IsDone)
            {
                if (!Dialogs.Confirm(this, "Stop the scan and start again?")) return;
                _scan.Stop();
            }
            _timer?.Stop();
            _scan?.Volume.Dispose();
            _scan = null;
            _results = null;
            ShowDrives();
        }

        // ================================================================== 1. choose a drive

        internal void ShowDrives()
        {
            SetStep(0);
            _drives = Drives.List();
            var selected = _drive == null ? null : _drives.FirstOrDefault(d => d.Letter == _drive.Letter);
            _drive = selected ?? _drives.FirstOrDefault(d => d.IsNtfs && !d.IsSystem && !d.TrimWarning) ?? _drives.FirstOrDefault(d => d.IsNtfs) ?? _drives.FirstOrDefault();

            var list = new StackPanel();
            foreach (var d in _drives) list.Children.Add(DriveCard(d));
            if (_drives.Count == 0) list.Children.Add(Ui.Subtle("No drives were found.", 13));

            var left = Ui.Card(Ui.Column(
                Ui.Text("Where were the files?", 18, bold: true),
                Ui.Subtle("Choose the drive the files were deleted from. KUDURESCUE only reads it; nothing on it is changed.", 12.5).Margin(0, 3, 0, 14),
                list), 18);

            var bin = new StackPanel();
            var easy = Ui.Card(Ui.Column(Ui.Heading("Check this first"),
                Ui.Subtle("Files in the Recycle Bin come back perfectly, with no scan.", 12.5).Margin(0, 3, 0, 12), bin), 18);
            FillRecycleBin(bin);

            var quick = new RadioButton { IsChecked = true, GroupName = "depth" }.Styled("OptionCard");
            quick.Content = Ui.Column(Ui.Row(Ui.Text("Quick scan", 13.5, bold: true), Ui.Chip("Recommended").Margin(8, 0, 0, 0)),
                Ui.Subtle("Recently deleted files with their names and folders. Usually a minute or two.", 12).Margin(0, 4, 0, 0));
            var deep = new RadioButton { IsEnabled = false, GroupName = "depth" }.Styled("OptionCard");
            deep.Content = Ui.Column(Ui.Row(Ui.Text("Deep scan", 13.5, bold: true), Ui.Chip("Coming in version 1.2", Tone.Neutral).Margin(8, 0, 0, 0)),
                Ui.Subtle("For formatted or damaged drives: finds photos, videos, documents and more by their contents.", 12).Margin(0, 4, 0, 0));
            var depth = Ui.Card(Ui.Column(Ui.Heading("How deep?").Margin(0, 0, 0, 12), quick, deep), 18);

            var grid = Ui.Columns(Ui.Star(1.35), Ui.Px(16), Ui.Star());
            grid.Margin = new Thickness(16);
            grid.Children.Add(left.At(0, 0));
            var right = Ui.Column(easy, depth.Margin(0, 16, 0, 0));
            grid.Children.Add(right.At(0, 2));
            _page.Content = Scroll(grid);
            RefreshDriveFooter();
        }

        private UIElement DriveCard(DriveDetails d)
        {
            var radio = new RadioButton { GroupName = "drive", IsChecked = d == _drive }.Styled("OptionCard");
            radio.Checked += (s, e) =>
            {
                _drive = d;
                ShowDrivesKeepingScroll();
            };

            var tile = new Border { Width = 40, Height = 32, CornerRadius = new CornerRadius(5), Child = Ui.Text(d.Letter, 13, "SubtleText", bold: true) }
                .Res(Border.BackgroundProperty, "NeutralChipBg");
            ((TextBlock)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;

            var info = new StackPanel();
            var title = Ui.Row(Ui.Text(d.Title, 13.5, bold: true),
                Ui.Subtle($"   {d.KindText} · {d.Format}" + (d.Size > 0 ? " · " + Format.Size(d.Size) : string.Empty), 12, false));
            info.Children.Add(title);
            if (d.Size > 0)
            {
                var bar = new ProgressBar { Height = 6, Maximum = 1, Value = 1 - (double)d.Free / d.Size, Margin = new Thickness(0, 7, 0, 7) }
                    .Res(Control.ForegroundProperty, "OffDot");
                info.Children.Add(bar);
            }
            var chips = new WrapPanel();
            foreach (var chip in DriveChips(d)) chips.Children.Add(chip.Margin(0, 2, 6, 0));
            info.Children.Add(chips);

            var grid = Ui.Columns(Ui.Auto, Ui.Star());
            grid.Children.Add(tile.Margin(0, 0, 14, 0).At(0, 0));
            grid.Children.Add(info.At(0, 1));
            radio.Content = grid;
            return radio;
        }

        private static IEnumerable<FrameworkElement> DriveChips(DriveDetails d)
        {
            if (d.Size <= 0) { yield return Ui.Chip(d.Format ?? "Not ready", Tone.Bad); yield break; }
            if (d.TrimWarning) yield return Ui.Chip("SSD with TRIM: deleted files are usually wiped within minutes", Tone.Warn, "IconWarn");
            else if (d.IsNtfs) yield return Ui.Chip("Good chance", Tone.Ok);
            if (d.IsFat) yield return Ui.Chip($"{d.Format} support is coming in version 1.1", Tone.Neutral);
            else if (!d.IsNtfs) yield return Ui.Chip($"{d.Format} drives aren't supported", Tone.Neutral);
            if (d.IsSystem) yield return Ui.Chip("Windows drive: use the PC as little as possible", Tone.Neutral);
        }

        private void ShowDrivesKeepingScroll()
        {
            var offset = (_page.Content as ScrollViewer)?.VerticalOffset ?? 0;
            ShowDrives();
            Dispatcher.BeginInvoke(new Action(() => (_page.Content as ScrollViewer)?.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
        }

        private void FillRecycleBin(StackPanel panel)
        {
            panel.Children.Clear();
            if (_drive == null) return;
            if (!_binCache.TryGetValue(_drive.Letter, out var items))
            {
                try { items = RecycleBin.List(_drive.Letter); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { items = new List<FoundFile>(); }
                _binCache[_drive.Letter] = items;
            }
            var row = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            row.Children.Add(Ui.Icon("IconTrash", 20).Margin(0, 0, 12, 0).At(0, 0));
            var text = items.Count == 0
                ? Ui.Column(Ui.Text($"Recycle Bin on {_drive.Letter}", 13, bold: true), Ui.Subtle("Empty" + (Admin.IsAdmin ? string.Empty : " (for your account)"), 12, false))
                : Ui.Column(Ui.Text($"Recycle Bin on {_drive.Letter}", 13, bold: true), Ui.Subtle($"{Format.Count(items.Count, "item")} · {Format.Size(items.Sum(i => i.Size))}", 12, false));
            row.Children.Add(text.At(0, 1));
            var show = Ui.Button("Show", null, click: (s, e) => ShowResults(items, null, $"Recycle Bin on {_drive.Letter}", true));
            show.IsEnabled = items.Count > 0;
            row.Children.Add(show.At(0, 2));
            panel.Children.Add(new Border { Child = row, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
                .Res(Border.BorderBrushProperty, "BorderBrush"));
        }

        private void RefreshDriveFooter()
        {
            var start = Ui.Button(_drive == null ? "Start Quick scan" : $"Start Quick scan on {_drive.Letter}", Admin.IsAdmin ? null : "IconShield", "PrimaryButton", (s, e) => StartScan());
            var reason = ScanBlocker(_drive);
            start.IsEnabled = reason == null;
            if (reason != null) start.ToolTip = reason;
            else if (!Admin.IsAdmin) start.ToolTip = "Reading a drive directly needs administrator rights. Windows will ask you.";

            var warning = _drive == null ? null : Ui.Banner("IconWarn",
                Ui.Text($"Stop saving anything to {_drive.Letter} until you've recovered your files. New files can overwrite them.", 12.5, "Text", wrap: true),
                "WarnBg", "WarnText");
            if (warning != null) warning.HorizontalAlignment = HorizontalAlignment.Left;
            SetFooter(warning, start);
        }

        private static string ScanBlocker(DriveDetails d)
        {
            if (d == null) return "Choose a drive first.";
            if (d.Size <= 0) return "This drive isn't ready. If it's locked with BitLocker, unlock it in File Explorer first.";
            if (d.IsFat) return $"{d.Format} drives (memory cards and most USB sticks) are supported from version 1.1.";
            if (!d.IsNtfs) return $"{d.Format} drives aren't supported.";
            return null;
        }

        // ================================================================== 2. scan

        private void StartScan()
        {
            if (_drive == null || ScanBlocker(_drive) != null) return;
            if (!Admin.IsAdmin)
            {
                if (Admin.RestartElevated(new[] { "--scan", _drive.Letter })) Close();
                else Dialogs.Info(this, "KUDURESCUE needs administrator rights to read the drive directly. Press Start again and choose Yes when Windows asks.");
                return;
            }
            BeginScan(_drive.Letter);
        }

        /// <summary>After restarting as administrator with --scan D:, start straight away.</summary>
        public void StartScanFromCommandLine(string drive)
        {
            try
            {
                var letter = VolumeDevice.DriveLetter(drive) + ":";
                _drive = _drives.FirstOrDefault(d => d.Letter == letter) ?? _drive;
                ShowDrives();
                if (_drive?.Letter == letter && ScanBlocker(_drive) == null && Admin.IsAdmin) BeginScan(letter);
            }
            catch (ArgumentException) { /* not a drive letter: just show the start page */ }
        }

        /// <summary>Opens the drive and starts a Quick scan. Also used by the self-test.</summary>
        public void BeginScan(string letter, IBlockDevice device = null)
        {
            NtfsVolume volume;
            try
            {
                volume = NtfsVolume.Open(device ?? VolumeDevice.Open(letter));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, $"{letter} can't be scanned:\n\n{ex.Message}");
                return;
            }

            _scan = new QuickScan(volume, letter);
            SetStep(1);
            ShowScanPage();
            _ = _scan.RunAsync();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => UpdateScan();
            _timer.Start();
        }

        private TextBlock _scanTitle, _percent, _elapsed, _left, _records, _total, _chanceLine;
        private ProgressBar _scanBar;
        private StackPanel _justFound, _kinds;
        private Grid _chanceBar;
        private Button _pause;

        private void ShowScanPage()
        {
            var drive = _drives.FirstOrDefault(d => d.Letter == _scan.Drive);
            _scanTitle = Ui.Text($"Scanning {_scan.Drive}" + (drive != null && !string.IsNullOrWhiteSpace(drive.Label) ? $" ({drive.Label})" : string.Empty), 18, bold: true);
            _percent = Ui.Text("0%", 28, "LinkText", bold: true);
            _scanBar = new ProgressBar { Height = 10, Maximum = 1, Margin = new Thickness(0, 14, 0, 12) };
            _elapsed = Ui.Text(string.Empty, 12.5);
            _left = Ui.Text(string.Empty, 12.5);
            _records = Ui.Text(string.Empty, 12.5);
            _justFound = new StackPanel();

            var head = Ui.Columns(Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.Column(_scanTitle, Ui.Subtle($"Quick scan · reading the drive's file index · nothing is written to {_scan.Drive}", 12.5).Margin(0, 3, 0, 0)).At(0, 0));
            head.Children.Add(_percent.At(0, 1));

            var times = Ui.Row(_elapsed, _left.Margin(24, 0, 0, 0), _records.Margin(24, 0, 0, 0));
            var found = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 0),
                Child = Ui.Column(new Border { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Text("Just found", 12, "SubtleText", bold: true) }
                    .Res(Border.BorderBrushProperty, "BorderBrush").Res(Border.BackgroundProperty, "SurfaceAltBg"), _justFound)
            }.Res(Border.BorderBrushProperty, "BorderBrush");
            var left = Ui.Card(Ui.Column(head, _scanBar, times, found), 18);

            _total = Ui.Text("0", 30, bold: true);
            _kinds = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var totals = Ui.Card(Ui.Column(Ui.Row(_total, Ui.Subtle("  deleted files found", 13, false)), _kinds), 18);

            _chanceBar = new Grid { Height = 12, Margin = new Thickness(0, 12, 0, 10), ClipToBounds = true };
            _chanceLine = Ui.Text(string.Empty, 12);
            var chances = Ui.Card(Ui.Column(Ui.Heading("Recovery chance so far"), _chanceBar, _chanceLine), 18);

            var grid = Ui.Columns(Ui.Star(1.5), Ui.Px(16), Ui.Star());
            grid.Margin = new Thickness(16);
            grid.Children.Add(left.At(0, 0));
            grid.Children.Add(Ui.Column(totals, chances.Margin(0, 16, 0, 0)).At(0, 2));
            _page.Content = Scroll(grid);

            _pause = Ui.Button("Pause", "IconPause", click: (s, e) =>
            {
                if (_scan.State == ScanState.Paused) _scan.Resume(); else _scan.Pause();
            });
            var stop = Ui.Button("Stop and show files", "IconStop", click: (s, e) => _scan.Stop());
            SetFooter(Ui.Subtle("You can stop at any time to look at what's been found so far.", 12.5, false), _pause, stop);
            UpdateScan();
        }

        private void UpdateScan()
        {
            if (_scan == null) return;
            var fraction = _scan.Fraction;
            _percent.Text = $"{fraction * 100:0}%";
            _scanBar.Value = fraction;
            _elapsed.Inlines.Clear();
            _elapsed.Inlines.Add(new Run("Elapsed ") { Foreground = Ui.Brush("SubtleText") });
            _elapsed.Inlines.Add(new Run(Format.Duration(_scan.Elapsed)) { FontWeight = FontWeights.SemiBold });
            _left.Inlines.Clear();
            if (_scan.Remaining.HasValue)
            {
                _left.Inlines.Add(new Run("Left about ") { Foreground = Ui.Brush("SubtleText") });
                _left.Inlines.Add(new Run(Format.Duration(_scan.Remaining.Value)) { FontWeight = FontWeights.SemiBold });
            }
            else if (_scan.State == ScanState.Paused) _left.Inlines.Add(new Run("Paused") { FontWeight = FontWeights.SemiBold });
            _records.Inlines.Clear();
            _records.Inlines.Add(new Run("Index entries ") { Foreground = Ui.Brush("SubtleText") });
            _records.Inlines.Add(new Run($"{_scan.RecordsDone:N0} of {_scan.RecordsTotal:N0}") { FontWeight = FontWeights.SemiBold });
            _pause.Content = Ui.Label(_scan.State == ScanState.Paused ? "IconPlay" : "IconPause", _scan.State == ScanState.Paused ? "Resume" : "Pause");

            var files = _scan.Files;
            _total.Text = files.Count.ToString("N0");
            _kinds.Children.Clear();
            foreach (var g in files.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            {
                var row = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Px(80));
                row.Margin = new Thickness(0, 5, 0, 5);
                row.Children.Add(Ui.Text(FileKinds.Label(g.Key), 13).At(0, 0));
                row.Children.Add(Ui.Text(g.Count().ToString("N0"), 13, bold: true).At(0, 1));
                var size = Ui.Mono(Format.Size(g.Sum(f => f.Size)), 12, "SubtleText");
                size.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(size.At(0, 2));
                _kinds.Children.Add(row);
            }

            int excellent = files.Count(f => f.Chance == Chance.Excellent), partial = files.Count(f => f.Chance == Chance.Partial);
            var lost = files.Count - excellent - partial;
            _chanceBar.ColumnDefinitions.Clear();
            _chanceBar.Children.Clear();
            var parts = new[] { (excellent, "OkText"), (partial, "WarnText"), (lost, "BadText") };
            for (var i = 0; i < parts.Length; i++)
            {
                _chanceBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(parts[i].Item1, files.Count == 0 && i == 0 ? 1 : 0), GridUnitType.Star) });
                _chanceBar.Children.Add(new Border().Res(Border.BackgroundProperty, files.Count == 0 ? "NeutralChipBg" : parts[i].Item2).At(0, i));
            }
            _chanceLine.Text = $"Excellent {excellent:N0}   ·   Partial {partial:N0}   ·   Overwritten {lost:N0}";

            _justFound.Children.Clear();
            foreach (var f in _scan.Recent.Take(8))
            {
                var row = Ui.Columns(Ui.Star(1.1), Ui.Star(1.3), Ui.Px(80), Ui.Px(120));
                row.Margin = new Thickness(12, 7, 12, 7);
                row.Children.Add(Ui.Text(f.Name, 13, bold: true).At(0, 0));
                row.Children.Add(Ui.Mono(_scan.Drive + "\\" + f.Folder, 11.5, "SubtleText").Margin(8, 0, 8, 0).At(0, 1));
                var size = Ui.Mono(f.SizeText, 12);
                size.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(size.At(0, 2));
                row.Children.Add(ChanceChip(f).Margin(12, 0, 0, 0).At(0, 3));
                _justFound.Children.Add(row);
            }
            if (_justFound.Children.Count == 0) _justFound.Children.Add(Ui.Subtle("Nothing yet…", 12.5).Margin(12, 10, 12, 10));

            if (!_scan.IsDone) return;
            _timer.Stop();
            if (_scan.State == ScanState.Failed)
            {
                Dialogs.Error(this, $"The scan of {_scan.Drive} stopped:\n\n{_scan.Error}");
                if (files.Count == 0) { StartAgain(); return; }
            }
            var title = $"Quick scan of {_scan.Drive}" + (_scan.State == ScanState.Stopped ? $" (stopped at {fraction * 100:0}%)" : string.Empty);
            ShowResults(files, _scan.Volume, title, false);
        }

        internal static Border ChanceChip(FoundFile f)
        {
            var tone = f.Chance == Chance.Excellent ? Tone.Ok : f.Chance == Chance.Partial ? Tone.Warn : Tone.Bad;
            return Ui.Chip(f.ChanceText, tone);
        }

        // ================================================================== 3 and 4. pick files, recover

        internal void ShowResults(List<FoundFile> files, NtfsVolume volume, string title, bool recycleBin)
        {
            var drive = _drive?.Letter ?? _scan?.Drive ?? "C:";
            if (_scan != null && volume == _scan.Volume) drive = _scan.Drive;
            _results = new ResultsView(this, files, volume, drive, title, recycleBin);
            ShowResultsPage();
        }

        internal void ShowResultsPage()
        {
            SetStep(2);
            _page.Content = _results.BuildPage();
            _results.RefreshFooter();
        }

        internal void ShowRecoverPage()
        {
            SetStep(3);
            _page.Content = _results.BuildRecoverPage();
        }

        /// <summary>Recovery progress and the finished summary replace the recover page's content.</summary>
        internal void ShowRecoverContent(UIElement content)
        {
            SetStep(3);
            _page.Content = content;
        }

        // ================================================================== updates

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || info.Latest.ToString(3) == Settings.SkippedVersion) return;
                _update.Content = $"Version {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // Offline or GitHub not reachable: try again next time.
            }
        }
    }
}
