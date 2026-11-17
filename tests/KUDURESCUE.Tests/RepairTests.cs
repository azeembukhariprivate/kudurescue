using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Kudurescue.Core;

namespace Kudurescue.Tests
{
    /// <summary>Repairing partly whole files: made-up photos and Office files, damaged on purpose as a reused cluster would.</summary>
    internal static class RepairTests
    {
        public static void Run(Action<bool, string> check)
        {
            Jpeg(check);
            Png(check);
            Word(check);
            Excel(check);
            PowerPoint(check);
            DamagedParts(check);
        }

        private static byte[] Noise(int n, int seed)
        {
            var b = new byte[n];
            new Random(seed).NextBytes(b);
            return b;
        }

        /// <summary>Puts other data over a part of a file, as when its clusters were reused; returns the damaged copy.</summary>
        private static byte[] Damage(byte[] data, long start, long length, int seed = 7)
        {
            var copy = (byte[])data.Clone();
            var noise = Noise((int)length, seed);
            Buffer.BlockCopy(noise, 0, copy, (int)start, (int)Math.Min(length, copy.Length - start));
            return copy;
        }

        // ------------------------------------------------------------------ JPEG

        private static byte[] Segment(byte marker, byte[] body) =>
            new byte[] { 0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2) }.Concat(body).ToArray();

        /// <summary>A JPEG's structure: header segments, then picture data with stuffed 0xFF bytes, then the end.</summary>
        public static byte[] JpegFile(int dataLength)
        {
            var head = new List<byte> { 0xFF, 0xD8 };
            head.AddRange(Segment(0xE0, Encoding.ASCII.GetBytes("JFIF\0\x01\x01\0\0\x01\0\x01\0\0")));
            head.AddRange(Segment(0xDB, new byte[65]));
            head.AddRange(Segment(0xC0, new byte[] { 8, 0, 64, 0, 64, 1, 1, 0x11, 0 }));
            head.AddRange(Segment(0xC4, new byte[29]));
            head.AddRange(Segment(0xDA, new byte[] { 1, 1, 0, 0, 63, 0 }));
            var picture = Noise(dataLength, 3).Select(b => b == 0xFF ? (byte)0x7F : b).ToArray();
            return head.Concat(picture).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
        }

        private static void Jpeg(Action<bool, string> check)
        {
            var photo = JpegFile(20000);
            var scan = photo.Length - 20000 - 2;
            check(Repair.Make(photo, ".jpg", null) == null, "a whole photo needs no repair");

            var damaged = Damage(photo, 12288, 4096);
            var repaired = Repair.Make(damaged, ".jpg", new List<(long, long)> { (12288, 4096) });
            check(repaired != null && repaired.Length == 12288 + 2, "cut where the damage starts: " + repaired?.Length);
            check(repaired.Take(12288).SequenceEqual(photo.Take(12288)), "everything before the damage kept");
            check(repaired[repaired.Length - 2] == 0xFF && repaired[repaired.Length - 1] == 0xD9, "closed properly");

            check(Repair.Make(Damage(photo, 0, 4096), ".jpg", new List<(long, long)> { (0, 4096) }) == null, "no repair when the header itself is lost");
            check(Repair.Make(Damage(photo, scan - 10, 4096), ".jpg", new List<(long, long)> { (scan - 10, 4096) }) == null, "or when the damage starts inside it");

            // An older saved scan doesn't know where the damage is: a photo ending in zeros is closed before them.
            var zeros = photo.Take(15000).Concat(new byte[5000]).ToArray();
            var closed = Repair.Make(zeros, ".jpeg", null);
            check(closed != null && closed.Length == 15002, "zeros at the end are cut off: " + closed?.Length);

            // A cut never leaves a lone 0xFF (half a marker) behind.
            var ff = (byte[])photo.Clone();
            ff[9999] = 0xFF;
            var noHalf = Repair.Make(ff, ".jpg", new List<(long, long)> { (10000, 4096) });
            check(noHalf != null && noHalf.Length == 9999 + 2, "no half marker: " + noHalf?.Length);
        }

        // ------------------------------------------------------------------ PNG

        private static byte[] Chunk(string type, byte[] data)
        {
            var head = new byte[8];
            Repair.WriteBig(head, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            var both = head.Skip(4).Concat(data).ToArray();
            var crc = new byte[4];
            Repair.WriteBig(crc, 0, Crc32.Of(both, 0, both.Length));
            return head.Concat(data).Concat(crc).ToArray();
        }

        /// <summary>An RGB picture whose rows are easy to check: row y has every pixel (y, 255 - y, 7).</summary>
        public static byte[] PngFile(int width, int height, int idatPieces)
        {
            var rows = new MemoryStream();
            for (var y = 0; y < height; y++)
            {
                rows.WriteByte(0);
                for (var x = 0; x < width; x++) { rows.WriteByte((byte)y); rows.WriteByte((byte)(255 - y)); rows.WriteByte(7); }
            }
            var z = Repair.Zlib(rows.ToArray());
            var ihdr = new byte[13];
            Repair.WriteBig(ihdr, 0, (uint)width);
            Repair.WriteBig(ihdr, 4, (uint)height);
            ihdr[8] = 8;
            ihdr[9] = 2;
            var file = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            file.AddRange(Chunk("IHDR", ihdr));
            var piece = (z.Length + idatPieces - 1) / idatPieces;
            for (var i = 0; i < z.Length; i += piece) file.AddRange(Chunk("IDAT", z.Skip(i).Take(piece).ToArray()));
            file.AddRange(Chunk("IEND", new byte[0]));
            return file.ToArray();
        }

        /// <summary>Reads back an RGB PNG written with no row filters (as the repair writes it): the rows' pixels.</summary>
        public static List<byte[]> PngRows(byte[] png, out int width, out int height)
        {
            width = (int)Repair.ReadBig(png, 16);
            height = (int)Repair.ReadBig(png, 20);
            var z = new MemoryStream();
            for (var pos = 8; pos + 12 <= png.Length;)
            {
                var length = (int)Repair.ReadBig(png, pos);
                var type = Encoding.ASCII.GetString(png, pos + 4, 4);
                if (Crc32.Of(png, pos + 4, length + 4) != Repair.ReadBig(png, pos + 8 + length)) throw new InvalidDataException("bad CRC in " + type);
                if (type == "IDAT") z.Write(png, pos + 8, length);
                pos += 12 + length;
            }
            var raw = Repair.Inflate(z.ToArray(), long.MaxValue);
            var rowBytes = width * 3 + 1;
            var rows = new List<byte[]>();
            for (var y = 0; y < height; y++) rows.Add(raw.Skip(y * rowBytes).Take(rowBytes).ToArray());
            return rows;
        }

        private static void Png(Action<bool, string> check)
        {
            var picture = PngFile(200, 300, 6);
            check(Repair.Make(picture, ".png", null) == null, "a whole picture needs no repair");

            // The fourth of six picture chunks is overwritten: the rows before it stay, the rest are grey.
            var chunks = new List<(int Start, int Length)>();
            for (var pos = 8; pos + 12 <= picture.Length;)
            {
                var length = (int)Repair.ReadBig(picture, pos);
                if (Encoding.ASCII.GetString(picture, pos + 4, 4) == "IDAT") chunks.Add((pos, length + 12));
                pos += 12 + length;
            }
            var hit = chunks[3];
            var damaged = Damage(picture, hit.Start + 20, 200);
            foreach (var known in new[] { new List<(long, long)> { (hit.Start + 20, 200) }, null })
            {
                var repaired = Repair.Make(damaged, ".png", known);
                check(repaired != null, "repaired (" + (known == null ? "found by its checksums" : "damage known") + ")");
                var rows = PngRows(repaired, out var w, out var h);
                check(w == 200 && h == 300, "the same size");
                var good = rows.TakeWhile(r => r.Length > 1 && !r.Skip(1).All(b => b == 0x80)).Count();
                check(good > 50 && good < 300, "some rows kept, not all: " + good);
                check(Enumerable.Range(0, good).All(y => rows[y][0] == 0 && rows[y][1] == (byte)y && rows[y][2] == (byte)(255 - y) && rows[y][3] == 7), "the kept rows are the picture's own");
                check(rows.Skip(good).All(r => r.Skip(1).All(b => b == 0x80)), "the rest grey");
            }

            // Cut short (the end overwritten with zeros, no IEND): the same.
            var shortened = picture.Take(chunks[4].Start).Concat(new byte[3000]).ToArray();
            check(Repair.Make(shortened, ".png", null) != null, "a picture cut short");
            check(Repair.Make(Damage(picture, 10, 20), ".png", new List<(long, long)> { (10, 20) }) == null, "no repair without the picture's sizes");
        }

        // ------------------------------------------------------------------ Office

        private static byte[] Zip(params (string Name, string Text)[] parts)
        {
            var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var (name, text) in parts)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using (var s = entry.Open())
                    {
                        var bytes = name.EndsWith(".png") ? Office.GreyPicture : Encoding.UTF8.GetBytes(text);
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
            return output.ToArray();
        }

        /// <summary>Where a part's data is in a ZIP (from its own header).</summary>
        private static (int Start, int Length) Where(byte[] zip, string name)
        {
            var key = Encoding.UTF8.GetBytes(name);
            for (var pos = 0; pos + 30 < zip.Length; pos++)
            {
                if (zip[pos] != 0x50 || zip[pos + 1] != 0x4B || zip[pos + 2] != 3 || zip[pos + 3] != 4) continue;
                var nameLength = zip[pos + 26] | zip[pos + 27] << 8;
                var extra = zip[pos + 28] | zip[pos + 29] << 8;
                if (!zip.Skip(pos + 30).Take(nameLength).SequenceEqual(key)) continue;
                var size = BitConverter.ToInt32(zip, pos + 18);
                return (pos + 30 + nameLength + extra, size);
            }
            throw new InvalidDataException(name + " not in the package");
        }

        private static Dictionary<string, string> Unzip(byte[] zip)
        {
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                return archive.Entries.ToDictionary(e => e.FullName, e =>
                {
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
                });
        }

        /// <summary>Every link in the package points at a part that's there, and every kind listed is of a part that's there.</summary>
        private static void Consistent(Action<bool, string> check, byte[] zip, string what)
        {
            var parts = Unzip(zip);
            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            foreach (var relsName in parts.Keys.Where(k => k.EndsWith(".rels")))
            {
                var owner = relsName == "_rels/.rels" ? string.Empty : relsName.Substring(0, relsName.Length - 5).Replace("_rels/", string.Empty);
                foreach (var r in XDocument.Parse(parts[relsName]).Root.Elements(rel + "Relationship"))
                {
                    if ((string)r.Attribute("TargetMode") == "External") continue;
                    var target = Office.Resolve(owner, (string)r.Attribute("Target"));
                    check(parts.ContainsKey(target), what + ": the link from " + relsName + " to " + target + " leads to a part");
                }
            }
            foreach (var o in XDocument.Parse(parts["[Content_Types].xml"]).Root.Elements(ct + "Override"))
                check(parts.ContainsKey(((string)o.Attribute("PartName")).TrimStart('/')), what + ": listed kind of a part that's there: " + (string)o.Attribute("PartName"));
            foreach (var xml in parts.Where(p => p.Key.EndsWith(".xml") || p.Key.EndsWith(".rels"))) XDocument.Parse(xml.Value); // all still XML
        }

        private const string Types = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>{0}</Types>";
        private static string Override(string part, string type) => "<Override PartName=\"/" + part + "\" ContentType=\"application/vnd.openxmlformats-officedocument." + type + "\"/>";
        private static string Rels(params (string Id, string Type, string Target)[] rels) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(rels.Select(r => "<Relationship Id=\"" + r.Id + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + r.Type + "\" Target=\"" + r.Target + "\"/>")) + "</Relationships>";

        private static void Word(Action<bool, string> check)
        {
            var paragraphs = string.Concat(Enumerable.Range(1, 400).Select(i => "<w:p><w:r><w:t>Paragraph number " + i + " &amp; more " + new string('x', i % 50) + "</w:t></w:r></w:p>"));
            var document = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>" +
                           paragraphs + "<w:p><w:r><w:drawing><a:blip xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" r:embed=\"rId3\"/></w:drawing></w:r></w:p>" +
                           "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId2\"/></w:sectPr></w:body></w:document>";
            var docx = Zip(
                ("[Content_Types].xml", string.Format(Types, Override("word/document.xml", "wordprocessingml.document.main+xml") + Override("word/styles.xml", "wordprocessingml.styles+xml") + Override("word/header1.xml", "wordprocessingml.header+xml"))),
                ("_rels/.rels", Rels(("rId1", "officeDocument", "word/document.xml"))),
                ("word/document.xml", document),
                ("word/_rels/document.xml.rels", Rels(("rId1", "styles", "styles.xml"), ("rId2", "header", "header1.xml"), ("rId3", "image", "media/image1.png"))),
                ("word/styles.xml", "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>"),
                ("word/header1.xml", "<w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:r><w:t>Header</w:t></w:r></w:p></w:hdr>"),
                ("word/media/image1.png", null));
            check(Repair.Make(docx, ".docx", null) == null, "a whole document needs no repair");

            // The end of the file is overwritten: the header, the picture and the list at the end are lost; the text is whole.
            var header = Where(docx, "word/header1.xml");
            var cut = header.Start - 40;
            var tail = Damage(docx, cut, docx.Length - cut);
            var repaired = Repair.Make(tail, ".docx", new List<(long, long)> { (cut, docx.Length - cut) });
            check(repaired != null, "a document whose end was lost is repaired");
            var parts = Unzip(repaired);
            check(parts["word/document.xml"].Contains("Paragraph number 400"), "all its text kept");
            check(!parts["word/document.xml"].Contains("headerReference"), "the link to the lost header taken out of the document");
            check(!parts.ContainsKey("word/header1.xml"), "the lost header isn't there");
            Consistent(check, repaired, "document with its end lost");

            // The middle of the text is overwritten: the paragraphs before it are kept in a new document.
            var body = Where(docx, "word/document.xml");
            var middle = body.Start + body.Length / 2;
            var hurt = Damage(docx, middle, 300);
            var fixedText = Repair.Make(hurt, ".docx", new List<(long, long)> { (middle, 300) });
            check(fixedText != null, "a document with damaged text is repaired");
            var text = Unzip(fixedText)["word/document.xml"];
            check(text.Contains("Paragraph number 1 &amp; more"), "the first paragraphs kept (and still written safely)");
            check(!text.Contains("Paragraph number 400"), "the text after the damage can't come back");
            Consistent(check, fixedText, "document with damaged text");

            check(Repair.Make(Damage(docx, body.Start, body.Length), ".docx", new List<(long, long)> { (body.Start, body.Length) }) == null, "no repair when all the text is lost");
        }

        private static void Excel(Action<bool, string> check)
        {
            const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:x14ac=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac\"";
            string Sheet(int first) => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet " + ns + "><sheetData>" +
                string.Concat(Enumerable.Range(1, 300).Select(i => "<row r=\"" + i + "\" x14ac:dyDescent=\"0.25\"><c r=\"A" + i + "\" s=\"1\"><v>" + (first + i) + "</v></c><c r=\"B" + i + "\" t=\"s\"><v>" + (i % 3) + "</v></c></row>")) +
                "</sheetData></worksheet>";
            var xlsx = Zip(
                ("[Content_Types].xml", string.Format(Types, Override("xl/workbook.xml", "spreadsheetml.sheet.main+xml") + Override("xl/worksheets/sheet1.xml", "spreadsheetml.worksheet+xml") +
                    Override("xl/worksheets/sheet2.xml", "spreadsheetml.worksheet+xml") + Override("xl/worksheets/sheet3.xml", "spreadsheetml.worksheet+xml") + Override("xl/sharedStrings.xml", "spreadsheetml.sharedStrings+xml") +
                    Override("xl/styles.xml", "spreadsheetml.styles+xml"))),
                ("_rels/.rels", Rels(("rId1", "officeDocument", "xl/workbook.xml"))),
                ("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook " + ns + "><sheets><sheet name=\"One\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Two\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Three\" sheetId=\"3\" r:id=\"rId3\"/></sheets><definedNames><definedName name=\"x\">Three!A1</definedName></definedNames></workbook>"),
                ("xl/_rels/workbook.xml.rels", Rels(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "worksheet", "worksheets/sheet2.xml"), ("rId3", "worksheet", "worksheets/sheet3.xml"), ("rId4", "sharedStrings", "sharedStrings.xml"), ("rId5", "styles", "styles.xml"))),
                ("xl/sharedStrings.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst " + ns + " count=\"3\" uniqueCount=\"3\"><si><t>zero</t></si><si><t>one</t></si><si><t>two</t></si></sst>"),
                ("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet " + ns + "/>"),
                ("xl/worksheets/sheet1.xml", Sheet(1000)),
                ("xl/worksheets/sheet2.xml", Sheet(2000)),
                ("xl/worksheets/sheet3.xml", Sheet(3000)));
            check(Repair.Make(xlsx, ".xlsx", null) == null, "a whole workbook needs no repair");

            // Sheet two damaged part-way, sheet three lost completely.
            var two = Where(xlsx, "xl/worksheets/sheet2.xml");
            var three = Where(xlsx, "xl/worksheets/sheet3.xml");
            var damaged = Damage(Damage(xlsx, two.Start + two.Length / 2, 200, 1), three.Start, three.Length, 2);
            var repaired = Repair.Make(damaged, ".xlsx", new List<(long, long)> { (two.Start + two.Length / 2, 200), (three.Start, three.Length) });
            check(repaired != null, "a workbook is repaired");
            var parts = Unzip(repaired);
            check(parts["xl/worksheets/sheet1.xml"].Contains("<v>1300</v>"), "a whole sheet kept as it was");
            var sheet2 = parts["xl/worksheets/sheet2.xml"];
            check(sheet2.Contains("<v>2001</v>") && !sheet2.Contains("<v>2300</v>"), "a damaged sheet keeps its rows before the damage");
            check(!sheet2.Contains("dyDescent"), "without attributes the new sheet can't declare");
            XDocument.Parse(sheet2);
            var workbook = parts["xl/workbook.xml"];
            check(workbook.Contains("\"One\"") && workbook.Contains("\"Two\"") && !workbook.Contains("\"Three\""), "the lost sheet left out of the workbook");
            check(!workbook.Contains("definedName"), "names that could point at it taken out");
            Consistent(check, repaired, "workbook");
        }

        private static void PowerPoint(Action<bool, string> check)
        {
            const string ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
            string Slide(int n) => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:sld " + ns + "><p:cSld><p:spTree><a:t>Slide " + n + " " + new string('s', 3000) + "</a:t></p:spTree></p:cSld></p:sld>";
            var pptx = Zip(
                ("[Content_Types].xml", string.Format(Types, Override("ppt/presentation.xml", "presentationml.presentation.main+xml") + string.Concat(Enumerable.Range(1, 3).Select(i => Override("ppt/slides/slide" + i + ".xml", "presentationml.slide+xml"))) +
                    Override("ppt/slideLayouts/slideLayout1.xml", "presentationml.slideLayout+xml") + Override("ppt/slideMasters/slideMaster1.xml", "presentationml.slideMaster+xml"))),
                ("_rels/.rels", Rels(("rId1", "officeDocument", "ppt/presentation.xml"))),
                ("ppt/presentation.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:presentation " + ns + "><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/><p:sldId id=\"258\" r:id=\"rId4\"/></p:sldIdLst></p:presentation>"),
                ("ppt/_rels/presentation.xml.rels", Rels(("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "slide", "slides/slide1.xml"), ("rId3", "slide", "slides/slide2.xml"), ("rId4", "slide", "slides/slide3.xml"))),
                ("ppt/slideMasters/slideMaster1.xml", "<p:sldMaster " + ns + "/>"),
                ("ppt/slideLayouts/slideLayout1.xml", "<p:sldLayout " + ns + "/>"),
                ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"))),
                ("ppt/slides/slide1.xml", Slide(1)), ("ppt/slides/_rels/slide1.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"))),
                ("ppt/slides/slide2.xml", Slide(2)), ("ppt/slides/_rels/slide2.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"))),
                ("ppt/slides/slide3.xml", Slide(3)), ("ppt/slides/_rels/slide3.xml.rels", Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"))));
            check(Repair.Make(pptx, ".pptx", null) == null, "a whole presentation needs no repair");

            var two = Where(pptx, "ppt/slides/slide2.xml");
            var repaired = Repair.Make(Damage(pptx, two.Start + 5, 50), ".pptx", new List<(long, long)> { (two.Start + 5, 50) });
            check(repaired != null, "a presentation is repaired");
            var parts = Unzip(repaired);
            check(parts.ContainsKey("ppt/slides/slide1.xml") && parts.ContainsKey("ppt/slides/slide3.xml") && !parts.ContainsKey("ppt/slides/slide2.xml"), "the damaged slide left out, the others kept");
            check(!parts["ppt/presentation.xml"].Contains("rId3") && parts["ppt/presentation.xml"].Contains("rId4"), "and taken out of the list of slides");
            Consistent(check, repaired, "presentation");
        }

        // ------------------------------------------------------------------ where the damage is

        private static void DamagedParts(Action<bool, string> check)
        {
            // Clusters 100-199 of a file at clusters 5000-5199 are used by other files now: 4096-byte clusters.
            var f = new FoundFile { Name = "x.jpg", Size = 200 * 4096, Runs = new List<DataRun> { new DataRun(0, 5000, 200) } };
            long Used(long lcn, long count)
            {
                long n = 0;
                for (var c = lcn; c < lcn + count; c++) if (c >= 5100 && c < 5150 || c == 5170) n++;
                return n;
            }
            QuickScan.SetChance(f, 4096, Used);
            check(f.Chance == Chance.Partial, "partly whole");
            var ranges = f.DamagedRanges;
            check(ranges != null && ranges.Count == 2, "two damaged pieces: " + string.Join(" ", ranges ?? new List<(long, long)>()));
            check(ranges[0] == (100L * 4096, 50L * 4096) && ranges[1] == (170L * 4096, 4096L), "in the right places");
        }
    }
}
