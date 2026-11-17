using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Kudurescue.Views
{
    /// <summary>Finishing touches for repaired copies, using Windows' own picture reader.</summary>
    public static class Repaired
    {
        /// <summary>
        /// A repaired photo is a JPEG closed where the damage started; Windows' reader shows the part that's there with the
        /// rest grey, and saving that picture again gives a JPEG every program opens. Returns null to keep the bytes as they are.
        /// </summary>
        public static byte[] Polish(string extension, byte[] bytes)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".jpe" && ext != ".jfif") return null;
            var frame = Open(bytes);
            if (frame == null) return null;
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return output.ToArray();
            }
        }

        /// <summary>The picture in a file, or null when Windows can't read it.</summary>
        public static BitmapSource Open(byte[] bytes)
        {
            try
            {
                var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame.PixelWidth > 0 && frame.PixelHeight > 0 ? frame : null;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is FileFormatException || ex is IOException || ex is ArgumentException || ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }
    }
}
