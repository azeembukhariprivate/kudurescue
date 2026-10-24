using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kudurescue.Controls;
using Kudurescue.Core;

namespace Kudurescue.Views
{
    /// <summary>Steps 3 and 4: pick the files to recover (folders, filters, preview), then recover them to another drive.</summary>
    public sealed class ResultsView
    {
        private const string RowTemplate = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid Height='32'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='40'/><ColumnDefinition Width='1.3*'/><ColumnDefinition Width='*'/><ColumnDefinition Width='90'/><ColumnDefinition Width='100'/><ColumnDefinition Width='132'/>
    </Grid.ColumnDefinitions>
    <CheckBox IsChecked='{Binding IsChecked, Mode=TwoWay}' IsEnabled='{Binding CanRecover}' VerticalAlignment='Center' HorizontalAlignment='Center' Focusable='False'/>
    <TextBlock Grid.Column='1' Text='{Binding Name}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' FontWeight='SemiBold' Margin='0,0,10,0'/>
    <TextBlock Grid.Column='2' Text='{Binding FolderText}' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Foreground='{DynamicResource SubtleText}' FontSize='12' Margin='0,0,10,0'/>
    <TextBlock Grid.Column='3' Text='{Binding SizeText}' VerticalAlignment='Center' HorizontalAlignment='Right' FontFamily='{DynamicResource MonoFont}' FontSize='12' Margin='0,0,14,0'/>
    <TextBlock Grid.Column='4' Text='{Binding ModifiedText}' VerticalAlignment='Center' Foreground='{DynamicResource SubtleText}' FontSize='12'/>
    <Border x:Name='Chip' Grid.Column='5' CornerRadius='10' Padding='8,2,8,3' HorizontalAlignment='Left' VerticalAlignment='Center' Background='{DynamicResource BadBg}'>
      <TextBlock x:Name='ChipText' Text='{Binding ChanceText}' FontSize='11.5' FontWeight='SemiBold' Foreground='{DynamicResource BadText}'/>
    </Border>
  </Grid>
  <DataTemplate.Triggers>
    <DataTrigger Binding='{Binding Tone}' Value='ok'>
      <Setter TargetName='Chip' Property='Background' Value='{DynamicResource OkBg}'/>
      <Setter TargetName='ChipText' Property='Foreground' Value='{DynamicResource OkText}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Tone}' Value='warn'>
      <Setter TargetName='Chip' Property='Background' Value='{DynamicResource WarnBg}'/>
      <Setter TargetName='ChipText' Property='Foreground' Value='{DynamicResource WarnText}'/>
    </DataTrigger>
  </DataTemplate.Triggers>
</DataTemplate>";

        private static readonly HashSet<string> ImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".webp" };
        private static readonly HashSet<string> TextTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".tsv", ".log", ".md", ".ini", ".cfg", ".json", ".xml", ".html", ".htm", ".yaml", ".yml", ".cs", ".py", ".js", ".ts", ".css", ".sql", ".bat", ".ps1", ".vcf", ".ics", ".eml", ".srt" };

        private readonly MainWindow _main;
        private readonly List<FoundFile> _files;
        private readonly NtfsVolume _volume;
        private readonly string _drive;
        private readonly string _title;
        private readonly bool _recycleBin;

        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Single }.Styled("TableList");
        private readonly ListBox _tree = new ListBox().Styled("TableList");
        private readonly WrapPanel _kindChips = new WrapPanel();
        private readonly WrapPanel _chanceChips = new WrapPanel();
        private readonly TextBox _search = new TextBox { MinHeight = 32 };
        private readonly ContentControl _preview = new ContentControl { Focusable = false };
        private readonly TextBlock _shownText = Ui.Subtle(string.Empty, 12, false);
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        private List<FoundFile> _shown = new List<FoundFile>();
        private string _folder;       // null = all folders; otherwise this folder and those inside it
        private FileKind? _kind;
        private Chance? _chance;
        private string _sort = "folder";
        private bool _descending;
        private int _previewVersion;

        // Recover
        private readonly TextBox _destination = new TextBox { MinHeight = 36, FontSize = 13 };
        private readonly CheckBox _keepFolders = new CheckBox { Content = "Keep the original folders" };
        private readonly CheckBox _partial = new CheckBox { Content = "Recover partial files too (marked \"(PARTIAL)\" in the name)" };
        private readonly StackPanel _checks = new StackPanel();
        private Button _recoverButton;
        private CancellationTokenSource _cancel;

        public ResultsView(MainWindow main, List<FoundFile> files, NtfsVolume volume, string drive, string title, bool recycleBin)
        {
            _main = main;
            _files = files;
            _volume = volume;
            _drive = drive;
            _title = title;
            _recycleBin = recycleBin;
            foreach (var f in _files) f.IsChecked = false;

            _list.ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);
            _list.SelectionChanged += (s, e) => ShowPreview(_list.SelectedItem as FoundFile);
            _list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Space && _list.SelectedItem is FoundFile f && f.CanRecover)
                {
                    f.IsChecked = !f.IsChecked;
                    e.Handled = true;
                }
            };
            _tree.SelectionChanged += (s, e) =>
            {
                if (_tree.SelectedItem is ListBoxItem item && item.Tag is string path)
                {
                    _folder = path.Length == 0 ? null : path;
                    Fill();
                }
            };
            _search.TextChanged += (s, e) =>
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            };
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                Fill();
            };
            FoundFile.CheckedChanged += (s, e) =>
            {
                if (!_bulk && _main.Results == this) RefreshFooter();
            };
            _destination.TextChanged += (s, e) => Validate();
            _keepFolders.Checked += (s, e) => Settings.KeepFolders = true;
            _keepFolders.Unchecked += (s, e) => Settings.KeepFolders = false;
            _partial.Checked += (s, e) => { Settings.RecoverPartial = true; Validate(); };
            _partial.Unchecked += (s, e) => { Settings.RecoverPartial = false; Validate(); };
        }

        private bool _bulk;

        /// <summary>Ticks or unticks many files, updating the totals once at the end.</summary>
        public void TickAll(IEnumerable<FoundFile> files, bool value)
        {
            _bulk = true;
            try { foreach (var f in files.Where(x => x.CanRecover || !value)) f.IsChecked = value; }
            finally { _bulk = false; }
            RefreshFooter();
        }

        public List<FoundFile> Files => _files;
        public List<FoundFile> Shown => _shown;
        public bool IsRecovering => _cancel != null;
        public RecoverResult LastResult { get; private set; }
        public string Destination { get => _destination.Text.Trim().Trim('"'); set => _destination.Text = value; }
        public int Ticked => _files.Count(f => f.IsChecked);

        public void Cancel() => _cancel?.Cancel();

        /// <summary>What's wrong with the chosen destination, or null when it can be used.</summary>
        public string CheckDestination() => Validate();

        // ================================================================== step 3: pick files

        private UIElement _page;
        private readonly Grid _header = new Grid { Margin = new Thickness(0, 4, 0, 0) };

        /// <summary>The page is built once (its controls can only have one parent) and refreshed each time it's shown.</summary>
        public UIElement BuildPage()
        {
            if (_page == null) _page = CreatePage();
            BuildTree();
            BuildChips();
            BuildHeader();
            Fill();
            return _page;
        }

        private void BuildHeader()
        {
            _header.Children.Clear();
            _header.ColumnDefinitions.Clear();
            foreach (var w in new[] { Ui.Px(40), Ui.Star(1.3), Ui.Star(), Ui.Px(90), Ui.Px(100), Ui.Px(132) })
                _header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            _header.Children.Add(SortHeader("Name", "name").At(0, 1));
            _header.Children.Add(SortHeader("Folder", "folder").At(0, 2));
            var size = SortHeader("Size", "size");
            size.HorizontalAlignment = HorizontalAlignment.Right;
            _header.Children.Add(size.Margin(0, 0, 14, 0).At(0, 3));
            _header.Children.Add(SortHeader("Modified", "modified").At(0, 4));
            _header.Children.Add(SortHeader("Recovery chance", "chance").At(0, 5));
        }

        private UIElement CreatePage()
        {

            var treeCard = Ui.Card(DockTop(Ui.Text("Original folders", 13, bold: true).Margin(8, 0, 0, 8), _tree), 10);

            var searchRow = Ui.Columns(Ui.Auto, Ui.Star());
            searchRow.Children.Add(Ui.Icon("IconSearch", 16).Res(System.Windows.Documents.TextElement.ForegroundProperty, "SubtleText").Margin(2, 0, 10, 0).At(0, 0));
            searchRow.Children.Add(_search.At(0, 1));
            _search.ToolTip = "Search names";

            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition());
            table.Children.Add(new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1) }.Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            table.Children.Add(_list.At(1));

            var top = Ui.Column(
                Ui.Columns(Ui.Star(), Ui.Auto).With(g =>
                {
                    g.Children.Add(Ui.Text(_title, 14, bold: true).At(0, 0));
                    g.Children.Add(_shownText.At(0, 1));
                }),
                searchRow.Margin(0, 10, 0, 8), _kindChips, _chanceChips.Margin(0, 4, 0, 4));
            var center = Ui.Card(DockTop(top, table), 14);

            var grid = Ui.Columns(Ui.Px(230), Ui.Px(14), Ui.Star(), Ui.Px(14), Ui.Px(260));
            grid.Margin = new Thickness(16);
            grid.Children.Add(treeCard.At(0, 0));
            grid.Children.Add(center.At(0, 2));
            grid.Children.Add(Ui.Card(_preview, 14).At(0, 4));
            return grid;
        }

        private static DockPanel DockTop(UIElement top, UIElement fill)
        {
            var d = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            d.Children.Add(top);
            d.Children.Add(fill);
            return d;
        }

        private Button SortHeader(string text, string key)
        {
            var arrow = _sort == key ? (_descending ? " ↓" : " ↑") : string.Empty;
            var b = new Button { Content = text + arrow, FontSize = 12, Padding = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left }.Styled("LinkButton");
            b.Res(Control.ForegroundProperty, "SubtleText");
            b.FontWeight = FontWeights.SemiBold;
            b.Click += (s, e) =>
            {
                _descending = _sort == key ? !_descending : key == "size" || key == "modified";
                _sort = key;
                _main.ShowResultsPage();
            };
            return b;
        }

        private void BuildTree()
        {
            // Count files per folder, including everything inside.
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = _files.Count };
            foreach (var f in _files)
            {
                var path = string.Empty;
                foreach (var part in f.Folder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    path = path.Length == 0 ? part : path + "\\" + part;
                    counts.TryGetValue(path, out var n);
                    counts[path] = n + 1;
                }
            }
            var children = counts.Keys.Where(k => k.Length > 0)
                .GroupBy(k => k.Contains("\\") ? k.Substring(0, k.LastIndexOf('\\')) : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(k => k.Equals(QuickScan.LostFolders, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

            _tree.Items.Clear();
            void Add(string path, int depth)
            {
                var hasChildren = children.ContainsKey(path);
                var expanded = _expanded.Contains(path);
                var name = path.Length == 0 ? (_recycleBin ? "Recycle Bin" : _drive) : path.Substring(path.LastIndexOf('\\') + 1);

                var toggle = new TextBlock { Text = hasChildren ? (expanded ? "▾" : "▸") : string.Empty, Width = 14, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                toggle.MouseLeftButtonDown += (s, e) =>
                {
                    if (!hasChildren) return;
                    if (!_expanded.Remove(path)) _expanded.Add(path);
                    var offset = FindScroll(_tree)?.VerticalOffset ?? 0;
                    BuildTree();
                    _tree.Dispatcher.BeginInvoke(new Action(() => FindScroll(_tree)?.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
                    e.Handled = true;
                };
                var row = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto);
                row.Margin = new Thickness(depth * 14, 0, 0, 0);
                row.Children.Add(toggle.At(0, 0));
                row.Children.Add(Ui.Icon("IconFolder", 14).Res(System.Windows.Documents.TextElement.ForegroundProperty, "SubtleText").Margin(2, 0, 6, 0).At(0, 1));
                row.Children.Add(Ui.Text(name, 12.5).At(0, 2));
                row.Children.Add(Ui.Subtle(counts[path].ToString("N0"), 11.5, false).Margin(6, 0, 0, 0).At(0, 3));
                var item = new ListBoxItem { Content = row, Tag = path, Padding = new Thickness(8, 5, 8, 5), ToolTip = path.Length == 0 ? null : path };
                _tree.Items.Add(item);
                if ((_folder ?? string.Empty).Equals(path, StringComparison.OrdinalIgnoreCase)) item.IsSelected = true;
                if (hasChildren && expanded) foreach (var c in children[path]) Add(c, depth + 1);
            }
            Add(string.Empty, 0);
        }

        private static ScrollViewer FindScroll(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var found = FindScroll(VisualTreeHelper.GetChild(d, i));
                if (found != null) return found;
            }
            return null;
        }

        private void BuildChips()
        {
            _kindChips.Children.Clear();
            Chip(_kindChips, "All", _files.Count, _kind == null, () => _kind = null);
            foreach (var g in _files.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            {
                var kind = g.Key;
                Chip(_kindChips, FileKinds.Label(kind), g.Count(), _kind == kind, () => _kind = kind);
            }
            _chanceChips.Children.Clear();
            Chip(_chanceChips, "Any chance", _files.Count, _chance == null, () => _chance = null);
            foreach (var c in new[] { Chance.Excellent, Chance.Partial, Chance.Overwritten, Chance.Unrecoverable })
            {
                var n = _files.Count(f => f.Chance == c);
                if (n == 0) continue;
                var chance = c;
                Chip(_chanceChips, c == Chance.Unrecoverable ? "Encrypted" : c.ToString(), n, _chance == c, () => _chance = chance);
            }
        }

        private void Chip(WrapPanel panel, string label, int count, bool on, Action select)
        {
            var t = new ToggleButton { Content = $"{label} {count:N0}", IsChecked = on, Margin = new Thickness(0, 0, 6, 6) }.Styled("ChipToggle");
            t.Click += (s, e) =>
            {
                select();
                BuildChips();
                Fill();
            };
            panel.Children.Add(t);
        }

        private void Fill()
        {
            IEnumerable<FoundFile> q = _files;
            if (_folder != null) q = q.Where(f => f.Folder.Equals(_folder, StringComparison.OrdinalIgnoreCase) || f.Folder.StartsWith(_folder + "\\", StringComparison.OrdinalIgnoreCase));
            if (_kind != null) q = q.Where(f => f.Kind == _kind);
            if (_chance != null) q = q.Where(f => f.Chance == _chance);
            var text = _search.Text.Trim();
            if (text.Length > 0) q = q.Where(f => f.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

            Func<FoundFile, object> key;
            switch (_sort)
            {
                case "name": key = f => f.Name; break;
                case "size": key = f => f.Size; break;
                case "modified": key = f => f.Modified; break;
                case "chance": key = f => (int)f.Chance * 1000 + (100 - f.GoodPercent); break;
                default: key = f => f.Folder + "\\\u0001" + f.Name; break;
            }
            var comparer = Comparer<object>.Create((a, b) => a is string sa && b is string sb ? string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) : Comparer<object>.Default.Compare(a, b));
            _shown = (_descending ? q.OrderByDescending(key, comparer) : q.OrderBy(key, comparer)).ToList();
            _list.ItemsSource = _shown;
            _shownText.Text = _shown.Count == _files.Count ? Format.Count(_files.Count, "file") : $"{_shown.Count:N0} of {_files.Count:N0} shown";
            if (_shown.Count > 0) _list.SelectedIndex = 0;
            else ShowPreview(null);
        }

        // ------------------------------------------------------------------ preview

        private void ShowPreview(FoundFile f)
        {
            var version = ++_previewVersion;
            if (f == null)
            {
                _preview.Content = Ui.Subtle(_files.Count == 0 ? "No deleted files were found." : "Select a file to see it here.", 12.5);
                return;
            }

            var picture = new Border { Height = 180, CornerRadius = new CornerRadius(8), ClipToBounds = true }.Res(Border.BackgroundProperty, "SurfaceAltBg");
            var icon = Ui.Icon(f.IsFolder ? "IconFolder" : "IconFile", 40).Res(System.Windows.Documents.TextElement.ForegroundProperty, "OffDot");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            picture.Child = icon;

            var details = new List<string> { f.SizeText };
            if (f.ModifiedText.Length > 0) details.Add(f.ModifiedText);
            var info = Ui.Column(picture,
                Ui.Text(f.Name, 13.5, bold: true, wrap: true).Margin(0, 12, 0, 0),
                Ui.Subtle(string.Join(" · ", details), 12).Margin(0, 2, 0, 8),
                Ui.Banner(f.Chance == Chance.Excellent ? "IconCheck" : "IconWarn", Ui.Text(FileKinds.ChanceExplained(f), 12, wrap: true),
                    f.Chance == Chance.Excellent ? "OkBg" : f.Chance == Chance.Partial ? "WarnBg" : "BadBg",
                    f.Chance == Chance.Excellent ? "OkText" : f.Chance == Chance.Partial ? "WarnText" : "BadText"),
                Ui.Mono("Was in " + _drive + "\\" + f.Folder, 11.5, "SubtleText").Margin(0, 10, 0, 0));
            ((TextBlock)info.Children[info.Children.Count - 1]).TextWrapping = TextWrapping.Wrap;
            if (f.Source == FoundSource.RecycleBin && f.Deleted > DateTime.MinValue)
                info.Children.Add(Ui.Subtle("Deleted " + f.Deleted.ToLocalTime().ToString("d MMM yyyy, HH:mm"), 12).Margin(0, 4, 0, 0));
            _preview.Content = info;

            if (!f.CanRecover || f.IsFolder) return;
            var isImage = ImageTypes.Contains(f.Extension) && f.Size <= 40L * 1024 * 1024;
            var isText = TextTypes.Contains(f.Extension);
            if (!isImage && !isText) return;
            var volume = _volume;
            var max = isImage ? (int)f.Size : 4096;
            Task.Run(() => Recovery.ReadStart(volume, f, max)).ContinueWith(t =>
            {
                if (version != _previewVersion || t.Result == null || t.Result.Length == 0) return;
                try
                {
                    if (isImage)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 480;
                        bmp.StreamSource = new MemoryStream(t.Result);
                        bmp.EndInit();
                        bmp.Freeze();
                        picture.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                    }
                    else
                    {
                        var preview = Encoding.UTF8.GetString(t.Result).Replace("\0", string.Empty);
                        var box = Ui.Mono(preview, 11, "Text");
                        box.TextWrapping = TextWrapping.Wrap;
                        box.VerticalAlignment = VerticalAlignment.Top;
                        picture.Child = new Border { Padding = new Thickness(10), Child = box };
                    }
                }
                catch (Exception)
                {
                    // Not a picture after all (or damaged): keep the icon.
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ footer

        public void RefreshFooter()
        {
            if (_main.Step != 2) return;
            var ticked = _files.Where(f => f.IsChecked).ToList();
            var left = Ui.Column(Ui.Text(ticked.Count == 0 ? "Tick the files you want back" : $"{Format.Count(ticked.Count, "file")} ticked · {Format.Size(ticked.Sum(f => f.Size))}", 13.5, bold: true),
                Ui.Subtle("Overwritten and encrypted files can't be recovered, so they can't be ticked.", 12, false));
            var tickShown = Ui.Button("Tick all shown", "IconCheck", click: (s, e) => TickAll(_shown, true));
            var untick = Ui.Button("Untick all", null, "GhostButton", (s, e) => TickAll(_files, false));
            untick.IsEnabled = ticked.Count > 0;
            var next = Ui.Button(ticked.Count == 0 ? "Recover…" : $"Recover {Format.Count(ticked.Count, "file")}…", null, "PrimaryButton", (s, e) => _main.ShowRecoverPage());
            next.IsEnabled = ticked.Count > 0;
            _main.SetFooter(left, untick, tickShown, next);
        }

        // ================================================================== step 4: recover

        private UIElement _recoverPage;

        public UIElement BuildRecoverPage()
        {
            if (string.IsNullOrEmpty(_destination.Text)) _destination.Text = SuggestDestination();
            _keepFolders.IsChecked = Settings.KeepFolders;
            _partial.IsChecked = Settings.RecoverPartial;
            _partial.Visibility = _files.Any(f => f.IsChecked && f.Chance == Chance.Partial) ? Visibility.Visible : Visibility.Collapsed;
            if (_recoverPage == null) _recoverPage = CreateRecoverPage();
            _recoverButton = Ui.Button("Recover", null, "PrimaryButton", (s, e) => StartRecovery());
            _main.SetFooter(Ui.Button("Back to files", "IconBack", click: (s, e) => _main.ShowResultsPage()), _recoverButton);
            Validate();
            return _recoverPage;
        }

        private UIElement CreateRecoverPage()
        {
            _destination.Res(Control.FontFamilyProperty, "MonoFont");

            var row = Ui.Columns(Ui.Star(), Ui.Auto);
            row.Children.Add(_destination.At(0, 0));
            row.Children.Add(Ui.Button("Browse…", "IconFolder", click: (s, e) =>
            {
                var folder = Dialogs.PickFolder(_main, "Choose where to save the recovered files (on a different drive)", Directory.Exists(Destination) ? Destination : null);
                if (folder != null) _destination.Text = folder;
            }).Margin(8, 0, 0, 0).At(0, 1));

            var card = Ui.Card(Ui.Column(
                Ui.Text("Where should the recovered files go?", 18, bold: true),
                Ui.Subtle($"Choose a different drive from {_drive}, so the files you're rescuing aren't overwritten.", 12.5).Margin(0, 3, 0, 14),
                row, _checks.Margin(0, 12, 0, 0), _keepFolders.Margin(0, 14, 0, 0), _partial.Margin(0, 8, 0, 0)), 22);
            card.MaxWidth = 680;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(16, 16, 16, 16);
            return new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        }

        private string SuggestDestination()
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm");
            var name = $"{_drive.TrimEnd(':')} {stamp}";
            var last = Settings.LastDestination;
            try
            {
                if (!string.IsNullOrEmpty(last))
                {
                    var parent = Path.GetDirectoryName(last);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Drives.IsOnDrive(parent, _drive)) return Path.Combine(parent, name);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException) { }
            var other = Drives.List().Where(d => d.Size > 0 && d.Letter != _drive).OrderByDescending(d => d.Free).FirstOrDefault();
            return other == null ? string.Empty : Path.Combine(other.Root, "KUDURESCUE", name);
        }

        private List<FoundFile> ToRecover() =>
            _files.Where(f => f.IsChecked && (f.Chance == Chance.Excellent || (f.Chance == Chance.Partial && _partial.IsChecked == true))).ToList();

        /// <summary>Checks the destination: it must be on another drive, with enough space.</summary>
        private string Validate()
        {
            _checks.Children.Clear();
            var files = ToRecover();
            var bytes = files.Sum(f => f.Size);
            string problem = null;
            var dest = Destination;
            if (dest.Length == 0) problem = "Choose a folder on another drive, like a USB drive.";
            else
            {
                try
                {
                    if (!Path.IsPathRooted(dest)) problem = "Enter a full folder path, like F:\\Recovered.";
                    else if (Drives.IsOnDrive(dest, _drive)) problem = $"{_drive} can't be chosen: saving there could overwrite the files you're recovering.";
                }
                catch (ArgumentException) { problem = "That folder name isn't valid."; }
            }
            if (problem == null)
            {
                var free = Drives.FreeSpace(dest);
                var root = Path.GetPathRoot(dest)?.TrimEnd('\\');
                if (free >= 0 && free < bytes) problem = $"Not enough space: {Format.Size(bytes)} is needed, but {root} only has {Format.Size(free)} free.";
                else if (free >= 0)
                    _checks.Children.Add(Ui.Banner("IconCheck", Ui.Text($"{root} has {Format.Size(free)} free. {Format.Size(bytes)} is needed.", 12.5, "Text", wrap: true), "OkBg", "OkText"));
            }
            if (problem != null) _checks.Children.Add(Ui.Banner("IconWarn", Ui.Text(problem, 12.5, "Text", wrap: true), "BadBg", "BadText"));
            if (_recoverButton != null)
            {
                _recoverButton.Content = files.Count == 0 ? "Recover" : $"Recover {Format.Count(files.Count, "file")}" + (problem == null && dest.Length > 1 && dest[1] == ':' ? $" to {dest.Substring(0, 2)}" : string.Empty);
                _recoverButton.IsEnabled = problem == null && files.Count > 0;
            }
            return problem;
        }

        private ProgressBar _bar;
        private TextBlock _current, _counts;

        /// <summary>Recovers the ticked files. Returns when it's finished (the self-test waits on it).</summary>
        public Task StartRecovery()
        {
            if (Validate() != null || IsRecovering) return Task.CompletedTask;
            var files = ToRecover();
            var options = new RecoverOptions { Destination = Destination, KeepFolders = _keepFolders.IsChecked == true, RecoverPartial = _partial.IsChecked == true };
            Settings.LastDestination = options.Destination;
            var total = Math.Max(1, files.Sum(f => f.Size));
            _cancel = new CancellationTokenSource();

            _bar = new ProgressBar { Height = 10, Maximum = 1, Margin = new Thickness(0, 16, 0, 10) };
            _current = Ui.Text(string.Empty, 12.5);
            _counts = Ui.Subtle(string.Empty, 12.5, false);
            var card = Ui.Card(Ui.Column(Ui.Text($"Recovering {Format.Count(files.Count, "file")} to {options.Destination}", 17, bold: true, wrap: true), _bar, _current, _counts.Margin(0, 4, 0, 0)), 22);
            card.MaxWidth = 680;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(16);
            SetPage(card);
            var stop = Ui.Button("Stop", "IconStop", click: (s, e) => _cancel?.Cancel());
            _main.SetFooter(Ui.Subtle($"Reading from {_drive} only. Nothing on it is changed.", 12.5, false), stop);

            var index = 0;
            var last = DateTime.MinValue;
            var token = _cancel.Token;
            return Task.Run(() => Recovery.Run(_volume, files, options, (f, done) =>
            {
                index = files.IndexOf(f) + 1;
                if ((DateTime.UtcNow - last).TotalMilliseconds < 120) return;
                last = DateTime.UtcNow;
                _main.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _bar.Value = (double)done / total;
                    _current.Text = f.RelativePath;
                    _counts.Text = $"File {index:N0} of {files.Count:N0} · {Format.Size(done)} of {Format.Size(total)}";
                }));
            }, token)).ContinueWith(t =>
            {
                _cancel = null;
                LastResult = t.IsFaulted ? new RecoverResult() : t.Result;
                if (t.IsFaulted) LastResult.Problems.Add(new RecoverProblem { File = options.Destination, Reason = t.Exception?.GetBaseException().Message });
                ShowDone(LastResult, options.Destination);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void SetPage(UIElement content)
        {
            var host = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            _main.ShowRecoverContent(host);
        }

        private void ShowDone(RecoverResult r, string destination)
        {
            var ok = r.Recovered > 0 && r.Problems.Count == 0 && !r.Cancelled;
            var title = r.Cancelled ? $"Stopped: {Format.Count(r.Recovered, "file")} recovered" : $"{Format.Count(r.Recovered, "file")} recovered";
            var lines = Ui.Column(
                Ui.Row(Ui.Icon(ok ? "IconCheck" : "IconWarn", 22).Res(System.Windows.Documents.TextElement.ForegroundProperty, ok ? "OkText" : "WarnText"),
                    Ui.Text(title, 19, bold: true).Margin(10, 0, 0, 0)),
                Ui.Text($"{Format.Size(r.Bytes)} saved to {destination}", 13, wrap: true).Margin(0, 10, 0, 0));
            if (r.Partial > 0) lines.Children.Add(Ui.Subtle($"{Format.Count(r.Partial, "file")} only partly there: marked \"(PARTIAL)\" in the name. Parts may be damaged.", 12.5).Margin(0, 6, 0, 0));
            if (r.Skipped > 0) lines.Children.Add(Ui.Subtle($"{Format.Count(r.Skipped, "file")} skipped (overwritten, encrypted, or partial with that option off).", 12.5).Margin(0, 6, 0, 0));
            if (r.BadSectors > 0) lines.Children.Add(Ui.Subtle($"{r.BadSectors:N0} damaged sectors on {_drive} couldn't be read; those parts were filled with zeros. The drive may be failing.", 12.5).Margin(0, 6, 0, 0));
            if (r.Problems.Count > 0)
            {
                lines.Children.Add(Ui.Text($"{Format.Count(r.Problems.Count, "file")} couldn't be recovered:", 13, bold: true).Margin(0, 14, 0, 4));
                foreach (var p in r.Problems.Take(30)) lines.Children.Add(Ui.Subtle($"{p.File}: {p.Reason}", 12));
                if (r.Problems.Count > 30) lines.Children.Add(Ui.Subtle($"…and {r.Problems.Count - 30:N0} more.", 12));
            }
            var card = Ui.Card(lines, 22);
            card.MaxWidth = 680;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(16);
            SetPage(card);

            var open = Ui.Button("Open folder", "IconFolder", "PrimaryButton", (s, e) => AppInfo.OpenFolder(destination));
            var back = Ui.Button("Back to files", "IconBack", click: (s, e) => { _destination.Text = string.Empty; _main.ShowResultsPage(); });
            _main.SetFooter(Ui.Subtle("Check the recovered files before deleting anything else.", 12.5, false), back, open);
        }
    }

    internal static class LayoutExtensions
    {
        public static T With<T>(this T element, Action<T> setup)
        {
            setup(element);
            return element;
        }
    }
}
