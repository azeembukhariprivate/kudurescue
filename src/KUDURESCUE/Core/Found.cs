using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;

namespace Kudurescue.Core
{
    /// <summary>How much of a deleted file is still on the drive. Unsure: still there, but where it starts had to be worked out.</summary>
    public enum Chance { Excellent, Unsure, Partial, Overwritten, Unrecoverable }

    public enum FileKind { Photos, Videos, Documents, Music, Archives, Email, Design, Data, Programs, Other }

    public enum FoundSource { Index, RecycleBin, Carved, Snapshot }

    /// <summary>A file that can (perhaps) be recovered. The marked members are kept when a scan is saved.</summary>
    [DataContract]
    public sealed class FoundFile : INotifyPropertyChanged
    {
        private bool _isChecked;

        [DataMember] public FoundSource Source { get; set; }
        [DataMember] public string Name { get; set; }

        /// <summary>Where the file was, relative to the drive's root, like "Photos\2026\Goa trip". Empty for the root.</summary>
        [DataMember] public string Folder { get; set; } = string.Empty;

        [DataMember] public long Size { get; set; }
        [DataMember] public DateTime Created { get; set; }
        [DataMember] public DateTime Modified { get; set; }
        [DataMember] public DateTime Deleted { get; set; }
        [DataMember] public Chance Chance { get; set; }

        /// <summary>Percentage of the file's data that is still free on the drive (100 for Excellent).</summary>
        [DataMember] public int GoodPercent { get; set; } = 100;

        [DataMember] public FileKind Kind { get; set; }

        /// <summary>Something worth knowing about this file, shown in the preview (e.g. how its location was found).</summary>
        [DataMember] public string Note { get; set; }

        // Found in the drive's index (MFT).
        [DataMember] public long Record { get; set; } = -1;
        [DataMember] public byte[] ResidentData { get; set; }
        [DataMember] public List<DataRun> Runs { get; set; }
        [DataMember] public bool Compressed { get; set; }
        [DataMember] public int CompressionUnit { get; set; }
        [DataMember] public long InitializedSize { get; set; }

        /// <summary>Found by Deep scan: where the file starts on the drive, in bytes.</summary>
        [DataMember] public long Offset { get; set; } = -1;

        // Found in the Recycle Bin.
        [DataMember] public string RecyclePath { get; set; }
        [DataMember] public bool IsFolder { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Text for the file list.
        public string SizeText => IsFolder ? (Size > 0 ? "Folder · " + Format.Size(Size) : "Folder") : Format.Size(Size);
        public string ModifiedText => Modified > new DateTime(1980, 1, 1) ? Modified.ToLocalTime().ToString("d MMM yyyy") : string.Empty;
        public string ChanceText => FileKinds.ChanceLabel(Chance, GoodPercent);
        public string FolderText => Folder.Length == 0 ? "\\" : Folder;
        public string Tone => Chance == Chance.Excellent ? "ok" : Chance == Chance.Partial || Chance == Chance.Unsure ? "warn" : "bad";
        public bool CanRecover => Chance == Chance.Excellent || Chance == Chance.Unsure || Chance == Chance.Partial;

        public string Extension => Path.GetExtension(Name ?? string.Empty).ToLowerInvariant();
        /// <summary>Recovered by copying an existing file (from the Recycle Bin or a snapshot) rather than reading the drive.</summary>
        public bool IsCopy => Source == FoundSource.RecycleBin || Source == FoundSource.Snapshot;

        public string RelativePath => Folder.Length == 0 ? Name : Folder + "\\" + Name;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised when any file is ticked or unticked, so totals can be updated.</summary>
        public static event EventHandler CheckedChanged;
    }

    /// <summary>Groups files by type, from their extension.</summary>
    public static class FileKinds
    {
        private static readonly Dictionary<string, FileKind> Map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);

        static FileKinds()
        {
            void Add(FileKind kind, string list)
            {
                foreach (var e in list.Split(' ')) Map["." + e] = kind;
            }
            Add(FileKind.Photos, "jpg jpeg jpe jfif png gif bmp tif tiff webp heic heif avif ico raw cr2 cr3 crw nef nrw arw srf sr2 orf rw2 raf dng pef srw x3f 3fr erf kdc mrw psb");
            Add(FileKind.Videos, "mp4 m4v mov avi mkv wmv flv webm mpg mpeg m2ts mts ts 3gp 3g2 vob ogv mxf divx");
            Add(FileKind.Documents, "pdf doc docx docm dot dotx xls xlsx xlsm xlsb xlt xltx ppt pptx pptm pps ppsx odt ods odp odg rtf txt md pages numbers key wps wpd xps oxps one");
            Add(FileKind.Music, "mp3 wav flac m4a aac ogg oga opus wma aif aiff alac mid midi amr ape");
            Add(FileKind.Archives, "zip rar 7z tar gz tgz bz2 tbz2 xz txz zst cab arj lzh lha z");
            Add(FileKind.Email, "pst ost eml msg mbox emlx vcf ics vcs");
            Add(FileKind.Design, "psd ai eps svg cdr indd idml dwg dxf sketch fig xd afdesign afphoto afpub skp blend fbx obj stl 3ds max c4d");
            Add(FileKind.Data, "epub mobi azw azw3 fb2 djvu csv tsv json xml yaml yml html htm sql sqlite sqlite3 db mdb accdb qbw qbb log ini cfg conf cs c cpp h hpp java py js ts jsx tsx php rb go rs swift kt vb ps1 bat cmd sh css scss ipynb");
            Add(FileKind.Programs, "exe msi msix appx dll sys iso img vhd vhdx vmdk vdi ova qcow2 apk ttf otf woff woff2 jar");
        }

        public static FileKind Of(string name) =>
            Map.TryGetValue(Path.GetExtension(name ?? string.Empty), out var kind) ? kind : FileKind.Other;

        public static string Label(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Email: return "Email & contacts";
                case FileKind.Design: return "Design";
                case FileKind.Data: return "Data & code";
                case FileKind.Programs: return "Programs & disks";
                default: return kind.ToString();
            }
        }

        public static string ChanceLabel(Chance chance, int goodPercent)
        {
            switch (chance)
            {
                case Chance.Excellent: return "Excellent";
                case Chance.Unsure: return "Probably";
                case Chance.Partial: return $"Partial · {goodPercent}%";
                case Chance.Overwritten: return "Overwritten";
                default: return "Encrypted";
            }
        }

        public static string ChanceExplained(FoundFile f)
        {
            switch (f.Chance)
            {
                case Chance.Excellent:
                    return f.Source == FoundSource.RecycleBin ? "Excellent: it's still in the Recycle Bin"
                        : f.Source == FoundSource.Snapshot ? "Excellent: it's kept in a Previous Versions snapshot"
                        : "Excellent: every part is still on the drive";
                case Chance.Unsure: return "Probably: its data is free on the drive, but where it starts had to be worked out. Check the file after recovering it";
                case Chance.Partial: return $"Partial: {f.GoodPercent}% is still on the drive; the rest has been reused by other files";
                case Chance.Overwritten: return "Overwritten: other files now use this space";
                default: return "Encrypted by Windows (EFS): it can't be recovered without the original user's key";
            }
        }
    }
}
