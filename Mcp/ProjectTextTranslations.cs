using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTranslationSelection
    {
        [JsonRequired] public string Sheet { get; set; } = "";
        [JsonRequired] public int Row { get; set; }
        [JsonRequired] public string Culture { get; set; } = "";
        [JsonRequired] public string ExpectedUnitFingerprint { get; set; } = "";
        [JsonRequired] public string ExpectedText { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTranslationEdit
    {
        [JsonRequired] public ProjectTranslationSelection Selection { get; set; } = null!;
        [JsonRequired] public string NewText { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTranslationEdits
    {
        [JsonRequired] public string ExpectedProjectPath { get; set; } = "";
        [JsonRequired] public ProjectTextCapture Capture { get; set; } = null!;
        public bool UpdateSourceLanguage { get; set; }
        [JsonRequired] public ProjectTranslationEdit[] Edits { get; set; } = Array.Empty<ProjectTranslationEdit>();
        public void Validate()
        {
            if (Capture == null || !Path.IsPathRooted(ExpectedProjectPath ?? "") || ExpectedProjectPath.Length > 4096 || ExpectedProjectPath.Any(char.IsControl))
                throw new ArgumentException("Supply the exact absolute attached-project path and explicit language capture.");
            Capture.Validate();
            if (Edits == null || Edits.Length < 1 || Edits.Length > 200) throw new ArgumentException("Supply 1..200 exact translation edits.");
        }
    }
    internal sealed class ProjectTranslationUnit
    {
        public string Sheet { get; set; } = "";
        public int Row { get; set; }
        public string Category { get; set; } = "";
        public string ViewPath { get; set; } = "";
        public string UnitFingerprint { get; set; } = "";
        public string SourceLanguage { get; set; } = "";
        public string TargetLanguage { get; set; } = "";
        public string SourceText { get; set; } = "";
        public string TargetText { get; set; } = "";
        public bool TargetEmpty => TargetText.Length == 0;
        public bool Editable { get; set; }
        public string? ReadOnlyReason { get; set; }
        internal ProjectTextCell Source = null!, Target = null!;
        public ProjectTranslationSelection Select(bool source = false) => new ProjectTranslationSelection {
            Sheet = Sheet, Row = Row, Culture = source ? SourceLanguage : TargetLanguage,
            ExpectedUnitFingerprint = UnitFingerprint, ExpectedText = source ? SourceText : TargetText };
    }
    internal sealed class ProjectTranslationCandidate
    {
        public byte[] Bytes = Array.Empty<byte>();
        public string BeforeFingerprint = "", AfterFingerprint = "";
    }
    internal sealed partial class ProjectTextWorkbook
    {
        public string Fingerprint => CellHash(Sheets, Cells);
        private static string CellHash(IEnumerable<ProjectTextSheet> sheets, IEnumerable<ProjectTextCell> cells)
        {
            using (var buffer = new MemoryStream()) {
                using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false, true), true)) {
                    writer.Write("tia-project-text-cells-v1");
                    foreach (var sheet in sheets.OrderBy(s => s.Name, StringComparer.Ordinal)) { writer.Write(sheet.Name); writer.Write(sheet.State); writer.Write(sheet.CellCount); }
                    foreach (var cell in cells.OrderBy(c => c.Sheet, StringComparer.Ordinal).ThenBy(c => c.Address, StringComparer.Ordinal)) {
                        writer.Write(cell.Sheet); writer.Write(cell.Address); writer.Write(cell.Type); writer.Write(cell.Text);
                        writer.Write(cell.Formula != null); if (cell.Formula != null) writer.Write(cell.Formula);
                    }
                }
                return SiemensMCPServer.TextListDocument.Hash(buffer.ToArray());
            }
        }
        public ProjectTranslationUnit[] TranslationUnits(ProjectTextCapture capture)
        {
            capture.Validate();
            using (var input = new MemoryStream(originalBytes, false))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) {
                var parts = zip.Entries.Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal)).ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
                if (!parts.ContainsKey("docProps/custom.xml")) throw new NotSupportedException("Unrecognized project-text workbook: native TIA_Version metadata is required. Raw cell inspection remains available.");
                var custom = Xml(parts, "docProps/custom.xml"); XNamespace cp = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
                var versions = custom.Root?.Elements(cp + "property").Where(e => (string?)e.Attribute("name") == "TIA_Version").ToArray();
                if (versions?.Length != 1 || versions[0].Value != "1.3") throw new NotSupportedException("Translation editing recognizes native project-text workbook schema 1.3 only; use raw inspection for other schemas.");
            }
            var result = new List<ProjectTranslationUnit>();
            bool hasFormula = Cells.Any(c => c.Formula != null);
            foreach (var sheet in Sheets) {
                var rows = Cells.Where(c => c.Sheet == sheet.Name).GroupBy(c => Coordinate(c.Address).Item2).OrderBy(r => r.Key).ToArray();
                var headers = rows.SingleOrDefault(r => r.Key == 1)?.ToDictionary(c => Coordinate(c.Address).Item1);
                if (headers == null || !new[] { "Category", "ViewPath", "Internal ID", "Substitutions", capture.SourceLanguage + "*", capture.TargetLanguage }
                    .Select((value, i) => headers.TryGetValue(i + 1, out var cell) && cell.Formula == null && cell.Text == value).All(v => v)
                    || headers.Where(h => h.Key > 6).Any(h => h.Value.Formula != null || !Regex.IsMatch(h.Value.Text, "\\Aref=\"[0-9]+\"\\z")))
                    throw new NotSupportedException("Unrecognized native project-text headers or language pair. Metadata columns are never inferred as translations.");
                foreach (var row in rows.Where(r => r.Key > 1)) {
                    var columns = row.ToDictionary(c => Coordinate(c.Address).Item1);
                    if (columns.Count == 0) continue;
                    if (!new[] { 1, 2, 3, 4, 5, 6 }.All(columns.ContainsKey) || columns[3].Text.Length == 0)
                        throw new InvalidDataException("Native translation row is incomplete or lacks an internal identity.");
                    var source = columns[5]; var target = columns[6];
                    string? reason = sheet.Name != "User Texts" ? "Only the native User Texts sheet is eligible for updates; system/unknown sheets are inspection-only."
                        : IgnoredExternalRelationships != 0 || hasFormula ? "Workbook formulas or external relationships are inspection-only."
                        : !source.PlainString || !target.PlainString || row.Any(c => c.Formula != null) ? "Native source and target cells must be existing plain strings."
                        : columns[4].Text.Length != 0 || columns.Any(c => c.Key > 6 && c.Value.Text.Length != 0) ? "Substitutions/reference fields require a specialized text adapter."
                        : source.Text.IndexOf('<') >= 0 || target.Text.IndexOf('<') >= 0 ? "Possible native rich/dynamic markup requires a specialized text adapter."
                        : source.Text.Length > 32767 || target.Text.Length > 32767 ? "Translation exceeds the 32767-character update limit." : null;
                    result.Add(new ProjectTranslationUnit { Sheet = sheet.Name, Row = row.Key, Category = columns[1].Text, ViewPath = columns[2].Text,
                        SourceLanguage = capture.SourceLanguage, TargetLanguage = capture.TargetLanguage, SourceText = source.Text, TargetText = target.Text,
                        UnitFingerprint = CellHash(Array.Empty<ProjectTextSheet>(), row), Editable = reason == null, ReadOnlyReason = reason, Source = source, Target = target });
                }
            }
            return result.ToArray();
        }
        public ProjectTranslationCandidate EditTranslations(ProjectTranslationEdits request)
        {
            request.Validate();
            // Construct a separate model: failed selection/serialization leaves this workbook unchanged.
            var model = new ProjectTextWorkbook(originalBytes); var units = model.TranslationUnits(request.Capture);
            var index = units.ToDictionary(u => u.Sheet + "\0" + u.Row.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal); var changes = new List<Tuple<ProjectTextCell, string>>();
            foreach (var edit in request.Edits) {
                var selection = edit?.Selection;
                if (selection == null || selection.Sheet == null || selection.Culture == null || selection.ExpectedText == null || selection.ExpectedUnitFingerprint == null
                    || edit!.NewText == null || edit.NewText.Length > 32767 || edit.NewText.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                    throw new ArgumentException("Every translation needs an exact bounded selection and literal new text of at most 32767 characters.");
                XmlConvert.VerifyXmlChars(edit.NewText);
                if (!index.TryGetValue(selection.Sheet + "\0" + selection.Row.ToString(CultureInfo.InvariantCulture), out var unit) || !unit.Editable)
                    throw new ArgumentException("Select an editable User Texts unit returned by read_translation_units.");
                bool source = selection.Culture == request.Capture.SourceLanguage;
                if ((!source && selection.Culture != request.Capture.TargetLanguage) || source && !request.UpdateSourceLanguage)
                    throw new ArgumentException("Source-language changes require updateSourceLanguage=true; other cultures require their own language-pair capture.");
                var cell = source ? unit.Source : unit.Target;
                if (!selected.Add(cell.Sheet + "\0" + cell.Address)) throw new ArgumentException("Duplicate translation cell selection.");
                if (unit.UnitFingerprint != selection.ExpectedUnitFingerprint || cell.Text != selection.ExpectedText)
                    throw new InvalidOperationException("Translation identity, source/context or expected text changed; read translation units again.");
                if (edit.NewText != cell.Text) changes.Add(Tuple.Create(cell, edit.NewText));
            }
            if (changes.Count == 0) throw new ArgumentException("Translation preview contains no changes.");
            var changedParts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes) {
                var cell = change.Item1; XNamespace ns = cell.Node.Name.Namespace;
                string encoded = Regex.Replace(change.Item2, "_x[0-9a-fA-F]{4}_", m => "_x005F_" + m.Value.Substring(1));
                var text = new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), encoded);
                if (cell.Type == "s") {
                    if (model.sharedDocument?.Root == null) throw new InvalidDataException("Missing shared-string table.");
                    int ordinal = model.sharedDocument.Root.Elements(ns + "si").Count();
                    model.sharedDocument.Root.Add(new XElement(ns + "si", text));
                    model.sharedDocument.Root.SetAttributeValue("uniqueCount", ordinal + 1);
                    cell.Node.Element(ns + "v")!.Value = ordinal.ToString(CultureInfo.InvariantCulture); changedParts.Add(model.sharedPart);
                } else { cell.Node.Element(ns + "is")!.ReplaceWith(new XElement(ns + "is", text)); }
                cell.Text = change.Item2; changedParts.Add(cell.Part);
            }
            using (var output = new MemoryStream()) {
                using (var source = new MemoryStream(originalBytes, false))
                using (var input = new ZipArchive(source, ZipArchiveMode.Read))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) {
                    foreach (var entry in input.Entries) {
                        var created = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal); created.LastWriteTime = entry.LastWriteTime; created.ExternalAttributes = entry.ExternalAttributes;
                        using (var destination = created.Open()) {
                            if (changedParts.Contains(entry.FullName)) {
                                XDocument document = entry.FullName == model.sharedPart ? model.sharedDocument! : model.worksheets[entry.FullName];
                                using (var writer = XmlWriter.Create(destination, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false, NewLineHandling = NewLineHandling.Entitize })) document.Save(writer);
                            } else using (var original = entry.Open()) original.CopyTo(destination);
                        }
                    }
                }
                byte[] candidate = output.ToArray();
                if (new ProjectTextWorkbook(candidate).Fingerprint != model.Fingerprint) throw new InvalidOperationException("Candidate workbook round-trip differs from selected literal text edits.");
                return new ProjectTranslationCandidate { Bytes = candidate, BeforeFingerprint = Fingerprint, AfterFingerprint = model.Fingerprint };
            }
        }
    }
}
