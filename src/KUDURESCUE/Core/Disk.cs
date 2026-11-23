// Reading a drive's bytes directly (only ever reading): a whole drive with administrator rights, or a disk image file;
// and the Windows calls used.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kudurescue.Core
{
    /// <summary>Something that can be read like a disk: a drive (volume) or an image file. KUDURESCUE never writes to it.</summary>
    public interface IBlockDevice : IDisposable
    {
        string Name { get; }
        long Length { get; }
        int SectorSize { get; }

        /// <summary>Reads exactly <paramref name="count"/> bytes at any offset. Throws IOException if the device can't be read there.</summary>
        void Read(long offset, byte[] buffer, int index, int count);
    }

    /// <summary>An image file (or any file) read as a disk. Used for drive images and by the tests.</summary>
    public sealed class ImageDevice : IBlockDevice
    {
        /// <summary>The image file.</summary>
        private readonly FileStream _stream;

        /// <summary>Opens an image file to read.</summary>
        public ImageDevice(string path, int sectorSize = 512)
        {
            Name = path;
            SectorSize = sectorSize;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        }

        /// <summary>The file's name.</summary>
        public string Name { get; }
        /// <summary>How big it is.</summary>
        public long Length => _stream.Length;
        /// <summary>How big a sector is.</summary>
        public int SectorSize { get; }

        /// <summary>Reads bytes from a place in the image.</summary>
        public void Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset + count > Length) throw new IOException($"Read past the end of {Name}.");
            lock (_stream)
            {
                _stream.Position = offset;
                var done = 0;
                while (done < count)
                {
                    var n = _stream.Read(buffer, index + done, count - done);
                    if (n <= 0) throw new IOException($"Unexpected end of {Name}.");
                    done += n;
                }
            }
        }

        /// <summary>Closes the file.</summary>
        public void Dispose() => _stream.Dispose();
    }

    /// <summary>
    /// A Windows drive opened for raw, read-only access (\\.\D:). Needs administrator rights.
    /// Windows only allows whole sectors to be read from a drive, so reads are widened to sector boundaries.
    /// </summary>
    public sealed class VolumeDevice : IBlockDevice
    {
        /// <summary>The drive, opened for reading only.</summary>
        private readonly SafeFileHandle _handle;
        /// <summary>Guards reading (one read at a time).</summary>
        private readonly object _lock = new object();
        /// <summary>A buffer for reads that don't fall on sector edges (the drive only reads whole sectors).</summary>
        private byte[] _aligned = new byte[0];

        /// <summary>A drive opened to read.</summary>
        private VolumeDevice(SafeFileHandle handle, string name, long length, int sectorSize)
        {
            _handle = handle;
            Name = name;
            Length = length;
            SectorSize = sectorSize;
        }

        /// <summary>The drive's name, like "D:".</summary>
        public string Name { get; }
        /// <summary>How big it is.</summary>
        public long Length { get; }
        /// <summary>How big a sector is.</summary>
        public int SectorSize { get; }

        /// <summary>Opens a drive by letter ("D:" or "D:\"). Throws UnauthorizedAccessException without administrator rights.</summary>
        public static VolumeDevice Open(string drive)
        {
            var letter = DriveLetter(drive);
            var path = @"\\.\" + letter + ":";
            var handle = Native.CreateFile(path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 5) throw new UnauthorizedAccessException("Reading a drive directly needs administrator rights.");
                throw new IOException($"Drive {letter}: can't be opened: {new Win32Exception(error).Message}");
            }

            // Without this, Windows refuses to read the last sectors of a partition beyond the end the file system
            // declares, which drive copies and Deep scan need. It only widens what may be read.
            Native.DeviceIoControl(handle, Native.FSCTL_ALLOW_EXTENDED_DASD_IO, null, 0, null, 0, out _, IntPtr.Zero);

            var sectorSize = 512;
            var geometry = new byte[24];
            if (Native.DeviceIoControl(handle, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY, null, 0, geometry, geometry.Length, out _, IntPtr.Zero))
            {
                var bps = BitConverter.ToInt32(geometry, 20);
                if (bps >= 512 && bps <= 65536 && (bps & (bps - 1)) == 0) sectorSize = bps;
            }

            long length = 0;
            var lengthInfo = new byte[8];
            if (Native.DeviceIoControl(handle, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 0, lengthInfo, 8, out _, IntPtr.Zero))
                length = BitConverter.ToInt64(lengthInfo, 0);
            if (length <= 0)
            {
                try { length = new DriveInfo(letter + ":").TotalSize; }
                catch { length = long.MaxValue / 2; }
            }
            return new VolumeDevice(handle, $"Drive {letter}:", length, sectorSize);
        }

        /// <summary>The letter of a drive, from a path like "D:\".</summary>
        public static char DriveLetter(string drive)
        {
            var d = (drive ?? string.Empty).Trim();
            if (d.Length == 0 || !char.IsLetter(d[0]) || (d.Length > 1 && d[1] != ':')) throw new ArgumentException("Not a drive letter: " + drive);
            return char.ToUpperInvariant(d[0]);
        }

        /// <summary>Reads bytes from a place on the drive (through whole sectors).</summary>
        public void Read(long offset, byte[] buffer, int index, int count)
        {
            if (count == 0) return;
            var start = offset / SectorSize * SectorSize;
            var end = (offset + count + SectorSize - 1) / SectorSize * SectorSize;
            var length = (int)(end - start);
            lock (_lock)
            {
                if (_aligned.Length < length) _aligned = new byte[Math.Max(length, 1 << 20)];
                if (!Native.SetFilePointerEx(_handle, start, out _, 0))
                    throw new IOException($"Can't move to byte {start:N0} on {Name}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                var done = 0;
                while (done < length)
                {
                    if (!Native.ReadFile(_handle, _aligned, length - done, out var n, IntPtr.Zero, done))
                        throw new IOException($"Can't read byte {start + done:N0} on {Name}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    if (n <= 0) throw new IOException($"Unexpected end of {Name}.");
                    done += n;
                }
                Buffer.BlockCopy(_aligned, (int)(offset - start), buffer, index, count);
            }
        }

        /// <summary>Closes the drive.</summary>
        public void Dispose() => _handle.Dispose();
    }

    /// <summary>Reads that survive bad sectors: unreadable sectors come back as zeros and are counted.</summary>
    public static class SafeRead
    {
        /// <returns>The number of sectors that couldn't be read.</returns>
        public static int Read(IBlockDevice device, long offset, byte[] buffer, int index, int count)
        {
            try
            {
                device.Read(offset, buffer, index, count);
                return 0;
            }
            catch (IOException)
            {
                // Try sector by sector so one bad sector doesn't lose the whole block.
                var bad = 0;
                var sector = device.SectorSize;
                var pos = 0;
                while (pos < count)
                {
                    var next = (int)Math.Min(count - pos, sector - (offset + pos) % sector);
                    try { device.Read(offset + pos, buffer, index + pos, next); }
                    catch (IOException)
                    {
                        Array.Clear(buffer, index + pos, next);
                        bad++;
                    }
                    pos += next;
                }
                return bad;
            }
        }
    }

    public static class Native
    {
        /// <summary>Windows' constants for opening and asking drives (as in Windows' own headers).</summary>
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
        public const uint OPEN_EXISTING = 3;
        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

        /// <summary>Opens a file or drive.</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        /// <summary>Asks a drive something (its size, its sectors...).</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

        /// <summary>Writes what's waiting to the drive (used only for files saved elsewhere).</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushFileBuffers(SafeFileHandle file);

        /// <summary>Moves to a place in a file or drive.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint method);

        /// <summary>Reads into memory.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool ReadFile(SafeFileHandle file, byte* buffer, int count, out int read, IntPtr overlapped);

        /// <summary>Reads into a byte array.</summary>
        public static unsafe bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped, int offset)
        {
            fixed (byte* p = buffer) return ReadFile(file, p + offset, count, out read, overlapped);
        }

        /// <summary>The drive or mounted folder a path is on.</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetVolumePathName(string fileName, System.Text.StringBuilder volumePath, int length);

        /// <summary>The drive's own volume name, from where it's mounted.</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, System.Text.StringBuilder volumeName, int length);

        /// <summary>Lets a folder be opened (to find which drive a path is really on).</summary>
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // needed to open a folder
        /// <summary>Asks for the drive's volume name.</summary>
        public const uint VOLUME_NAME_GUID = 0x1;

        /// <summary>The full path of an open file or folder (following links).</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
    }
}
