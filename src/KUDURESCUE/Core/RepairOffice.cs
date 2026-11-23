// Repairing damaged Word, Excel and PowerPoint files: their parts found by their own headers and checked by CRC, and
// the file put together again without what was lost.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kudurescue.Core
{
    /// <summary>
    /// Word, Excel and PowerPoint files (.docx, .xlsx, .pptx) are ZIP packages of parts. Each part is found from its own
    /// header (the list at the end of a ZIP is often what got overwritten) and checked against its own checksum. The
    /// package is then put together again from the whole parts: links to lost parts are taken out (a lost picture becomes
    /// a grey one), a damaged Word document keeps the text before the damage, a damaged sheet keeps its whole rows, and
    /// lost slides or sheets are left out.
    /// </summary>
    public static class Office
    {
        /// <summary>The namespace of the package's own list of links.</summary>
        private const string PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
        /// <summary>The namespace of the list of part kinds.</summary>
        private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        /// <summary>The kind of link to the main document.</summary>
        private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        /// <summary>Word's XML namespace.</summary>
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        /// <summary>Excel's XML namespace.</summary>
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        /// <summary>PowerPoint's XML namespace.</summary>
        private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        /// <summary>The namespace of links between parts.</summary>
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        /// <summary>The same, as an XML namespace.</summary>
        private static readonly XNamespace Rel = PackageRels;
        /// <summary>The namespace of the list of part kinds.</summary>
        private static readonly XNamespace Ct = ContentTypesNs;

        /// <summary>A part read from a damaged package: whole (checksum right) or only its readable start.</summary>
        public sealed class Part
        {
            /// <summary>The part's name.</summary>
            public string Name;
            /// <summary>Its bytes, as unpacked.</summary>
            public byte[] Data;
            /// <summary>True when it unpacked whole and its CRC is right.</summary>
            public bool Whole;
        }

        /// <summary>The most one part is unpacked to, and all the parts together: a file made to swell when unpacked can't fill the memory.</summary>
        public const long MaxPart = 64L << 20, MaxTotal = 512L << 20;

        /// <summary>Every part that can be found from its own header, whole or not (the last copy of a name wins if whole).</summary>
        public static Dictionary<string, Part> Salvage(byte[] zip, List<(long Start, long Length)> damaged)
        {
            var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
            long budget = MaxTotal;
            for (var pos = 0; pos + 30 <= zip.Length && budget > 0; pos++)
            {
                if (zip[pos] != 0x50 || zip[pos + 1] != 0x4B || zip[pos + 2] != 3 || zip[pos + 3] != 4) continue;
                var flags = U16(zip, pos + 6);
                var method = U16(zip, pos + 8);
                var crc = U32(zip, pos + 14);
                long size = U32(zip, pos + 18);
                long full = U32(zip, pos + 22);
                var nameLength = U16(zip, pos + 26);
                var extraLength = U16(zip, pos + 28);
                var dataStart = pos + 30 + nameLength + extraLength;
                if (nameLength == 0 || nameLength > 1024 || dataStart > zip.Length || (method != 0 && method != 8)) continue;
                var name = Encoding.UTF8.GetString(zip, pos + 30, nameLength).Replace('\\', '/').TrimStart('/');
                if (name.EndsWith("/")) continue;
                if ((flags & 8) != 0 && size == 0)
                {
                    // The sizes come after the data, in a "data descriptor".
                    var d = Find(zip, new byte[] { 0x50, 0x4B, 7, 8 }, dataStart);
                    if (d < 0 || d + 16 > zip.Length) continue;
                    crc = U32(zip, d + 4);
                    size = U32(zip, d + 8);
                    full = U32(zip, d + 12);
                }
                var available = (int)Math.Max(0, Math.Min(size, zip.Length - dataStart));
                var hit = damaged.Any(r => r.Start < dataStart + size && r.Start + r.Length > pos);
                byte[] content;
                bool whole;
                var most = Math.Min(Math.Min(full > 0 ? full : MaxPart, MaxPart), budget);
                if (method == 8) content = Inflate(zip, dataStart, available, most);
                else
                {
                    content = new byte[available];
                    Buffer.BlockCopy(zip, dataStart, content, 0, available);
                }
                budget -= content.Length;
                whole = !hit && available == size && content.Length == full && Crc32.Of(content, 0, content.Length) == crc;
                if (!whole && hit)
                {
                    // Keep only what comes before the damage.
                    var first = damaged.Where(r => r.Start + r.Length > dataStart).Select(r => r.Start).DefaultIfEmpty(long.MaxValue).Min();
                    if (first <= dataStart) content = new byte[0];
                    else if (method == 0) content = content.Take((int)Math.Min(content.Length, first - dataStart)).ToArray();
                    else content = Inflate(zip, dataStart, (int)Math.Min(available, first - dataStart), most);
                }
                if (!parts.TryGetValue(name, out var had) || (!had.Whole && (whole || content.Length > had.Data.Length)))
                    parts[name] = new Part { Name = name, Data = content, Whole = whole };
                if (whole && method == 8) pos = dataStart + (int)size - 1;
            }
            return parts;
        }

        /// <summary>Unpacks a part (no more than max bytes).</summary>
        private static byte[] Inflate(byte[] zip, int start, int length, long max)
        {
            var output = new MemoryStream();
            using (var input = new MemoryStream(zip, start, length))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[1 << 16];
                try
                {
                    while (output.Length < max)
                    {
                        var n = deflate.Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;
                        output.Write(buffer, 0, n);
                    }
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
                catch (ArgumentException) { }
                catch (IndexOutOfRangeException) { }
            }
            return output.ToArray();
        }

        /// <summary>A repaired package, or null when the main part (the document, workbook or presentation) is lost.</summary>
        public static byte[] Make(byte[] data, string extension, List<(long Start, long Length)> damaged)
        {
            var parts = Salvage(data, damaged);
            if (parts.Count == 0) return null;
            var whole = parts.Values.Where(p => p.Whole).ToDictionary(p => p.Name, p => p.Data, StringComparer.OrdinalIgnoreCase);
            var ext = extension.ToLowerInvariant();
            var main = ext.StartsWith(".doc") ? "word/document.xml" : ext.StartsWith(".xls") ? "xl/workbook.xml" : "ppt/presentation.xml";
            var mainName = MainPart(whole) ?? main;
            if (!whole.ContainsKey(mainName) && !parts.ContainsKey(mainName)) return null;

            var changed = false;
            if (ext.StartsWith(".doc")) changed |= FixWord(parts, whole, mainName);
            else if (ext.StartsWith(".xls")) changed |= FixExcel(parts, whole, mainName);
            else changed |= FixPowerPoint(whole, mainName);
            if (!whole.ContainsKey(mainName)) return null;

            changed |= FixLinks(whole);
            changed |= FixContentTypes(whole, mainName);
            if (!whole.ContainsKey("_rels/.rels"))
            {
                whole["_rels/.rels"] = Xml(new XDocument(new XElement(Rel + "Relationships",
                    new XElement(Rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", OfficeDocument), new XAttribute("Target", mainName)))));
                changed = true;
            }
            // Nothing was lost: the file only needed its list at the end written again.
            if (!changed && whole.Count == parts.Count && IsReadableZip(data)) return null;
            return Pack(whole);
        }

        /// <summary>True when the bytes open as a whole ZIP.</summary>
        private static bool IsReadableZip(byte[] data)
        {
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                {
                    foreach (var e in archive.Entries)
                        using (var s = e.Open()) s.CopyTo(Stream.Null);
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException || ex is ArgumentException) { return false; }
        }

        /// <summary>The main part named by the package's own list of links, if that's whole.</summary>
        private static string MainPart(Dictionary<string, byte[]> whole)
        {
            if (!whole.TryGetValue("_rels/.rels", out var rels)) return null;
            var doc = Parse(rels);
            var main = doc?.Root?.Elements(Rel + "Relationship").FirstOrDefault(e => (string)e.Attribute("Type") == OfficeDocument);
            return main == null ? null : ((string)main.Attribute("Target")).TrimStart('/');
        }

        // ------------------------------------------------------------------ Word

        /// <summary>A damaged Word document: keeps the text that's whole.</summary>
        private static bool FixWord(Dictionary<string, Part> parts, Dictionary<string, byte[]> whole, string main)
        {
            if (whole.ContainsKey(main)) return false;
            if (!parts.TryGetValue(main, out var part) || part.Data.Length == 0) return false;
            // The text before the damage, paragraph by paragraph, in a plain new document.
            var text = Encoding.UTF8.GetString(part.Data);
            var paragraphs = new List<string>();
            foreach (Match p in Regex.Matches(text, @"<w:p[\s>][\s\S]*?(?:</w:p>|$)"))
            {
                var runs = Regex.Matches(p.Value, @"<w:t(?:\s[^>]*)?>([^<]*)</w:t>").Cast<Match>().Select(m => Unescape(m.Groups[1].Value));
                paragraphs.Add(string.Concat(runs));
            }
            if (paragraphs.All(string.IsNullOrWhiteSpace)) return false;
            var body = new XElement(W + "body", paragraphs.Select(t => new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), t)))));
            whole[main] = Xml(new XDocument(new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R), body)));
            return true;
        }

        // ------------------------------------------------------------------ Excel

        /// <summary>
        /// A damaged Excel workbook: keeps the whole rows of damaged sheets, and leaves out lost sheets.
        /// </summary>
        private static bool FixExcel(Dictionary<string, Part> parts, Dictionary<string, byte[]> whole, string main)
        {
            if (!whole.TryGetValue(main, out var workbookBytes)) return false;
            var workbook = Parse(workbookBytes);
            if (workbook == null) return false;
            var relsName = RelsOf(main);
            var rels = whole.TryGetValue(relsName, out var rb) ? Parse(rb) : null;
            if (rels == null) return false;
            var changed = false;

            // Shared texts: whole, or the ones before the damage.
            var stringsRel = rels.Root.Elements(Rel + "Relationship").FirstOrDefault(e => ((string)e.Attribute("Type") ?? string.Empty).EndsWith("/sharedStrings"));
            var stringsName = stringsRel == null ? null : Resolve(main, (string)stringsRel.Attribute("Target"));
            var stringCount = int.MaxValue;
            if (stringsName != null && !whole.ContainsKey(stringsName))
            {
                var items = parts.TryGetValue(stringsName, out var sp) ? CompleteElements(Encoding.UTF8.GetString(sp.Data), "si") : new List<string>();
                stringCount = items.Count;
                whole[stringsName] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst xmlns=\"" + S.NamespaceName + "\" count=\"" + items.Count + "\" uniqueCount=\"" + items.Count + "\">" + string.Concat(items) + "</sst>");
                changed = true;
            }
            var stylesRel = rels.Root.Elements(Rel + "Relationship").FirstOrDefault(e => ((string)e.Attribute("Type") ?? string.Empty).EndsWith("/styles"));
            var stylesLost = stylesRel != null && !whole.ContainsKey(Resolve(main, (string)stylesRel.Attribute("Target")));

            var sheets = workbook.Root.Element(S + "sheets");
            foreach (var sheet in sheets?.Elements(S + "sheet").ToList() ?? new List<XElement>())
            {
                var id = (string)sheet.Attribute(R + "id");
                var rel = rels.Root.Elements(Rel + "Relationship").FirstOrDefault(e => (string)e.Attribute("Id") == id);
                var name = rel == null ? null : Resolve(main, (string)rel.Attribute("Target"));
                if (name != null && whole.ContainsKey(name) && !stylesLost && stringCount == int.MaxValue) continue;
                var source = name != null && whole.TryGetValue(name, out var wb) ? Encoding.UTF8.GetString(wb) : name != null && parts.TryGetValue(name, out var sp2) ? Encoding.UTF8.GetString(sp2.Data) : string.Empty;
                var rows = CompleteElements(source, "row");
                if (rows.Count == 0 && (name == null || !whole.ContainsKey(name)))
                {
                    // Nothing of this sheet is left: it's left out.
                    sheet.Remove();
                    rel?.Remove();
                    if (name != null) whole.Remove(name);
                    changed = true;
                    continue;
                }
                var cleaned = rows.Select(r => CleanRow(r, stylesLost, stringCount, whole.ContainsKey(stringsName ?? "?"))).Where(r => r != null);
                whole[name] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"" + S.NamespaceName + "\" xmlns:r=\"" + R.NamespaceName + "\"><sheetData>" + string.Concat(cleaned) + "</sheetData></worksheet>");
                RemoveRels(whole, name); // its drawings, comments and tables went with the old sheet
                changed = true;
            }
            if (sheets == null || !sheets.Elements(S + "sheet").Any()) { whole.Remove(main); return true; }
            if (changed)
            {
                // Names and the calculation order may point at what was left out; Excel makes both again.
                workbook.Root.Element(S + "definedNames")?.Remove();
                var calc = rels.Root.Elements(Rel + "Relationship").Where(e => ((string)e.Attribute("Type") ?? string.Empty).EndsWith("/calcChain")).ToList();
                foreach (var c in calc)
                {
                    whole.Remove(Resolve(main, (string)c.Attribute("Target")));
                    c.Remove();
                }
                whole[main] = Xml(workbook);
                whole[relsName] = Xml(rels);
            }
            return changed;
        }

        /// <summary>A row without what would point at lost parts: style numbers when the styles are lost, shared texts past the ones kept.</summary>
        private static string CleanRow(string row, bool stylesLost, int stringCount, bool haveStrings)
        {
            // Attributes from Office's later additions ("x14ac:dyDescent") need declarations the new sheet doesn't have.
            row = Regex.Replace(row, @"\s(?!xml:)[A-Za-z][\w.-]*:[A-Za-z][\w.-]*=""[^""]*""", string.Empty);
            if (!stylesLost && stringCount == int.MaxValue) return row;
            var x = XElement.Parse(row.Replace("<row", "<row xmlns=\"" + S.NamespaceName + "\"").Replace("xmlns=\"" + S.NamespaceName + "\" xmlns=\"" + S.NamespaceName + "\"", "xmlns=\"" + S.NamespaceName + "\""));
            foreach (var e in x.DescendantsAndSelf()) if (stylesLost) e.Attribute("s")?.Remove();
            foreach (var c in x.Elements(S + "c").ToList())
            {
                if ((string)c.Attribute("t") != "s") continue;
                if (!haveStrings || !int.TryParse((string)c.Element(S + "v"), out var n) || n >= stringCount) c.Remove();
            }
            return x.ToString(SaveOptions.DisableFormatting).Replace(" xmlns=\"" + S.NamespaceName + "\"", string.Empty);
        }

        // ------------------------------------------------------------------ PowerPoint

        /// <summary>A damaged PowerPoint presentation: leaves out lost slides.</summary>
        private static bool FixPowerPoint(Dictionary<string, byte[]> whole, string main)
        {
            if (!whole.TryGetValue(main, out var bytes)) return false;
            var presentation = Parse(bytes);
            var relsName = RelsOf(main);
            var rels = whole.TryGetValue(relsName, out var rb) ? Parse(rb) : null;
            if (presentation == null || rels == null) return false;
            var changed = false;
            // A slide counts when it's whole and so is its layout (and the layout's master).
            foreach (var slide in presentation.Root.Element(P + "sldIdLst")?.Elements(P + "sldId").ToList() ?? new List<XElement>())
            {
                var id = (string)slide.Attribute(R + "id");
                var rel = rels.Root.Elements(Rel + "Relationship").FirstOrDefault(e => (string)e.Attribute("Id") == id);
                var name = rel == null ? null : Resolve(main, (string)rel.Attribute("Target"));
                if (name != null && whole.ContainsKey(name) && LayoutWhole(whole, name)) continue;
                slide.Remove();
                rel?.Remove();
                if (name != null) { whole.Remove(name); whole.Remove(RelsOf(name)); }
                changed = true;
            }
            var masters = presentation.Root.Element(P + "sldMasterIdLst")?.Elements(P + "sldMasterId").ToList() ?? new List<XElement>();
            foreach (var m in masters)
            {
                var rel = rels.Root.Elements(Rel + "Relationship").FirstOrDefault(e => (string)e.Attribute("Id") == (string)m.Attribute(R + "id"));
                if (rel == null || !whole.ContainsKey(Resolve(main, (string)rel.Attribute("Target")))) { whole.Remove(main); return true; }
            }
            if (!(presentation.Root.Element(P + "sldIdLst")?.Elements(P + "sldId").Any() ?? false)) { whole.Remove(main); return true; }
            if (changed)
            {
                whole[main] = Xml(presentation);
                whole[relsName] = Xml(rels);
            }
            return changed;
        }

        /// <summary>Puts in whatever parts every file of its kind needs (taken from a plain one).</summary>
        private static bool LayoutWhole(Dictionary<string, byte[]> whole, string slide)
        {
            if (!whole.TryGetValue(RelsOf(slide), out var rb)) return false;
            var rels = Parse(rb);
            var layout = rels?.Root?.Elements(Rel + "Relationship").FirstOrDefault(e => ((string)e.Attribute("Type") ?? string.Empty).EndsWith("/slideLayout"));
            if (layout == null) return false;
            var name = Resolve(slide, (string)layout.Attribute("Target"));
            return whole.ContainsKey(name);
        }

        // ------------------------------------------------------------------ the package

        /// <summary>Every link to a lost part is taken out; a lost picture is pointed at a grey picture instead.</summary>
        private static bool FixLinks(Dictionary<string, byte[]> whole)
        {
            var changed = false;
            foreach (var relsName in whole.Keys.Where(k => k.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var rels = Parse(whole[relsName]);
                if (rels?.Root == null) { whole.Remove(relsName); changed = true; continue; }
                var owner = OwnerOf(relsName);
                if (owner.Length > 0 && !whole.ContainsKey(owner)) { whole.Remove(relsName); changed = true; continue; }
                var edited = false;
                var removed = new HashSet<string>();
                foreach (var r in rels.Root.Elements(Rel + "Relationship").ToList())
                {
                    if ((string)r.Attribute("TargetMode") == "External") continue;
                    var target = Resolve(owner.Length == 0 ? string.Empty : owner, (string)r.Attribute("Target") ?? string.Empty);
                    if (whole.ContainsKey(target)) continue;
                    if (((string)r.Attribute("Type") ?? string.Empty).EndsWith("/image"))
                    {
                        var dir = target.Contains("/") ? target.Substring(0, target.LastIndexOf('/') + 1) : string.Empty;
                        var grey = dir + "kudurescue-lost-picture.png";
                        whole[grey] = GreyPicture;
                        r.SetAttributeValue("Target", Relative(owner, grey));
                    }
                    else
                    {
                        removed.Add((string)r.Attribute("Id"));
                        r.Remove();
                    }
                    edited = true;
                }
                if (edited) { whole[relsName] = Xml(rels); changed = true; }
                // What pointed at a removed link goes too (a header, a footer, a comment), or Office would call the file damaged.
                if (removed.Count > 0 && owner.Length > 0 && whole.TryGetValue(owner, out var ownerBytes))
                {
                    var doc = Parse(ownerBytes);
                    if (doc?.Root == null) continue;
                    var pointing = doc.Root.Descendants().Where(e => e.Attributes().Any(a => a.Name.Namespace == R && removed.Contains(a.Value))).ToList();
                    foreach (var e in pointing) if (e.Parent != null) e.Remove();
                    if (pointing.Count > 0) whole[owner] = Xml(doc);
                }
            }
            return changed;
        }

        /// <summary>The list of part kinds: kept when whole (without entries for lost parts), otherwise made from the part names.</summary>
        private static bool FixContentTypes(Dictionary<string, byte[]> whole, string main)
        {
            const string name = "[Content_Types].xml";
            var types = whole.TryGetValue(name, out var tb) ? Parse(tb) : null;
            var changed = false;
            if (types?.Root == null)
            {
                types = new XDocument(new XElement(Ct + "Types",
                    new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(Ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml"))));
                foreach (var part in whole.Keys.Where(k => k.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var type = KnownType(part, main);
                    if (type != null) types.Root.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/" + part), new XAttribute("ContentType", type)));
                }
                changed = true;
            }
            foreach (var o in types.Root.Elements(Ct + "Override").ToList())
                if (!whole.ContainsKey(((string)o.Attribute("PartName") ?? string.Empty).TrimStart('/'))) { o.Remove(); changed = true; }
            var defaults = new HashSet<string>(types.Root.Elements(Ct + "Default").Select(d => ((string)d.Attribute("Extension") ?? string.Empty).ToLowerInvariant()));
            foreach (var ext in whole.Keys.Select(k => Path.GetExtension(k).TrimStart('.').ToLowerInvariant()).Distinct())
            {
                var media = ext == "png" ? "image/png" : ext == "jpeg" || ext == "jpg" ? "image/jpeg" : ext == "gif" ? "image/gif" : ext == "emf" ? "image/x-emf" : ext == "wmf" ? "image/x-wmf" : null;
                if (media != null && !defaults.Contains(ext))
                {
                    types.Root.AddFirst(new XElement(Ct + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", media)));
                    changed = true;
                }
            }
            if (changed || !whole.ContainsKey(name)) whole[name] = Xml(types);
            return changed;
        }

        /// <summary>A part's kind (for the list of part kinds), from its name.</summary>
        private static string KnownType(string part, string main)
        {
            var p = part.ToLowerInvariant();
            const string ml = "application/vnd.openxmlformats-officedocument.";
            if (p == main.ToLowerInvariant())
                return p.StartsWith("word/") ? ml + "wordprocessingml.document.main+xml" : p.StartsWith("xl/") ? ml + "spreadsheetml.sheet.main+xml" : ml + "presentationml.presentation.main+xml";
            if (Regex.IsMatch(p, @"^xl/worksheets/[^/]+\.xml$")) return ml + "spreadsheetml.worksheet+xml";
            if (p == "xl/sharedstrings.xml") return ml + "spreadsheetml.sharedStrings+xml";
            if (p == "xl/styles.xml") return ml + "spreadsheetml.styles+xml";
            if (Regex.IsMatch(p, @"^ppt/slides/[^/]+\.xml$")) return ml + "presentationml.slide+xml";
            if (Regex.IsMatch(p, @"^ppt/slidelayouts/[^/]+\.xml$")) return ml + "presentationml.slideLayout+xml";
            if (Regex.IsMatch(p, @"^ppt/slidemasters/[^/]+\.xml$")) return ml + "presentationml.slideMaster+xml";
            if (Regex.IsMatch(p, @"/theme/[^/]+\.xml$")) return ml + "theme+xml";
            if (p == "word/styles.xml") return ml + "wordprocessingml.styles+xml";
            if (p == "word/settings.xml") return ml + "wordprocessingml.settings+xml";
            if (p == "word/fonttable.xml") return ml + "wordprocessingml.fontTable+xml";
            if (p == "word/numbering.xml") return ml + "wordprocessingml.numbering+xml";
            if (p == "docprops/core.xml") return "application/vnd.openxmlformats-package.core-properties+xml";
            if (p == "docprops/app.xml") return ml + "extended-properties+xml";
            return null;
        }

        /// <summary>Packs the parts into a ZIP.</summary>
        private static byte[] Pack(Dictionary<string, byte[]> whole)
        {
            var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                // The list of kinds first, as Office writes it.
                foreach (var name in whole.Keys.OrderBy(k => k == "[Content_Types].xml" ? 0 : k == "_rels/.rels" ? 1 : 2).ThenBy(k => k, StringComparer.Ordinal))
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using (var s = entry.Open()) s.Write(whole[name], 0, whole[name].Length);
                }
            }
            return output.ToArray();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The whole &lt;tag&gt;…&lt;/tag&gt; elements in a piece of XML that may stop part-way.</summary>
        private static List<string> CompleteElements(string xml, string tag) =>
            Regex.Matches(xml, "<" + tag + @"(?:\s[^>]*)?(?:/>|>[\s\S]*?</" + tag + ">)").Cast<Match>().Select(m => m.Value).ToList();

        /// <summary>The links file of a part.</summary>
        private static string RelsOf(string part)
        {
            var i = part.LastIndexOf('/');
            return i < 0 ? "_rels/" + part + ".rels" : part.Substring(0, i) + "/_rels/" + part.Substring(i + 1) + ".rels";
        }

        /// <summary>The part a links file belongs to.</summary>
        private static string OwnerOf(string rels)
        {
            if (string.Equals(rels, "_rels/.rels", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var name = rels.Substring(0, rels.Length - 5); // without ".rels"
            return name.Replace("_rels/", string.Empty);
        }

        /// <summary>Removes the links to a part that was lost.</summary>
        private static void RemoveRels(Dictionary<string, byte[]> whole, string part) => whole.Remove(RelsOf(part));

        /// <summary>A link's target as a part name, from the part that holds the link.</summary>
        public static string Resolve(string owner, string target)
        {
            if (string.IsNullOrEmpty(target)) return string.Empty;
            if (target.StartsWith("/")) return target.TrimStart('/');
            var dir = owner.Contains("/") ? owner.Substring(0, owner.LastIndexOf('/')) : string.Empty;
            var parts = (dir.Length == 0 ? new string[0] : dir.Split('/')).ToList();
            foreach (var piece in target.Split('/'))
            {
                if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (piece != "." && piece.Length > 0) parts.Add(piece);
            }
            return string.Join("/", parts);
        }

        /// <summary>A link's target from the part that links to it.</summary>
        private static string Relative(string owner, string target)
        {
            var dir = owner.Contains("/") ? owner.Substring(0, owner.LastIndexOf('/') + 1) : string.Empty;
            return dir.Length > 0 && target.StartsWith(dir) ? target.Substring(dir.Length) : "/" + target;
        }

        /// <summary>XML from bytes (no DTDs).</summary>
        private static XDocument Parse(byte[] bytes)
        {
            try { return XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿')); }
            catch (System.Xml.XmlException) { return null; }
        }

        /// <summary>XML as bytes.</summary>
        private static byte[] Xml(XDocument doc)
        {
            var s = new MemoryStream();
            using (var w = System.Xml.XmlWriter.Create(s, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false) })) doc.Save(w);
            return s.ToArray();
        }

        /// <summary>Text with XML's escapes turned back.</summary>
        private static string Unescape(string s) => System.Net.WebUtility.HtmlDecode(s);

        /// <summary>A 2-byte number, smallest byte first.</summary>
        private static int U16(byte[] b, int p) => b[p] | b[p + 1] << 8;
        /// <summary>A 4-byte number, smallest byte first.</summary>
        private static uint U32(byte[] b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);

        /// <summary>Where the bytes are found in data, from a place, or -1.</summary>
        private static int Find(byte[] data, byte[] what, int from)
        {
            for (var i = from; i + what.Length <= data.Length; i++)
            {
                var ok = true;
                for (var k = 0; k < what.Length && ok; k++) ok = data[i + k] == what[k];
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>A small grey picture standing in for a lost one.</summary>
        public static byte[] GreyPicture
        {
            get
            {
                var rows = new MemoryStream();
                for (var y = 0; y < 16; y++)
                {
                    rows.WriteByte(0);
                    for (var x = 0; x < 16 * 3; x++) rows.WriteByte(0xC0);
                }
                var png = new MemoryStream();
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                Repair.WriteBig(ihdr, 0, 16);
                Repair.WriteBig(ihdr, 4, 16);
                ihdr[8] = 8;
                ihdr[9] = 2;
                Chunk(png, "IHDR", ihdr);
                Chunk(png, "IDAT", Repair.Zlib(rows.ToArray()));
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        /// <summary>Writes a PNG part (for the grey pictures put in place of lost ones).</summary>
        private static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            Repair.WriteBig(head, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            s.Write(head, 0, 8);
            s.Write(data, 0, data.Length);
            var both = head.Skip(4).Concat(data).ToArray();
            var crc = new byte[4];
            Repair.WriteBig(crc, 0, Crc32.Of(both, 0, both.Length));
            s.Write(crc, 0, 4);
        }
    }
}
