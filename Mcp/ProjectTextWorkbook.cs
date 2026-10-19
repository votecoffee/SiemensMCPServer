using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    internal sealed class ProjectTextCell
    {
        public string Sheet { get; set; } = "";
        public string Address { get; set; } = "";
        public string Type { get; set; } = "";
        public string Text { get; set; } = "";
        public string? Formula { get; set; }
        internal XElement Node = null!;
        internal string Part = "";
        internal bool PlainString;
    }
    internal sealed class ProjectTextSheet
    {
        public string Name { get; set; } = "";
        public string State { get; set; } = "";
        public int CellCount { get; set; }
    }
    // Bounded OOXML view. The separate translation adapter recognizes only native schemas.
    internal sealed partial class ProjectTextWorkbook
    {
        public const int MaxCells = 200000, MaxTextCharacters = 16 * 1024 * 1024, MaxCellCharacters = 65536;
        private const int MaxPartBytes = 16 * 1024 * 1024;
        private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", StrictMain = "http://purl.oclc.org/ooxml/spreadsheetml/main";
        private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/", StrictRel = "http://purl.oclc.org/ooxml/officeDocument/relationships/";
        private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";
        public List<ProjectTextCell> Cells { get; } = new List<ProjectTextCell>();
        public List<ProjectTextSheet> Sheets { get; } = new List<ProjectTextSheet>();
        public int IgnoredExternalRelationships { get; private set; }
        private int textCharacters;
        private readonly byte[] originalBytes;
        private readonly Dictionary<string, XDocument> worksheets = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        private XDocument? sharedDocument;
        private string sharedPart = "";
        private readonly List<bool> plainStrings = new List<bool>();
        private sealed class Relationship { public string Type = "", Target = ""; public bool External; }

        public ProjectTextWorkbook(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > ProjectTextFiles.MaxBytes) throw new InvalidDataException("Workbook exceeds the 8 MiB file limit.");
            originalBytes = bytes.ToArray();
            using (var stream = new MemoryStream(bytes, false))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read)) {
                if (zip.Entries.Count > 512) throw new InvalidDataException("Workbook exceeds 512 ZIP entries.");
                var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var entry in zip.Entries) {
                    string path = entry.FullName;
                    if (path.EndsWith("/", StringComparison.Ordinal)) { ValidatePart(path.TrimEnd('/')); continue; }
                    ValidatePart(path);
                    if (parts.ContainsKey(path)) throw new InvalidDataException("Duplicate or case-colliding workbook ZIP members.");
                    total += entry.Length;
                    if (entry.Length > MaxPartBytes || total > 64L * 1024 * 1024) throw new InvalidDataException("Workbook exceeds its uncompressed size limits.");
                    parts.Add(path, entry);
                }
                // Resolve the workbook from the package relationship, rather than guessing a worksheet path.
                var packageRelations = Relationships(Xml(parts, "_rels/.rels"));
                var office = packageRelations.Values.Where(r => IsRelationship(r.Type, "officeDocument")).ToArray();
                if (office.Length != 1 || office[0].External) throw new InvalidDataException("Expected one internal workbook relationship.");
                string workbookPart = Target("", office[0].Target);
                var workbook = Xml(parts, workbookPart); XNamespace ns = SpreadsheetNamespace(workbook, "workbook");
                string directory = workbookPart.Substring(0, workbookPart.LastIndexOf('/') + 1);
                string leaf = workbookPart.Substring(directory.Length);
                var relationships = Relationships(Xml(parts, directory + "_rels/" + leaf + ".rels"));
                var sharedRelations = relationships.Values.Where(r => IsRelationship(r.Type, "sharedStrings")).ToArray();
                if (sharedRelations.Length > 1 || sharedRelations.Any(r => r.External)) throw new InvalidDataException("Ambiguous or external shared-string table.");
                var strings = new List<string>();
                if (sharedRelations.Length == 1) {
                    sharedPart = Target(directory, sharedRelations[0].Target);
                    var shared = sharedDocument = Xml(parts, sharedPart);
                    XNamespace sns = SpreadsheetNamespace(shared, "sst");
                    foreach (var item in shared.Root!.Elements(sns + "si")) {
                        if (strings.Count >= 100000) throw new InvalidDataException("Workbook exceeds 100000 shared strings.");
                        string value = Text(item, sns); Budget(value); strings.Add(value);
                        plainStrings.Add(item.Elements().All(e => e.Name == sns + "t") && item.Elements(sns + "t").Count() == 1);
                    }
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Bound the stored string table and expanded cells separately. A shared
                // string is intentionally counted again when referenced by a cell.
                textCharacters = 0;
                var usedParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sheet in workbook.Root!.Element(ns + "sheets")?.Elements(ns + "sheet") ?? Enumerable.Empty<XElement>()) {
                    if (Sheets.Count >= 128) throw new InvalidDataException("Workbook exceeds 128 sheets.");
                    string name = (string?)sheet.Attribute("name") ?? "";
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || !names.Add(name)) throw new InvalidDataException("Invalid or duplicate workbook sheet name.");
                    string id = (string?)sheet.Attribute(XName.Get("id", Rel.TrimEnd('/'))) ?? (string?)sheet.Attribute(XName.Get("id", StrictRel.TrimEnd('/'))) ?? "";
                    if (!relationships.TryGetValue(id, out var relationship) || !IsRelationship(relationship.Type, "worksheet") || relationship.External)
                        throw new InvalidDataException("Sheet does not resolve to an internal worksheet.");
                    string part = Target(directory, relationship.Target);
                    if (!usedParts.Add(part)) throw new InvalidDataException("Multiple sheets reference the same worksheet.");
                    var info = new ProjectTextSheet { Name = name, State = (string?)sheet.Attribute("state") ?? "visible" };
                    var worksheet = Xml(parts, part); worksheets.Add(part, worksheet);
                    ReadSheet(worksheet, strings, info, part); Sheets.Add(info);
                }
                if (Sheets.Count == 0) throw new InvalidDataException("Workbook has no supported worksheets.");
            }
        }
        private Dictionary<string, Relationship> Relationships(XDocument document)
        {
            if (document.Root?.Name != Package + "Relationships") throw new InvalidDataException("Invalid package relationships.");
            var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            foreach (var item in document.Root.Elements(Package + "Relationship")) {
                if (result.Count >= 512) throw new InvalidDataException("Workbook part exceeds 512 relationships.");
                string id = (string?)item.Attribute("Id") ?? "", type = (string?)item.Attribute("Type") ?? "", target = (string?)item.Attribute("Target") ?? "";
                string mode = (string?)item.Attribute("TargetMode") ?? "Internal";
                if (id.Length == 0 || type.Length == 0 || target.Length == 0 || result.ContainsKey(id) || mode != "Internal" && mode != "External")
                    throw new InvalidDataException("Invalid or duplicate package relationship.");
                bool external = mode == "External";
                if (external) IgnoredExternalRelationships++;
                result.Add(id, new Relationship { Type = type, Target = target, External = external });
            }
            return result;
        }
        private static bool IsRelationship(string value, string suffix) => value == Rel + suffix || value == StrictRel + suffix;
        private static XNamespace SpreadsheetNamespace(XDocument document, string root)
        {
            string ns = document.Root?.Name.NamespaceName ?? "";
            if ((ns != Main && ns != StrictMain) || document.Root?.Name.LocalName != root) throw new InvalidDataException("Unsupported SpreadsheetML part.");
            return ns;
        }
        private void ReadSheet(XDocument document, IList<string> strings, ProjectTextSheet sheet, string part)
        {
            XNamespace ns = SpreadsheetNamespace(document, "worksheet"); int previousRow = 0;
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            var data = document.Root!.Element(ns + "sheetData") ?? throw new InvalidDataException("Worksheet has no sheetData.");
            int rowCount = 0;
            foreach (var row in data.Elements(ns + "row")) {
                if (++rowCount > MaxCells) throw new InvalidDataException("Worksheet exceeds 200000 explicit rows.");
                string? rowNumber = (string?)row.Attribute("r");
                string? firstReference = (string?)row.Element(ns + "c")?.Attribute("r");
                int number = rowNumber != null ? Number(rowNumber, 1048576) : firstReference != null ? Coordinate(firstReference).Item2 : previousRow + 1;
                if (number < 1 || number > 1048576) throw new InvalidDataException("Invalid worksheet row index.");
                previousRow = number; int previousColumn = 0;
                foreach (var cell in row.Elements(ns + "c")) {
                    if (Cells.Count >= MaxCells) throw new InvalidDataException("Workbook exceeds 200000 explicit cells.");
                    string? reference = (string?)cell.Attribute("r");
                    var coordinate = reference == null ? Tuple.Create(previousColumn + 1, number) : Coordinate(reference);
                    if (coordinate.Item1 > 16384 || coordinate.Item2 != number) throw new InvalidDataException("Cell reference does not match its row or worksheet bounds.");
                    string address = Column(coordinate.Item1) + number.ToString(CultureInfo.InvariantCulture); previousColumn = coordinate.Item1;
                    if (!addresses.Add(address)) throw new InvalidDataException("Duplicate worksheet cell address.");
                    string type = (string?)cell.Attribute("t") ?? "n", value = cell.Element(ns + "v")?.Value ?? "";
                    if (cell.Elements(ns + "v").Count() > 1 || cell.Elements(ns + "f").Count() > 1 || cell.Elements(ns + "is").Count() > 1)
                        throw new InvalidDataException("Ambiguous worksheet cell contents.");
                    string? formula = cell.Element(ns + "f")?.Value;
                    bool plain = false;
                    if (type == "s") {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= strings.Count) throw new InvalidDataException("Invalid shared-string reference.");
                        value = strings[index];
                        plain = plainStrings[index];
                    } else if (type == "inlineStr") {
                        var inline = cell.Element(ns + "is") ?? throw new InvalidDataException("Missing inline string.");
                        value = Text(inline, ns); plain = inline.Elements().All(e => e.Name == ns + "t") && inline.Elements(ns + "t").Count() == 1;
                    }
                    else if (type == "str") value = DecodeEscapes(value);
                    else if (type != "n" && type != "b" && type != "d" && type != "e") throw new InvalidDataException("Unsupported workbook cell type.");
                    if (type == "b" && value != "0" && value != "1" && value != "") throw new InvalidDataException("Invalid boolean cell value.");
                    Budget(value); if (formula != null) Budget(formula);
                    Cells.Add(new ProjectTextCell { Sheet = sheet.Name, Address = address, Type = type, Text = value, Formula = formula, Node = cell, Part = part, PlainString = plain }); sheet.CellCount++;
                }
            }
        }
        private string Text(XElement item, XNamespace ns)
        {
            var text = new StringBuilder();
            foreach (var element in item.Elements()) {
                if (element.Name == ns + "t") text.Append(element.Value);
                else if (element.Name == ns + "r") foreach (var value in element.Elements(ns + "t")) text.Append(value.Value);
                if (text.Length > MaxCellCharacters * 7) throw new InvalidDataException("Workbook encoded text exceeds its per-value limit.");
            }
            return DecodeEscapes(text.ToString());
        }
        private void Budget(string value)
        {
            if (value.Length > MaxCellCharacters || (textCharacters += value.Length) > MaxTextCharacters) throw new InvalidDataException("Workbook exceeds cell or total decoded-text limits.");
            for (int i = 0; i < value.Length; i++) {
                if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new InvalidDataException("Invalid surrogate in workbook text."); }
                else if (char.IsLowSurrogate(value[i])) throw new InvalidDataException("Invalid surrogate in workbook text.");
            }
        }
        private static string DecodeEscapes(string value) => Regex.Replace(value, "_x([0-9A-Fa-f]{4})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
        private static Tuple<int, int> Coordinate(string address)
        {
            var match = Regex.Match(address, "\\A([A-Z]{1,3})([1-9][0-9]{0,6})\\z");
            if (!match.Success) throw new InvalidDataException("Invalid worksheet cell address.");
            int column = 0; foreach (char c in match.Groups[1].Value) column = column * 26 + c - 'A' + 1;
            if (column > 16384) throw new InvalidDataException("Cell column exceeds XFD.");
            return Tuple.Create(column, Number(match.Groups[2].Value, 1048576));
        }
        private static int Number(string value, int max)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || number > max) throw new InvalidDataException("Invalid worksheet index.");
            return number;
        }
        private static string Column(int number)
        {
            string result = ""; while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; } return result;
        }
        private static void ValidatePart(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.StartsWith("/", StringComparison.Ordinal) || path.Any(c => char.IsControl(c) || c == '\\' || c == ':')
                || path.Split('/').Any(p => p == "" || p == "." || p == "..")) throw new InvalidDataException("Unsafe workbook ZIP member.");
        }
        private static string Target(string directory, string target)
        {
            target = Uri.UnescapeDataString(target);
            if (target.IndexOfAny(new[] { '\\', ':', '?', '#' }) >= 0 || target.Any(char.IsControl)) throw new InvalidDataException("Unsupported internal workbook relationship target.");
            var parts = new List<string>();
            foreach (string part in (target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : directory + target).Split('/')) {
                if (part == ".") continue;
                if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Workbook relationship escapes its package."); parts.RemoveAt(parts.Count - 1); }
                else parts.Add(part);
            }
            string path = string.Join("/", parts); ValidatePart(path); return path;
        }
        private static XDocument Xml(IDictionary<string, ZipArchiveEntry> parts, string name)
        {
            if (!parts.TryGetValue(name, out var part)) throw new InvalidDataException("Missing workbook part: " + name);
            using (var source = part.Open())
            using (var buffer = new MemoryStream()) {
                byte[] chunk = new byte[8192]; int read;
                while ((read = source.Read(chunk, 0, chunk.Length)) != 0) {
                    if (buffer.Length + read > MaxPartBytes) throw new InvalidDataException("Workbook XML part exceeds 16 MiB.");
                    buffer.Write(chunk, 0, read);
                }
                if (buffer.Length != part.Length) throw new InvalidDataException("Workbook part length mismatch.");
                buffer.Position = 0;
                using (var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPartBytes }))
                    return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
        }
    }
}
