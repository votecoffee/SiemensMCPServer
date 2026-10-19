using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTranslationReadOptions
    {
        public string Sheet { get; set; } = "";
        public string Query { get; set; } = "";
        public string Category { get; set; } = "";
        public bool MissingOnly { get; set; }
        public int Offset { get; set; }
        public int MaxUnits { get; set; } = 50;
        public int MaxCharacters { get; set; } = 4096;
    }
    internal static class ProjectTranslationOperations
    {
        public static object Schema(bool edit)
        {
            object capture = new { type = "object", additionalProperties = false, required = new[] { "sourceLanguage", "targetLanguage" }, properties = new {
                sourceLanguage = new { type = "string" }, targetLanguage = new { type = "string" } } };
            if (!edit) return new { type = "object", additionalProperties = false, required = new[] { "expectedProjectPath", "capture", "options" }, properties = new {
                expectedProjectPath = new { type = "string" }, capture, options = new { type = "object", additionalProperties = false, properties = new {
                    sheet = new { type = "string" }, query = new { type = "string", maxLength = 256 }, category = new { type = "string", maxLength = 256 }, missingOnly = new { type = "boolean" },
                    offset = new { type = "integer", minimum = 0, maximum = 200000 }, maxUnits = new { type = "integer", minimum = 1, maximum = 100 }, maxCharacters = new { type = "integer", minimum = 1, maximum = 32767 } } } } };
            return new { type = "object", additionalProperties = false, required = new[] { "expectedProjectPath", "capture", "edits" }, properties = new {
                expectedProjectPath = new { type = "string" }, capture, updateSourceLanguage = new { type = "boolean", description = "Explicitly permit selected source-language updates; defaults to false." },
                edits = new { type = "array", minItems = 1, maxItems = 200, items = new { type = "object", additionalProperties = false, required = new[] { "selection", "newText" }, properties = new {
                    newText = new { type = "string", maxLength = 32767 }, selection = new { type = "object", additionalProperties = false,
                        required = new[] { "sheet", "row", "culture", "expectedUnitFingerprint", "expectedText" }, properties = new {
                            sheet = new { type = "string" }, row = new { type = "integer", minimum = 2 }, culture = new { type = "string" }, expectedUnitFingerprint = new { type = "string" }, expectedText = new { type = "string" } } } } } } } };
        }
        public static JsonElement Execute(string operation, JsonElement input)
        {
            string? phase = input.GetProperty("phase").GetString();
            if (phase != "validate" && phase != "snapshot") throw new ArgumentException("Unknown project translation phase.");
            if (operation == "edit_project_texts") {
                var request = ExtensionWire.Parse<ProjectTranslationEdits>(input.GetProperty("arguments")); request.Validate();
                if (phase == "validate") return ExtensionWire.Json(new { validated = true });
                var before = ExtensionWire.Parse<ProjectTextSnapshot>(input.GetProperty("snapshot"));
                if (before.SourceLanguage != request.Capture.SourceLanguage || before.TargetLanguage != request.Capture.TargetLanguage) throw new InvalidOperationException("Language capture changed.");
                var candidate = new ProjectTextWorkbook(ProjectTextFiles.Decode(before)).EditTranslations(request);
                return ExtensionWire.Json(new { candidate.BeforeFingerprint, candidate.AfterFingerprint, candidate = ProjectTextFiles.FromBytes(candidate.Bytes, request.Capture) });
            }
            if (operation != "read_translation_units") throw new ArgumentException("Unknown project translation operation.");
            var read = ExtensionWire.Parse<ProjectTextInput>(input.GetProperty("arguments")); read.Validate("projectTexts");
            var options = ExtensionWire.Parse<ProjectTranslationReadOptions>(read.Options);
            if (options.Offset < 0 || options.Offset > ProjectTextWorkbook.MaxCells || options.MaxUnits < 1 || options.MaxUnits > 100 || options.MaxCharacters < 1 || options.MaxCharacters > 32767
                || new[] { options.Sheet, options.Query, options.Category }.Any(v => v == null || v.Length > 256 || v.Any(char.IsControl))) throw new ArgumentException("Use bounded translation filters and paging.");
            if (phase == "validate") return ExtensionWire.Json(new { validated = true });
            var snapshot = ExtensionWire.Parse<ProjectTextSnapshot>(input.GetProperty("snapshot")); var capture = ExtensionWire.Parse<ProjectTextCapture>(read.Capture);
            if (snapshot.SourceLanguage != capture.SourceLanguage || snapshot.TargetLanguage != capture.TargetLanguage) throw new InvalidOperationException("Language capture changed.");
            var workbook = new ProjectTextWorkbook(ProjectTextFiles.Decode(snapshot)); var all = workbook.TranslationUnits(capture);
            if (options.Sheet.Length != 0 && !workbook.Sheets.Any(s => s.Name == options.Sheet)) throw new ArgumentException("Select an exact returned worksheet name.");
            var units = all.Where(u => (options.Sheet.Length == 0 || u.Sheet == options.Sheet) && (!options.MissingOnly || u.TargetEmpty)
                && (options.Category.Length == 0 || u.Category == options.Category)
                && (options.Query.Length == 0 || new[] { u.SourceText, u.TargetText, u.ViewPath, u.Category }.Any(v => v.IndexOf(options.Query, StringComparison.OrdinalIgnoreCase) >= 0))).ToArray();
            var rows = new List<object>(); int size = 0;
            foreach (var unit in units.Skip(options.Offset).Take(options.MaxUnits)) {
                bool clipped = unit.SourceText.Length > options.MaxCharacters || unit.TargetText.Length > options.MaxCharacters;
                var row = new { unit.Sheet, unit.Row, unit.Category, unit.ViewPath, unit.UnitFingerprint, unit.SourceLanguage, unit.TargetLanguage,
                    sourceText = Prefix(unit.SourceText, options.MaxCharacters), targetText = Prefix(unit.TargetText, options.MaxCharacters), unit.TargetEmpty, textClipped = clipped,
                    canPreview = unit.Editable && !clipped, readOnlyReason = clipped ? "Increase maxCharacters to inspect complete source and target text." : unit.ReadOnlyReason,
                    targetSelection = unit.Editable && !clipped ? unit.Select() : null,
                    sourceSelection = unit.Editable && !clipped ? unit.Select(true) : null };
                int cost = JsonSerializer.Serialize(row, JsonDefaults.Options).Length;
                if (size + cost > 258048) {
                    if (rows.Count == 0) throw new ArgumentException("One translation unit exceeds the response budget; reduce maxCharacters or use the retained workbook.");
                    break;
                }
                rows.Add(row); size += cost;
            }
            return ExtensionWire.Json(new { workbookFingerprint = workbook.Fingerprint, schema = "TIA project texts 1.3", capture.SourceLanguage, capture.TargetLanguage,
                units = rows, totalUnits = all.Length, matchedUnits = units.Length, editableUnits = all.Count(u => u.Editable), missingTranslations = all.Count(u => u.TargetEmpty),
                categories = all.GroupBy(u => u.Category).Take(128).Select(g => new { category = Prefix(g.Key, 256), categoryClipped = g.Key.Length > 256, count = g.Count() }).ToArray(), categoriesTruncated = all.Select(u => u.Category).Distinct().Take(129).Count() > 128,
                offset = options.Offset, nextOffset = options.Offset + rows.Count, hasMore = options.Offset + rows.Count < units.Length,
                scope = "Native exported project texts: categories and object paths retain comments, descriptions, captions and text values. Existing User Texts language slots only; system texts, substitutions and complex markup are inspection-only. No automatic translation or language activation." });
        }
        private static string Prefix(string value, int limit)
        {
            int length = Math.Min(value.Length, limit); if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }
    }
}
