using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTextReadOptions
    {
        public string Sheet { get; set; } = "";
        public int Offset { get; set; }
        public int MaxCells { get; set; } = 100;
        public int MaxCharacters { get; set; } = 1024;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTextSearchOptions
    {
        public string Sheet { get; set; } = "";
        [JsonRequired] public string Query { get; set; } = "";
        [JsonRequired] public bool CaseSensitive { get; set; }
        public int MaxMatches { get; set; } = 50;
    }
    internal static class ProjectTextOperations
    {
        public static object Schema(string operation)
        {
            object capture = operation == "list_project_languages" ? (object)new { type = "object", additionalProperties = false }
                : new { type = "object", additionalProperties = false, required = new[] { "sourceLanguage", "targetLanguage" }, properties = new {
                    sourceLanguage = new { type = "string", description = "Exact active culture name from list_project_languages." }, targetLanguage = new { type = "string", description = "A different active culture name." } } };
            object options = operation == "list_project_languages" ? (object)new { type = "object", additionalProperties = false }
                : operation == "read_project_texts" ? (object)new { type = "object", additionalProperties = false, properties = new {
                    sheet = new { type = "string", description = "Exact worksheet name, or empty for all sheets." }, offset = new { type = "integer", minimum = 0, maximum = ProjectTextWorkbook.MaxCells },
                    maxCells = new { type = "integer", minimum = 1, maximum = 200 }, maxCharacters = new { type = "integer", minimum = 1, maximum = 4096 } } }
                : new { type = "object", additionalProperties = false, required = new[] { "query", "caseSensitive" }, properties = new {
                    sheet = new { type = "string" }, query = new { type = "string", minLength = 1, maxLength = 256 }, caseSensitive = new { type = "boolean" }, maxMatches = new { type = "integer", minimum = 1, maximum = 200 } } };
            return new { type = "object", additionalProperties = false, required = new[] { "expectedProjectPath", "capture", "options" }, properties = new {
                expectedProjectPath = new { type = "string", description = "Exact absolute path of the attached project." }, capture, options } };
        }
        public static JsonElement Execute(string operation, JsonElement input)
        {
            bool languages = operation == "list_project_languages";
            if (!languages && operation != "read_project_texts" && operation != "find_in_project_texts") throw new ArgumentException("Unknown project-text operation.");
            var request = ExtensionWire.Parse<ProjectTextInput>(input.GetProperty("arguments")); request.Validate(languages ? "projectLanguages" : "projectTexts");
            ProjectTextReadOptions? read = null; ProjectTextSearchOptions? search = null;
            if (languages) {
                if (request.Options.EnumerateObject().Any()) throw new ArgumentException("Language options must be empty.");
            } else if (operation == "read_project_texts") {
                read = ExtensionWire.Parse<ProjectTextReadOptions>(request.Options); Sheet(read.Sheet);
                if (read.Offset < 0 || read.Offset > ProjectTextWorkbook.MaxCells || read.MaxCells < 1 || read.MaxCells > 200 || read.MaxCharacters < 1 || read.MaxCharacters > 4096)
                    throw new ArgumentException("Use a bounded cell offset, 1 to 200 cells and 1 to 4096 characters per returned value.");
            } else {
                search = ExtensionWire.Parse<ProjectTextSearchOptions>(request.Options); Sheet(search.Sheet);
                if (string.IsNullOrEmpty(search.Query) || search.Query.Length > 256 || search.Query.Any(char.IsControl) || search.MaxMatches < 1 || search.MaxMatches > 200)
                    throw new ArgumentException("Use a single-line literal query of 1 to 256 characters and 1 to 200 matches.");
            }
            string? phase = input.GetProperty("phase").GetString();
            if (phase == "validate") return ExtensionWire.Json(new { validated = true });
            if (phase != "snapshot") throw new ArgumentException("Unknown project-text inspection phase.");
            if (languages) return input.GetProperty("snapshot").Clone();
            var snapshot = ExtensionWire.Parse<ProjectTextSnapshot>(input.GetProperty("snapshot"));
            var capture = ExtensionWire.Parse<ProjectTextCapture>(request.Capture);
            if (snapshot.SourceLanguage != capture.SourceLanguage || snapshot.TargetLanguage != capture.TargetLanguage) throw new InvalidOperationException("Workbook language scope does not match the request.");
            var workbook = new ProjectTextWorkbook(ProjectTextFiles.Decode(snapshot));
            string sheet = read?.Sheet ?? search!.Sheet;
            if (sheet != "" && !workbook.Sheets.Any(s => s.Name == sheet)) throw new ArgumentException("Worksheet does not exist; use a returned exact sheet name.");
            var cells = workbook.Cells.Where(c => sheet == "" || c.Sheet == sheet).ToArray();
            if (read != null) {
                var rows = new List<object>(); int characters = 0; bool payloadLimited = false;
                foreach (var cell in cells.Skip(read.Offset).Take(read.MaxCells)) {
                    string text = Prefix(cell.Text, read.MaxCharacters); string? formula = cell.Formula == null ? null : Prefix(cell.Formula, read.MaxCharacters);
                    int size = cell.Sheet.Length + cell.Address.Length + cell.Type.Length + text.Length + (formula?.Length ?? 0);
                    if (characters + size > 262144) { payloadLimited = true; break; }
                    characters += size;
                    rows.Add(new { cell.Sheet, cell.Address, cell.Type, text, textTruncated = text.Length != cell.Text.Length,
                        formula, formulaTruncated = formula?.Length != cell.Formula?.Length, hasFormula = cell.Formula != null });
                }
                bool more = read.Offset + rows.Count < cells.Length;
                return ExtensionWire.Json(new { sheets = workbook.Sheets, cells = rows, totalCells = cells.Length, offset = read.Offset,
                    nextOffset = more ? (int?)(read.Offset + rows.Count) : null, hasMore = more, payloadLimited, workbook.IgnoredExternalRelationships,
                    valueSemantics = "Raw cell text/numeric values and formula caches. Styles are not rendered; formulas and external links are never evaluated." });
            }
            var matches = new List<object>(); int count = 0;
            foreach (var cell in cells) {
                int offset = 0;
                while (offset <= cell.Text.Length - search!.Query.Length) {
                    int index = cell.Text.IndexOf(search.Query, offset, search.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    count++;
                    if (matches.Count < search.MaxMatches) {
                        int start = Math.Max(0, index - 80);
                        if (start > 0 && char.IsLowSurrogate(cell.Text[start]) && char.IsHighSurrogate(cell.Text[start - 1])) start--;
                        string context = Prefix(cell.Text.Substring(start), 512);
                        matches.Add(new { cell.Sheet, cell.Address, character = index + 1, context, contextStartCharacter = start + 1,
                            contextTruncated = start != 0 || context.Length != cell.Text.Length, hasFormula = cell.Formula != null });
                    }
                    offset = index + search.Query.Length;
                }
            }
            return ExtensionWire.Json(new { sheets = workbook.Sheets, query = search!.Query, matches, totalMatches = count, truncated = count > matches.Count,
                workbook.IgnoredExternalRelationships, scope = "Literal non-overlapping matches in decoded cells/formula caches, including headers and metadata. One-based UTF-16 character positions; no formula-expression search or translation-column inference." });
        }
        private static void Sheet(string value) { if (value == null || value.Length > 128 || value.Any(char.IsControl)) throw new ArgumentException("Use a returned worksheet name, or empty for all worksheets."); }
        private static string Prefix(string value, int max)
        {
            int length = Math.Min(value.Length, max);
            if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }
    }
}
