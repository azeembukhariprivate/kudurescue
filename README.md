<p align="center">
  <img src="docs/logo-512.png" alt="KUDURESCUE logo" width="128">
</p>

<h1 align="center">KUDURESCUE</h1>

<p align="center">Get deleted files back on Windows. Free, with no size limits.</p>

<p align="center">
  <a href="https://github.com/azeembukhariprivate/kudurescue/releases/latest/download/KUDURESCUE-setup.exe"><img src="https://img.shields.io/badge/Download-KUDURESCUE%20for%20Windows-2ea44f?style=for-the-badge" alt="Download KUDURESCUE for Windows"></a>
  <br>
  <sub>The installer for Windows 10 and 11, always the latest version · <a href="https://github.com/azeembukhariprivate/kudurescue/releases/latest">portable zip and what's new</a></sub>
</p>

---

## Features

- **Four simple steps**: choose a drive → scan → pick files → recover.
- **Quick scan** finds recently deleted files **with their names and original folders**, usually in a minute or two, on:
  - **NTFS** drives (the normal Windows format), by reading the drive's file index (MFT);
  - **FAT32, FAT16 and exFAT** drives (memory cards, USB sticks, cameras and phones), by reading their folders, including folders that were deleted too.
- **Deep scan** for formatted, damaged or unreadable drives: finds files **by their contents**, block by block. It searches only the free space when the drive's format can still be read (that's where deleted files are), or the whole drive when it can't. It recognises photos (JPEG, PNG, GIF, BMP, TIFF and camera RAW, HEIC, WebP), videos (MP4, MOV, AVI, MKV, WebM, 3GP), documents (PDF, Word, Excel, PowerPoint old and new, RTF, OpenDocument), music (MP3, WAV, M4A, FLAC, Ogg, Opus), archives (ZIP, 7Z, RAR, GZ), email and contacts (Outlook PST and MSG, VCF, ICS), design (PSD, SVG, DWG), data (SQLite, EPUB, HTML) and programs (EXE, DLL, APK, JAR, fonts). For most types it works out exactly where the file ends from its internal structure; when it can't, the file runs up to the next file found and is marked **Probably**. Names and folders aren't kept by a format, so files are named by type ("Photo 0001.jpg") and grouped in folders by type.
- **An honest recovery chance for every file**, worked out before you recover anything:
  - **Excellent**: every part of the file is still on the drive.
  - **Partial**: some of it has been reused by other files (the percentage still there is shown).
  - **Probably**: its data is free, but where it starts had to be worked out (see below).
  - **Overwritten**: other files now use its space; it can't be recovered.
- **Recycle Bin first**: files still in a drive's Recycle Bin are shown straight away and come back perfectly, with no scan.
- **Previous Versions**: Windows snapshots of a drive (Volume Shadow Copies) are listed too. KUDURESCUE shows the files that were in a snapshot but have been deleted since, and older versions of files that have changed since, and copies them straight from the snapshot.
- **Rescue failing drives safely**: *Copy a failing drive first* reads a drive once, sector by sector, into an image file on another drive, skipping unreadable sectors instead of getting stuck. Scan the copy as often as you like without wearing out the drive.
- **Open drive images**: `.img`, `.dd`, `.raw` and `.vhd` files (fixed and dynamic), including disk images with several partitions (MBR or GPT), where you choose the partition.
- **Save and open scans**: save what a scan found to a `.kudurescue` file and open it later to recover more, without scanning again. A Deep scan that was stopped carries on from where it got to.
- **Right-click a drive** in File Explorer → *Recover deleted files with KUDURESCUE* (optional, chosen when installing or in Settings).
- **Pick what you want**: the original folder tree, search, filters by type (photos, videos, documents, music, archives, email, design, data and code, programs and disks) and by chance, sorting, and a preview of photos and text files before recovering.
- **Safe by design**:
  - The drive being rescued is opened **read-only**; nothing on it is ever changed.
  - Recovered files **can't be saved to the same drive** (that could overwrite the files you're rescuing), and free space is checked first.
  - SSDs that wipe deleted data (TRIM) are pointed out up front, so you know what to expect.
  - Damaged sectors don't stop a recovery; unreadable parts are filled with zeros and reported.
- Handles fragmented, sparse and **NTFS-compressed** files, and files whose details are spread over several index records.
- Keeps the original folders (optional), restores file dates, and marks partly recovered files with "(PARTIAL)" in the name.
- Light and dark themes, following Windows.

## How it works

When Windows deletes a file it only marks its entry in the drive's index (the MFT) as free; the file's contents stay where they were until something else is written over them. KUDURESCUE reads the drive directly, finds those entries, rebuilds each file's name and folder, and checks Windows' map of used space to see whether the file's contents are still intact. Recovering copies the contents to a folder on another drive.

On FAT32 and exFAT drives, a deleted file's entry keeps its name, size and first cluster. exFAT also records whether the file was stored in one piece, so those files come back exactly; for files stored in pieces, KUDURESCUE follows the map of pieces when it's still there. On FAT32 the map is always cleared, so files are read from consecutive clusters, which is right for most files on memory cards. Two more FAT32 details are handled: Windows overwrites the first letter of a deleted file's short name (KUDURESCUE gets it back from the long name), and on large FAT32 drives it clears half of the file's start position (KUDURESCUE works it out from what fits and from the file's contents, and marks it **Probably** when it can't be sure).

KUDURESCUE has its own recovery engine and doesn't need any other program.

## What no recovery tool can do

- Bring back files whose space has been reused by other files.
- Bring back files from most SSDs some time after deletion: SSDs with TRIM wipe freed space, usually within minutes.
- Read files encrypted with Windows EFS without the original user's key, or drives locked with BitLocker (unlock them first).

The sooner you scan, and the less you use the drive in the meantime, the better your chances.

## Download

**[Download the installer](https://github.com/azeembukhariprivate/kudurescue/releases/latest/download/KUDURESCUE-setup.exe)** (`KUDURESCUE-setup.exe`, always the latest version), or get any of these from the [Releases page](https://github.com/azeembukhariprivate/kudurescue/releases/latest):

- `KUDURESCUE-x.y.z-setup.exe` installs KUDURESCUE for your Windows account.
- `KUDURESCUE-x.y.z-portable.zip` runs without installing: unzip it anywhere and open `KUDURESCUE.exe`.

**Tip:** if you're about to recover files, install or unzip KUDURESCUE on a **different drive** from the one the files were on, so it can't overwrite them.

Requires Windows 10 or 11 (the .NET Framework 4.8 it uses is already part of Windows).

## Administrator rights

Reading a drive directly needs administrator rights. KUDURESCUE starts normally and asks Windows for them only when you start a scan. Copying a drive, opening a saved scan of a drive and looking for Previous Versions need them too. Showing the Recycle Bin and opening drive images don't.

## Privacy

KUDURESCUE works entirely on your computer. The only network request it makes is an optional check for a newer version on GitHub, which can be turned off in Settings.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later). The screens are built in C# and the themes are loaded at startup, so the project builds with `dotnet` on any operating system; the app itself runs on Windows.

```
dotnet run --project tests/KUDURESCUE.Tests
dotnet build src/KUDURESCUE/KUDURESCUE.csproj -c Release -o out/KUDURESCUE
out\KUDURESCUE\KUDURESCUE.exe --self-test
```

The tests build small NTFS, FAT32 and exFAT drive images, disk images with MBR and GPT partition tables, and fixed and dynamic VHD files byte by byte (intact, overwritten, partly reused, fragmented, sparse, compressed, split and encrypted files, deleted long and short names, deleted folders, torn records and lost folders) and check the engine against them. `--self-test` needs administrator rights: it creates small virtual drives (VHD) formatted as NTFS, FAT32 and exFAT, writes files to them, deletes them, recovers them with the real engine and compares every byte, runs a Deep scan after deleting and again after formatting a drive, copies a drive to an image and recovers from the copy, opens real fixed and dynamic VHD files with partition tables, saves and reopens a scan, takes a real Previous Versions snapshot and recovers a file deleted after it, checks the Recycle Bin reader, opens every screen in both themes, writes `self-test.log` and exits. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and compile `installer/KUDURESCUE.iss`.


### Shared code

Some files (the look, dialogs, the update check, administrator helpers) are shared by the KUDU apps and come from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common); each starts with a comment saying so, and `.kudu-common`
lists them. You don't need kudu-common to build this app, but change those files there, not here: the build checks that
they match.

## Publishing a new version

1. Change `<Version>` in `src/KUDURESCUE/KUDURESCUE.csproj` (and `MyAppVersion` in `installer/KUDURESCUE.iss`).
2. Commit and push to `main`.
3. GitHub Actions tests and builds the app. When no release exists yet for that version, it creates the `v` tag and publishes the installer and portable zip on the Releases page. Pushes that keep the same version only build.

## License

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
