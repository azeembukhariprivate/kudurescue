using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Kudurescue.Core
{
    /// <summary>A drive opened read-only in one of the formats KUDURESCUE understands (NTFS, FAT12/16/32, exFAT).</summary>
    public interface IVolume : IDisposable
    {
        IBlockDevice Device { get; }

        /// <summary>"NTFS", "FAT32", "exFAT", …</summary>
        string FormatName { get; }

        int ClusterSize { get; }

        /// <summary>Where a cluster starts on the drive, in bytes.</summary>
        long ClusterOffset(long cluster);

        /// <summary>Lists the deleted files, reporting progress and each file found to <paramref name="scan"/>.</summary>
        void Scan(QuickScan scan, CancellationToken cancel);
    }

    public static class Volumes
    {
        /// <summary>Opens a drive or image in whichever supported format it has.</summary>
        public static IVolume Open(IBlockDevice device, bool ownsDevice = true)
        {
            try
            {
                var sector = new byte[Math.Max(512, device.SectorSize)];
                device.Read(0, sector, 0, sector.Length);
                var oem = Encoding.ASCII.GetString(sector, 3, 8);
                if (oem == "NTFS    " || oem == "-FVE-FS-") return NtfsVolume.Open(device, ownsDevice);
                if (oem == "EXFAT   ") return ExFatVolume.Open(device, ownsDevice);
                if (FatBoot.Looks(sector)) return FatVolume.Open(device, ownsDevice);
                throw new InvalidDataException("This drive's format isn't recognised. It may be damaged, unformatted or use a format KUDURESCUE doesn't support.");
            }
            catch
            {
                if (ownsDevice) device.Dispose();
                throw;
            }
        }
    }
}
