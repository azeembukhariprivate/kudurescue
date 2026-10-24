using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;

namespace Kudurescue.Core
{
    /// <summary>Lists, creates and removes Windows "Previous Versions" snapshots (Volume Shadow Copies). Needs administrator rights.</summary>
    public static class ShadowCopies
    {
        public static List<Snapshot> List(string drive)
        {
            var letter = VolumeDevice.DriveLetter(drive);
            var volume = VolumeName(letter);
            var list = new List<Snapshot>();
            using (var searcher = new ManagementObjectSearcher("SELECT ID, DeviceObject, VolumeName, InstallDate FROM Win32_ShadowCopy"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    using (o)
                    {
                        if (!string.Equals(o["VolumeName"] as string, volume, StringComparison.OrdinalIgnoreCase)) continue;
                        DateTime created;
                        try { created = ManagementDateTimeConverter.ToDateTime(o["InstallDate"] as string).ToUniversalTime(); }
                        catch (ArgumentException) { created = DateTime.MinValue; }
                        list.Add(new Snapshot { Drive = letter + ":", DevicePath = o["DeviceObject"] as string, Created = created, Id = o["ID"] as string });
                    }
                }
            }
            return list.Where(s => !string.IsNullOrEmpty(s.DevicePath)).OrderByDescending(s => s.Created).ToList();
        }

        /// <summary>Takes a snapshot of a drive (used by the self-test). Returns its ID.</summary>
        public static string Create(string drive)
        {
            using (var cls = new ManagementClass("Win32_ShadowCopy"))
            using (var args = cls.GetMethodParameters("Create"))
            {
                args["Volume"] = VolumeDevice.DriveLetter(drive) + @":\";
                args["Context"] = "ClientAccessible";
                using (var result = cls.InvokeMethod("Create", args, null))
                {
                    var code = Convert.ToUInt32(result["ReturnValue"]);
                    if (code != 0) throw new InvalidOperationException("Windows couldn't take a snapshot (error " + code + ").");
                    return result["ShadowID"] as string;
                }
            }
        }

        public static void Delete(string id)
        {
            using (var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id.Replace("'", string.Empty)}'"))
                foreach (ManagementObject o in searcher.Get())
                    using (o) o.Delete();
        }

        private static string VolumeName(char letter)
        {
            var name = new StringBuilder(128);
            return Native.GetVolumeNameForVolumeMountPoint(letter + @":\", name, name.Capacity) ? name.ToString() : null;
        }
    }
}
