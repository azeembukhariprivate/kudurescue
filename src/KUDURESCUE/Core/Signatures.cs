using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Kudurescue.Core
{
    /// <summary>Recognises common file types by their first bytes, to confirm that data really is the file we think it is.</summary>
    public static class Signatures
    {
        private static readonly Dictionary<string, Func<byte[], bool>> Checks = new Dictionary<string, Func<byte[], bool>>(StringComparer.OrdinalIgnoreCase);

        static Signatures()
        {
            void Add(string extensions, Func<byte[], bool> check)
            {
                foreach (var e in extensions.Split(' ')) Checks["." + e] = check;
            }
            Add("jpg jpeg jpe jfif", b => Starts(b, 0xFF, 0xD8, 0xFF));
            Add("png", b => Starts(b, 0x89, 0x50, 0x4E, 0x47));
            Add("gif", b => Ascii(b, 0, "GIF8"));
            Add("bmp", b => Ascii(b, 0, "BM"));
            Add("tif tiff dng cr2 nef arw", b => Starts(b, 0x49, 0x49, 0x2A, 0x00) || Starts(b, 0x4D, 0x4D, 0x00, 0x2A));
            Add("webp avi wav", b => Ascii(b, 0, "RIFF"));
            Add("mp4 m4v mov m4a heic heif avif 3gp 3g2", b => Ascii(b, 4, "ftyp") || Ascii(b, 4, "moov") || Ascii(b, 4, "wide") || Ascii(b, 4, "mdat"));
            Add("mkv webm", b => Starts(b, 0x1A, 0x45, 0xDF, 0xA3));
            Add("mp3", b => Ascii(b, 0, "ID3") || (b.Length > 1 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0));
            Add("flac", b => Ascii(b, 0, "fLaC"));
            Add("ogg oga ogv opus", b => Ascii(b, 0, "OggS"));
            Add("pdf", b => Ascii(b, 0, "%PDF"));
            Add("zip docx xlsx pptx odt ods odp epub jar apk", b => Starts(b, 0x50, 0x4B, 0x03, 0x04));
            Add("doc xls ppt msg", b => Starts(b, 0xD0, 0xCF, 0x11, 0xE0));
            Add("rtf", b => Ascii(b, 0, "{\\rtf"));
            Add("7z", b => Starts(b, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C));
            Add("rar", b => Ascii(b, 0, "Rar!"));
            Add("gz tgz", b => Starts(b, 0x1F, 0x8B));
            Add("exe dll msi sys", b => Ascii(b, 0, "MZ") || Starts(b, 0xD0, 0xCF, 0x11, 0xE0));
            Add("psd", b => Ascii(b, 0, "8BPS"));
            Add("sqlite sqlite3 db", b => Ascii(b, 0, "SQLite format 3"));
            Add("iso", b => true); // the signature is 32 KB in; don't judge
        }

        /// <summary>True if the data looks like this type, false if it clearly doesn't, null if the type isn't known.</summary>
        public static bool? Matches(string extension, byte[] start)
        {
            if (start == null || start.Length < 8 || !Checks.TryGetValue(extension ?? string.Empty, out var check)) return null;
            return check(start);
        }

        private static bool Starts(byte[] b, params byte[] magic) => b.Length >= magic.Length && !magic.Where((m, i) => b[i] != m).Any();

        private static bool Ascii(byte[] b, int offset, string text) =>
            b.Length >= offset + text.Length && Encoding.ASCII.GetString(b, offset, text.Length) == text;
    }
}
