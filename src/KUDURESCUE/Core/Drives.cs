using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Kudurescue.Core
{
    public enum DriveKind { InternalSsd, InternalHdd, Usb, MemoryCard, Virtual, Other }

    public sealed class DriveDetails
    {
        public string Letter { get; set; }  // "D:"
        public string Label { get; set; }
        public string Format { get; set; }
        public long Size { get; set; }
        public long Free { get; set; }
        public DriveKind Kind { get; set; }
        public bool Trim { get; set; }
        public bool IsSystem { get; set; }
        public string Product { get; set; }

        public string Root => Letter + "\\";
        public bool IsNtfs => string.Equals(Format, "NTFS", StringComparison.OrdinalIgnoreCase);
        public bool IsFat => Format != null && (Format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || Format.Equals("exFAT", StringComparison.OrdinalIgnoreCase));

        /// <summary>SSDs with TRIM wipe deleted data soon after deletion, so there's little left to find.</summary>
        public bool TrimWarning => Kind == DriveKind.InternalSsd && Trim;

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case DriveKind.InternalSsd: return "Internal SSD";
                    case DriveKind.InternalHdd: return "Internal hard drive";
                    case DriveKind.Usb: return "USB drive";
                    case DriveKind.MemoryCard: return "Memory card";
                    case DriveKind.Virtual: return "Virtual drive";
                    default: return "Drive";
                }
            }
        }

        public string Title => string.IsNullOrWhiteSpace(Label) ? (string.IsNullOrWhiteSpace(Product) ? KindText : Product) : Label;
    }

    public static class Drives
    {
        public static List<DriveDetails> List()
        {
            var list = new List<DriveDetails>();
            var system = (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\").Substring(0, 2).ToUpperInvariant();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                var details = new DriveDetails { Letter = d.Name.Substring(0, 2).ToUpperInvariant() };
                details.IsSystem = details.Letter == system;
                try
                {
                    if (!d.IsReady) { details.Format = "Not ready"; }
                    else
                    {
                        details.Label = d.VolumeLabel;
                        details.Format = d.DriveFormat;
                        details.Size = d.TotalSize;
                        details.Free = d.AvailableFreeSpace;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    details.Format = "Locked or not ready";
                }
                Describe(details, d.DriveType == DriveType.Removable);
                list.Add(details);
            }
            return list;
        }

        /// <summary>Asks Windows what kind of device the drive is on (USB, SD card, SSD) and whether it uses TRIM. Works without administrator rights.</summary>
        private static void Describe(DriveDetails details, bool removable)
        {
            details.Kind = removable ? DriveKind.Usb : DriveKind.InternalHdd;
            try
            {
                using (var handle = Native.CreateFile(@"\\.\" + details.Letter, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) return;

                    var device = Query(handle, 0, 1024); // StorageDeviceProperty
                    if (device != null && device.Length >= 32)
                    {
                        var bus = BitConverter.ToInt32(device, 28);
                        var productOffset = BitConverter.ToInt32(device, 16);
                        details.Product = ReadString(device, productOffset);
                        switch (bus)
                        {
                            case 7: details.Kind = DriveKind.Usb; break;            // BusTypeUsb
                            case 0xC: case 0xD: details.Kind = DriveKind.MemoryCard; break; // BusTypeSd, BusTypeMmc
                            case 0xE: case 0xF: details.Kind = DriveKind.Virtual; break;   // BusTypeVirtual, BusTypeFileBackedVirtual
                        }
                    }

                    if (details.Kind == DriveKind.InternalHdd)
                    {
                        var seek = Query(handle, 7, 12); // StorageDeviceSeekPenaltyProperty
                        if (seek != null && seek.Length >= 9 && seek[8] == 0) details.Kind = DriveKind.InternalSsd;
                    }
                    var trim = Query(handle, 8, 12); // StorageDeviceTrimProperty
                    details.Trim = trim != null && trim.Length >= 9 && trim[8] != 0;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is IOException)
            {
                // Not on Windows, or the device doesn't answer: keep the simple guess.
            }
        }

        private static byte[] Query(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int propertyId, int size)
        {
            var query = new byte[12];
            BitConverter.GetBytes(propertyId).CopyTo(query, 0); // QueryType 0 = PropertyStandardQuery
            var output = new byte[size];
            return Native.DeviceIoControl(handle, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out var returned, IntPtr.Zero)
                ? output.Take(returned).ToArray() : null;
        }

        private static string ReadString(byte[] data, int offset)
        {
            if (offset <= 0 || offset >= data.Length) return null;
            var end = offset;
            while (end < data.Length && data[end] != 0) end++;
            var s = Encoding.ASCII.GetString(data, offset, end - offset).Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// Whether a folder is on the given drive, also when the drive is mounted in a folder.
        /// Recovered files must never be saved to the drive they're recovered from.
        /// </summary>
        public static bool IsOnDrive(string folder, string drive)
        {
            var letter = VolumeDevice.DriveLetter(drive);
            try
            {
                var full = Path.GetFullPath(folder);
                var mount = new StringBuilder(1024);
                if (Native.GetVolumePathName(full, mount, mount.Capacity))
                {
                    var a = new StringBuilder(128);
                    var b = new StringBuilder(128);
                    if (Native.GetVolumeNameForVolumeMountPoint(mount.ToString(), a, a.Capacity) &&
                        Native.GetVolumeNameForVolumeMountPoint(letter + @":\", b, b.Capacity))
                        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
                }
                return char.ToUpperInvariant(full[0]) == letter && full.Length > 1 && full[1] == ':';
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return char.ToUpperInvariant(folder.TrimStart()[0]) == letter;
            }
        }

        /// <summary>Free space where the folder is, or -1 if unknown.</summary>
        public static long FreeSpace(string folder)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return -1;
                var d = new DriveInfo(root);
                return d.IsReady ? d.AvailableFreeSpace : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
