using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Kudurescue.Core;

namespace Kudurescue.Tests
{
    /// <summary>What the preview reads: music details and the words in Office files, from made-up files.</summary>
    internal static class PreviewTests
    {
        private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
        private static byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] Le(uint v) => BitConverter.GetBytes(v);
        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        private static byte[] Box(string type, params byte[][] body) { var b = Cat(body); return Cat(Be((uint)(b.Length + 8)), Encoding.Latin1.GetBytes(type), b); }
        private static MediaInfo Info(byte[] file, string ext) => MediaInfo.Read(new MemoryStream(file), ext);

        public static void Run(Action<bool, string> check)
        {
            // MP3: an ID3 tag with the name, singer and album, and a "Xing" header saying 1000 frames at 44.1 kHz (26.1 s).
            byte[] Frame(string id, string text) { var b = Cat(new byte[] { 3 }, U(text)); return Cat(A(id), Be((uint)b.Length), new byte[2], b); }
            var frames = Cat(Frame("TIT2", "Thalavattam"), Frame("TPE1", "Azeem"), Frame("TALB", "Kerala Songs"));
            var size = frames.Length;
            var id3 = Cat(A("ID3"), new byte[] { 3, 0, 0 }, new[] { (byte)(size >> 21 & 0x7F), (byte)(size >> 14 & 0x7F), (byte)(size >> 7 & 0x7F), (byte)(size & 0x7F) }, frames);
            var mpeg = Cat(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, new byte[32], A("Xing"), Be(1), Be(1000), new byte[400]);
            var mp3 = Info(Cat(id3, mpeg), ".mp3");
            check(mp3 != null && mp3.Title == "Thalavattam" && mp3.Artist == "Azeem" && mp3.Album == "Kerala Songs", "MP3 name, singer and album");
            check(Math.Abs(mp3.Length.TotalSeconds - 1000 * 1152 / 44100.0) < 0.01, "MP3 length from its frame count: " + mp3.Length);
            var plain = Info(Cat(mpeg.Take(4).ToArray(), new byte[16000]), ".mp3");
            check(plain != null && Math.Abs(plain.Length.TotalSeconds - 16004 * 8 / 128000.0) < 0.01, "MP3 length from its bit rate: " + plain?.Length);

            // M4A with its details at the end, after the sound (as many phones write it): 3 min 7 s.
            var mvhd = Box("mvhd", new byte[4], new byte[8], Be(1000), Be(187000), new byte[80]);
            byte[] Item(string type, string text) => Box(type, Box("data", Be(1), Be(0), U(text)));
            var ilst = Box("ilst", Item("©nam", "Monsoon"), Item("©ART", "Inaara"), Item("©alb", "Rain"));
            var meta = Box("meta", new byte[4], Box("hdlr", new byte[25]), ilst);
            var m4a = Cat(Box("ftyp", A("M4A "), new byte[4]), Box("mdat", new byte[50000]), Box("moov", mvhd, Box("udta", meta)));
            var mp4 = Info(m4a, ".m4a");
            check(mp4 != null && mp4.Title == "Monsoon" && mp4.Artist == "Inaara" && mp4.Album == "Rain", "M4A details, after the sound");
            check(Math.Abs(mp4.Length.TotalSeconds - 187) < 0.01 && MediaInfo.LengthText(mp4.Length) == "3:07", "M4A length: " + mp4.Length);
            check(MediaInfo.LengthText(TimeSpan.FromSeconds(3723)) == "1:02:03", "an hour or more");

            // FLAC: 48 kHz, 480000 samples (10 s), and its comments.
            var streaminfo = new byte[34];
            var rate = 48000;
            streaminfo[10] = (byte)(rate >> 12); streaminfo[11] = (byte)(rate >> 4); streaminfo[12] = (byte)((rate & 0xF) << 4);
            Buffer.BlockCopy(Be(480000), 0, streaminfo, 14, 4);
            byte[] Comment(string s) => Cat(Le((uint)U(s).Length), U(s));
            var comments = Cat(Comment("maker"), Le(2), Comment("TITLE=Onam"), Comment("ARTIST=Aiza"));
            var flac = Info(Cat(A("fLaC"), new byte[] { 0, 0, 0, 34 }, streaminfo, new byte[] { 0x84, 0, 0, (byte)comments.Length }, comments), ".flac");
            check(flac != null && flac.Title == "Onam" && flac.Artist == "Aiza" && Math.Abs(flac.Length.TotalSeconds - 10) < 0.01, "FLAC details and length");

            // WAV: 16-bit stereo 44.1 kHz, 2 s of sound.
            var fmt = Cat(A("fmt "), Le(16), new byte[] { 1, 0, 2, 0 }, Le(44100), Le(176400), new byte[] { 4, 0, 16, 0 });
            var data = Cat(A("data"), Le(352800), new byte[352800]);
            var wav = Info(Cat(A("RIFF"), Le((uint)(4 + fmt.Length + data.Length)), A("WAVE"), fmt, data), ".wav");
            check(wav != null && Math.Abs(wav.Length.TotalSeconds - 2) < 0.01, "WAV length");

            check(Info(new byte[100], ".mp3") == null && Info(new byte[100], ".flac") == null, "nothing made up for a file that isn't one");
            check(MediaInfo.Read(new MemoryStream(new byte[10]), ".xyz") == null, "unknown kinds");

            // Office files: the words in them.
            check(OfficeText.Read(Zip(("word/document.xml", "<w:document><w:body><w:p><w:r><w:t>Dear Azeem,</w:t></w:r></w:p><w:p><w:r><w:t>Tea &amp; snacks</w:t></w:r><w:r><w:tab/><w:t>at four.</w:t></w:r></w:p></w:body></w:document>")), ".docx")
                  == "Dear Azeem,\nTea & snacks at four.", "Word paragraphs");
            var xlsx = Zip(("xl/sharedStrings.xml", "<sst><si><t>Name</t></si><si><t>Total</t></si></sst>"),
                ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Rice</t></is></c><c r=\"B2\"><v>42</v></c></row></sheetData></worksheet>"));
            check(OfficeText.Read(xlsx, ".xlsx") == "Name\tTotal\nRice\t42", "Excel rows, shared and inline texts: " + OfficeText.Read(xlsx, ".xlsx"));
            var pptx = Zip(("ppt/slides/slide10.xml", "<p:sld><a:t>Ten</a:t></p:sld>"), ("ppt/slides/slide2.xml", "<p:sld><a:t>Two</a:t><a:t>words</a:t></p:sld>"));
            check(OfficeText.Read(pptx, ".pptx") == "Two words\n\nTen", "slides in order: " + OfficeText.Read(pptx, ".pptx"));
            var cut = Zip(("word/document.xml", "<w:document><w:body><w:p><w:r><w:t>Still here</w:t></w:r></w:p></w:body></w:document>"));
            check(OfficeText.Read(cut.Take(cut.Length - 30).ToArray(), ".docx") == "Still here", "read from the parts' own headers when the end is lost");
            var doc = Cat(new byte[512], Encoding.Unicode.GetBytes("An old Word document about the boat race."), new byte[512]);
            check((OfficeText.Read(doc, ".doc") ?? string.Empty).Contains("An old Word document about the boat race."), "older Word files: the words inside");
            check(OfficeText.Read(A(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello\par world}"), ".rtf") == "Hello\nworld", "RTF: " + OfficeText.Read(A(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello\par world}"), ".rtf"));
            check(OfficeText.Read(new byte[300], ".docx") == null, "nothing for a file with no words");
        }

        private static byte[] Zip(params (string Name, string Text)[] parts)
        {
            var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var (name, text) in parts)
                    using (var s = archive.CreateEntry(name).Open()) { var b = U(text); s.Write(b, 0, b.Length); }
            return output.ToArray();
        }
    }
}
