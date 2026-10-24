using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Kudurescue.Tests
{
    /// <summary>Small but structurally valid files of each type Deep scan knows, built byte by byte.</summary>
    internal static class CarveSamples
    {
        private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
        private static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] BE32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        /// <summary>Random-looking data with no 0xFF bytes (so it can sit inside JPEG data or anywhere).</summary>
        public static byte[] Noise(int n, int seed) => NtfsImage.Pattern(n, seed).Select(b => b == 0xFF ? (byte)0x7E : b).ToArray();

        public static byte[] Jpeg()
        {
            var thumbnail = Cat(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, BE16(6), Noise(4, 1), new byte[] { 0xFF, 0xD9 });
            var app1 = Cat(new byte[] { 0xFF, 0xE1 }, BE16(2 + 6 + thumbnail.Length), A("Exif\0\0"), thumbnail);
            var dqt = Cat(new byte[] { 0xFF, 0xDB }, BE16(67), Noise(65, 2));
            var sos = Cat(new byte[] { 0xFF, 0xDA }, BE16(12), Noise(10, 3));
            var data = Cat(Noise(3000, 4), new byte[] { 0xFF, 0x00 }, Noise(500, 5), new byte[] { 0xFF, 0xD3 }, Noise(800, 6));
            return Cat(new byte[] { 0xFF, 0xD8 }, app1, dqt, sos, data, new byte[] { 0xFF, 0xD9 });
        }

        public static byte[] Png()
        {
            byte[] Chunk(string type, byte[] data) => Cat(BE32(data.Length), A(type), data, new byte[4]);
            return Cat(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                Chunk("IHDR", new byte[13]), Chunk("IDAT", Noise(5000, 7)), Chunk("IEND", new byte[0]));
        }

        public static byte[] Gif()
        {
            var image = Cat(new byte[] { 0x2C, 0, 0, 0, 0, 4, 0, 4, 0, 0x00 }, new byte[] { 2 }, new byte[] { 200 }, Noise(200, 8), new byte[] { 50 }, Noise(50, 9), new byte[] { 0 });
            return Cat(A("GIF89a"), new byte[] { 4, 0, 4, 0, 0x80, 0, 0 }, new byte[6], new byte[] { 0x21, 0xF9, 4, 0, 0, 0, 0, 0 }, image, new byte[] { 0x3B });
        }

        public static byte[] Bmp()
        {
            var size = 54 + 3000;
            var b = new byte[size];
            A("BM").CopyTo(b, 0);
            BitConverter.GetBytes(size).CopyTo(b, 2);
            BitConverter.GetBytes(54).CopyTo(b, 10);
            BitConverter.GetBytes(40).CopyTo(b, 14);
            Noise(3000, 10).CopyTo(b, 54);
            return b;
        }

        public static byte[] Pdf() =>
            A("%PDF-1.7\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n2 0 obj << /Updated true >> endobj\ntrailer << >>\n%%EOF\n");

        public static byte[] Docx()
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    foreach (var name in new[] { "[Content_Types].xml", "word/document.xml", "docProps/core.xml" })
                        using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(string.Concat(Enumerable.Repeat("<w:p>Hello</w:p>", 200)));
                }
                return ms.ToArray();
            }
        }

        public static byte[] Mp4() => Cat(BE32(20), A("ftypisom"), BE32(512), A("isom"), BE32(16), A("moov"), new byte[8], BE32(2008), A("mdat"), Noise(2000, 11));

        public static byte[] Wav()
        {
            var data = Noise(4000, 12);
            return Cat(A("RIFF"), BitConverter.GetBytes(4 + 8 + data.Length), A("WAVE"), A("data"), BitConverter.GetBytes(data.Length), data);
        }

        public static byte[] Mp3()
        {
            var id3 = Cat(A("ID3"), new byte[] { 3, 0, 0, 0, 0, 0, 20 }, new byte[20]);
            var frames = new List<byte>();
            for (var i = 0; i < 10; i++)
            {
                // MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding: 417 bytes per frame.
                frames.AddRange(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
                frames.AddRange(Noise(413, 20 + i));
            }
            return Cat(id3, frames.ToArray());
        }

        public static byte[] Ogg()
        {
            byte[] Page(byte flags, byte[] body, int sequence)
            {
                var segments = (body.Length + 254) / 255;
                var table = Enumerable.Range(0, segments).Select(i => (byte)Math.Min(255, body.Length - i * 255)).ToArray();
                var h = new byte[27];
                A("OggS").CopyTo(h, 0);
                h[5] = flags;
                BitConverter.GetBytes(sequence).CopyTo(h, 18);
                h[26] = (byte)segments;
                return Cat(h, table, body);
            }
            return Cat(Page(0x02, Cat(A("\x01vorbis"), Noise(23, 30)), 0), Page(0, Noise(600, 31), 1), Page(0x04, Noise(300, 32), 2));
        }

        public static byte[] SevenZip()
        {
            var b = new byte[32 + 1000 + 40];
            new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(b, 0);
            BitConverter.GetBytes(1000L).CopyTo(b, 12);
            BitConverter.GetBytes(40L).CopyTo(b, 20);
            Noise(1040, 33).CopyTo(b, 32);
            return b;
        }

        public static byte[] Sqlite()
        {
            var b = new byte[3 * 1024];
            A("SQLite format 3\0").CopyTo(b, 0);
            BE16(1024).CopyTo(b, 16);
            BE32(7).CopyTo(b, 24);
            BE32(3).CopyTo(b, 28);
            BE32(7).CopyTo(b, 92);
            Noise(2000, 34).CopyTo(b, 100);
            return b;
        }

        public static byte[] Exe()
        {
            var b = new byte[0x600];
            A("MZ").CopyTo(b, 0);
            BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
            A("PE\0\0").CopyTo(b, 0x80);
            BitConverter.GetBytes((ushort)1).CopyTo(b, 0x86);
            BitConverter.GetBytes((ushort)224).CopyTo(b, 0x94);
            BitConverter.GetBytes((ushort)0x0102).CopyTo(b, 0x96);
            BitConverter.GetBytes((ushort)0x10B).CopyTo(b, 0x98);
            var section = 0x98 + 224;
            A(".text\0\0\0").CopyTo(b, section);
            BitConverter.GetBytes(0x200).CopyTo(b, section + 16);
            BitConverter.GetBytes(0x400).CopyTo(b, section + 20);
            Noise(0x200, 35).CopyTo(b, 0x400);
            return b;
        }

        public static byte[] Ttf()
        {
            var b = new byte[12 + 4 * 16 + 400];
            new byte[] { 0, 1, 0, 0 }.CopyTo(b, 0);
            BE16(4).CopyTo(b, 4);
            var tags = new[] { "cmap", "head", "hhea", "name" };
            for (var i = 0; i < 4; i++)
            {
                A(tags[i]).CopyTo(b, 12 + i * 16);
                BE32(76 + i * 100).CopyTo(b, 12 + i * 16 + 8);
                BE32(100).CopyTo(b, 12 + i * 16 + 12);
            }
            Noise(400, 36).CopyTo(b, 76);
            return b;
        }

        public static byte[] Webm()
        {
            var docType = Cat(new byte[] { 0x42, 0x82, 0x84 }, A("webm"));
            var header = Cat(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, (byte)(0x80 | docType.Length) }, docType);
            var body = Noise(3000, 37);
            return Cat(header, new byte[] { 0x18, 0x53, 0x80, 0x67, 0x40 | (3000 >> 8), (byte)(3000 & 0xFF) }, body);
        }

        public static byte[] Doc()
        {
            var b = new byte[512 * 4];
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(b, 0);
            BitConverter.GetBytes((ushort)9).CopyTo(b, 30);   // 512-byte sectors
            BitConverter.GetBytes(1).CopyTo(b, 44);           // one FAT sector
            BitConverter.GetBytes(1).CopyTo(b, 48);           // directory starts at sector 1
            BitConverter.GetBytes(unchecked((int)0xFFFFFFFE)).CopyTo(b, 68);
            BitConverter.GetBytes(0).CopyTo(b, 76);           // FAT in sector 0
            for (var i = 1; i < 109; i++) BitConverter.GetBytes(-1).CopyTo(b, 76 + i * 4);
            var fat = 512;
            for (var i = 0; i < 128; i++) BitConverter.GetBytes(-1).CopyTo(b, fat + i * 4);
            BitConverter.GetBytes(unchecked((int)0xFFFFFFFD)).CopyTo(b, fat);       // sector 0: the FAT
            BitConverter.GetBytes(unchecked((int)0xFFFFFFFE)).CopyTo(b, fat + 4);   // sector 1: directory
            BitConverter.GetBytes(unchecked((int)0xFFFFFFFE)).CopyTo(b, fat + 8);   // sector 2: data
            Encoding.Unicode.GetBytes("Root Entry\0\0WordDocument").CopyTo(b, 1024);
            Noise(400, 38).CopyTo(b, 1536);
            return b;
        }

        public static byte[] Vcf() => A("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Azeem\r\nEND:VCARD\r\nBEGIN:VCARD\r\nVERSION:3.0\r\nFN:Claude\r\nEND:VCARD\r\n");

        /// <summary>A Photoshop file: its length isn't recorded, so it runs up to the next file.</summary>
        public static byte[] Psd() => Cat(A("8BPS"), new byte[] { 0, 1 }, Noise(5000, 39));
    }
}
