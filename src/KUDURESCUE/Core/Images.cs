using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>A part of another device, e.g. one partition inside a disk image.</summary>
    public sealed class OffsetDevice : IBlockDevice
    {
        private readonly IBlockDevice _base;
        private readonly long _offset;
        private readonly bool _ownsBase;

        public OffsetDevice(IBlockDevice device, long offset, long length, string name, bool ownsBase)
        {
            _base = device;
            _offset = offset;
            Length = Math.Min(length, device.Length - offset);
            Name = name;
            _ownsBase = ownsBase;
        }

        public string Name { get; }
        public long Length { get; }
        public int SectorSize => _base.SectorSize;

        public void Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset + count > Length) throw new IOException($"Read past the end of {Name}.");
            _base.Read(_offset + offset, buffer, index, count);
        }

        public void Dispose()
        {
            if (_ownsBase) _base.Dispose();
        }
    }

    /// <summary>
    /// A Virtual Hard Disk (.vhd) file. Fixed disks are the raw data followed by a 512-byte footer; dynamic disks keep
    /// data in blocks listed in a block allocation table (unwritten blocks read as zeros).
    /// </summary>
    public sealed class VhdDevice : IBlockDevice
    {
        private readonly FileStream _file;
        private readonly bool _dynamic;
        private readonly uint[] _bat;
        private readonly long _blockSize;
        private readonly long _bitmapBytes;

        private VhdDevice(FileStream file, string name, long length, bool dynamic, uint[] bat, long blockSize)
        {
            _file = file;
            Name = name;
            Length = length;
            _dynamic = dynamic;
            _bat = bat;
            _blockSize = blockSize;
            _bitmapBytes = ((blockSize / 512 / 8) + 511) / 512 * 512;
        }

        public string Name { get; }
        public long Length { get; }
        public int SectorSize => 512;

        public static bool IsVhd(string path)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (f.Length < 1024) return false;
                    var footer = new byte[512];
                    f.Position = f.Length - 512;
                    f.Read(footer, 0, 512);
                    return Encoding.ASCII.GetString(footer, 0, 8) == "conectix";
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static VhdDevice Open(string path)
        {
            var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                var footer = new byte[512];
                f.Position = f.Length - 512;
                ReadFully(f, footer);
                if (Encoding.ASCII.GetString(footer, 0, 8) != "conectix") throw new InvalidDataException("This isn't a VHD file.");
                var size = (long)BE64(footer, 48);
                var type = BE32(footer, 60);
                if (type == 2) return new VhdDevice(f, path, Math.Min(size, f.Length - 512), false, null, 0);
                if (type == 4) throw new InvalidDataException("This is a differencing VHD; open its parent disk instead.");
                if (type != 3) throw new InvalidDataException("This VHD type isn't supported.");

                var headerAt = (long)BE64(footer, 16);
                var header = new byte[1024];
                f.Position = headerAt;
                ReadFully(f, header);
                if (Encoding.ASCII.GetString(header, 0, 8) != "cxsparse") throw new InvalidDataException("The VHD's dynamic disk header is damaged.");
                var batAt = (long)BE64(header, 16);
                var entries = (int)BE32(header, 28);
                var blockSize = (long)BE32(header, 32);
                if (entries <= 0 || entries > 1 << 24 || blockSize < 512 || (blockSize & (blockSize - 1)) != 0)
                    throw new InvalidDataException("The VHD's dynamic disk header is damaged.");
                var raw = new byte[entries * 4];
                f.Position = batAt;
                ReadFully(f, raw);
                var bat = new uint[entries];
                for (var i = 0; i < entries; i++) bat[i] = BE32(raw, i * 4);
                return new VhdDevice(f, path, size, true, bat, blockSize);
            }
            catch
            {
                f.Dispose();
                throw;
            }
        }

        public void Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset + count > Length) throw new IOException($"Read past the end of {Name}.");
            lock (_file)
            {
                if (!_dynamic)
                {
                    _file.Position = offset;
                    ReadFully(_file, buffer, index, count);
                    return;
                }
                while (count > 0)
                {
                    var block = offset / _blockSize;
                    var within = offset % _blockSize;
                    var n = (int)Math.Min(count, _blockSize - within);
                    var entry = block < _bat.Length ? _bat[block] : 0xFFFFFFFF;
                    if (entry == 0xFFFFFFFF) Array.Clear(buffer, index, n);
                    else
                    {
                        _file.Position = entry * 512L + _bitmapBytes + within;
                        ReadFully(_file, buffer, index, n);
                    }
                    offset += n;
                    index += n;
                    count -= n;
                }
            }
        }

        public void Dispose() => _file.Dispose();

        private static void ReadFully(Stream s, byte[] b) => ReadFully(s, b, 0, b.Length);

        private static void ReadFully(Stream s, byte[] b, int index, int count)
        {
            while (count > 0)
            {
                var n = s.Read(b, index, count);
                if (n <= 0) throw new IOException("Unexpected end of the VHD file.");
                index += n;
                count -= n;
            }
        }

        private static uint BE32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static ulong BE64(byte[] b, int o) => ((ulong)BE32(b, o) << 32) | BE32(b, o + 4);
    }

    /// <summary>One partition found in a disk image (or the whole image, when it holds a single volume).</summary>
    public sealed class ImagePart
    {
        public long Offset { get; set; }
        public long Length { get; set; }
        public string Format { get; set; }
        public string Label { get; set; }
        public int Index { get; set; }

        public string Title => $"Partition {Index} · {Format} · {Kudurescue.Core.Format.Size(Length)}" + (string.IsNullOrEmpty(Label) ? string.Empty : $" · {Label}");
    }

    /// <summary>Drive images: opening .img/.dd/.vhd files, finding partitions inside them, and copying a drive into one.</summary>
    public static class DriveImages
    {
        public const string FileFilter = "Drive images (*.img; *.dd; *.raw; *.bin; *.vhd)|*.img;*.dd;*.raw;*.bin;*.vhd;*.ima|All files|*.*";

        /// <summary>Opens an image file as a device.</summary>
        public static IBlockDevice OpenFile(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".vhdx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("VHDX files aren't supported yet. Double-click the file to attach it in Windows, then scan its drive letter.");
            return VhdDevice.IsVhd(path) ? (IBlockDevice)VhdDevice.Open(path) : new ImageDevice(path);
        }

        /// <summary>The volumes in an image: the image itself when it's one volume, otherwise the partitions in its MBR or GPT table.</summary>
        public static List<ImagePart> Partitions(IBlockDevice device)
        {
            var parts = new List<ImagePart>();
            var sector = Read(device, 0, 512);
            var own = FormatOf(sector);
            if (own != null)
            {
                parts.Add(new ImagePart { Index = 1, Offset = 0, Length = device.Length, Format = own });
                return parts;
            }
            if (sector.Length < 512 || sector[510] != 0x55 || sector[511] != 0xAA) return parts;

            var entries = new List<(long Start, long Length)>();
            var gpt = Read(device, 512, 512);
            if (gpt.Length == 512 && Encoding.ASCII.GetString(gpt, 0, 8) == "EFI PART")
            {
                var tableLba = BitConverter.ToInt64(gpt, 72);
                var count = Math.Min(BitConverter.ToInt32(gpt, 80), 256);
                var size = BitConverter.ToInt32(gpt, 84);
                if (size < 128 || size > 4096) size = 128;
                var table = Read(device, tableLba * 512, count * size);
                for (var i = 0; i + 1 <= count && (i + 1) * size <= table.Length; i++)
                {
                    var e = i * size;
                    if (table.Skip(e).Take(16).All(b => b == 0)) continue;
                    var first = BitConverter.ToInt64(table, e + 32);
                    var last = BitConverter.ToInt64(table, e + 40);
                    if (first > 0 && last >= first) entries.Add((first * 512, (last - first + 1) * 512));
                }
            }
            else
            {
                void Mbr(byte[] s, long baseLba, long extendedBase, int depth)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var e = 446 + i * 16;
                        var type = s[e + 4];
                        var start = BitConverter.ToUInt32(s, e + 8);
                        var count = BitConverter.ToUInt32(s, e + 12);
                        if (type == 0 || count == 0) continue;
                        if (type == 0x05 || type == 0x0F || type == 0x85)
                        {
                            // Extended partition: a chain of extended boot records.
                            var ebrLba = (extendedBase == 0 ? start : extendedBase + start);
                            var ebr = Read(device, ebrLba * 512, 512);
                            if (depth < 64 && ebr.Length == 512 && ebr[510] == 0x55 && ebr[511] == 0xAA)
                                Mbr(ebr, ebrLba, extendedBase == 0 ? start : extendedBase, depth + 1);
                            continue;
                        }
                        entries.Add(((baseLba + start) * 512, (long)count * 512));
                    }
                }
                Mbr(sector, 0, 0, 0);
            }

            var index = 1;
            foreach (var (start, length) in entries.OrderBy(e => e.Start))
            {
                if (start >= device.Length) continue;
                var boot = Read(device, start, 512);
                parts.Add(new ImagePart
                {
                    Index = index++,
                    Offset = start,
                    Length = Math.Min(length, device.Length - start),
                    Format = FormatOf(boot) ?? "Unknown",
                    Label = null
                });
            }
            return parts;
        }

        private static string FormatOf(byte[] boot)
        {
            if (boot.Length < 512) return null;
            var oem = Encoding.ASCII.GetString(boot, 3, 8);
            if (oem == "NTFS    ") return "NTFS";
            if (oem == "EXFAT   ") return "exFAT";
            if (oem == "-FVE-FS-") return "BitLocker";
            if (FatBoot.Looks(boot))
            {
                try { return "FAT" + FatBoot.Parse(boot).Type; }
                catch (InvalidDataException) { return null; }
            }
            return null;
        }

        private static byte[] Read(IBlockDevice device, long offset, int count)
        {
            if (offset < 0 || offset >= device.Length) return new byte[0];
            count = (int)Math.Min(count, device.Length - offset);
            var b = new byte[count];
            SafeRead.Read(device, offset, b, 0, count);
            return b;
        }

        /// <summary>
        /// Copies a drive into an image file, sector by sector. Unreadable sectors are written as zeros and counted, so a
        /// failing drive is read once, as gently as possible, and every later scan uses the copy.
        /// <paramref name="progress"/> gets bytes done and bad sectors so far.
        /// </summary>
        public static int Copy(IBlockDevice source, string target, Action<long, int> progress, CancellationToken cancel)
        {
            var buffer = new byte[1 << 20];
            var bad = 0;
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20))
            {
                for (long pos = 0; pos < source.Length; pos += buffer.Length)
                {
                    cancel.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(buffer.Length, source.Length - pos);
                    bad += SafeRead.Read(source, pos, buffer, 0, n);
                    output.Write(buffer, 0, n);
                    progress?.Invoke(pos + n, bad);
                }
            }
            return bad;
        }
    }
}
