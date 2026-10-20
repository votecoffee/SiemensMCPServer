using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class TextListBindingExpectation
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedName { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class TextListBindingEvidence
    {
        [JsonRequired] public string Xml { get; set; } = "";
        [JsonRequired] public string[] KnownTextLists { get; set; } = Array.Empty<string>();
        public TextListBindingExpectation[] ExpectedBindings { get; set; } = Array.Empty<TextListBindingExpectation>();
        public string? UnchangedScreenAfterXml { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class TextListWorkflowItem
    {
        [JsonRequired] public ClassicDeltaValidation Delta { get; set; } = null!;
        public JsonElement? ApplyReceipt { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class TextListWorkflowEvidence
    {
        [JsonRequired] public string ProjectPath { get; set; } = "";
        [JsonRequired] public int ProcessId { get; set; }
        [JsonRequired] public string HmiId { get; set; } = "";
        [JsonRequired] public TextListWorkflowItem[] Lists { get; set; } = Array.Empty<TextListWorkflowItem>();
        public JsonElement? CompileReceipt { get; set; }
        public JsonElement? BaselineCompileReceipt { get; set; }
        public JsonElement? SaveReceipt { get; set; }
    }
    internal static class ClassicTextListEvidence
    {
        internal static object Schema(bool workflow)
        {
            var text = new { type = "string", minLength = 1, maxLength = 16777216 };
            if (workflow) return new { type = "object", additionalProperties = false, required = new[] { "projectPath", "processId", "hmiId", "lists" }, properties = new {
                projectPath = text, processId = new { type = "integer", minimum = 1 }, hmiId = text,
                lists = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", additionalProperties = false, required = new[] { "delta" }, properties = new {
                    delta = ClassicTextListValidation.Schema("validate_text_list_delta"), applyReceipt = new { type = "object" } } } },
                compileReceipt = new { type = "object" }, baselineCompileReceipt = new { type = "object" }, saveReceipt = new { type = "object" } } };
            return new { type = "object", additionalProperties = false, required = new[] { "xml", "knownTextLists" }, properties = new {
                xml = text, knownTextLists = new { type = "array", maxItems = 10000, items = text }, unchangedScreenAfterXml = text,
                expectedBindings = new { type = "array", maxItems = 5000, items = new { type = "object", additionalProperties = false, required = new[] { "xPath", "expectedName" }, properties = new { xPath = text, expectedName = text } } } } };
        }
        internal static object Bindings(TextListBindingEvidence input)
        {
            if (input.Xml == null || input.Xml.Length > 16 * 1024 * 1024 || input.KnownTextLists == null || input.KnownTextLists.Length > 10000
                || input.KnownTextLists.Any(string.IsNullOrWhiteSpace) || input.KnownTextLists.Distinct(StringComparer.Ordinal).Count() != input.KnownTextLists.Length
                || input.ExpectedBindings == null || input.ExpectedBindings.Length > 5000 || input.ExpectedBindings.Any(e => e == null || string.IsNullOrWhiteSpace(e.XPath) || e.ExpectedName == null)
                || input.ExpectedBindings.Select(e => e.XPath).Distinct(StringComparer.Ordinal).Count() != input.ExpectedBindings.Length) throw new ArgumentException("Supply bounded screen XML, exact known names and unique expected binding paths.");
            var screen = new ClassicXmlDocument(input.Xml); var rows = new List<JsonElement>();
            int offset = 0;
            do
            {
                var page = JsonSerializer.SerializeToElement(screen.ReferenceCatalog(new ClassicReferenceInspection { Xml = input.Xml, LinkType = "TextList", Offset = offset, MaxItems = 200 }), JsonDefaults.Options);
                rows.AddRange(page.GetProperty("references").EnumerateArray().Select(r => r.Clone()));
                int next = page.GetProperty("nextOffset").GetInt32();
                if (!page.GetProperty("hasMore").GetBoolean()) break;
                if (next <= offset || next > 5000) throw new ArgumentException("Binding report could not cover all exported TextList links.");
                offset = next;
            } while (true);
            var report = new HmiValidationReport { Profile = "classic-text-list-screen-bindings", ObjectName = screen.Name, SourceSha256 = TextListDocument.Hash(input.Xml), SourceEncoding = "decoded-string" };
            foreach (var row in rows)
            {
                string path = row.GetProperty("xPath").GetString()!; string? name = row.GetProperty("name").GetString();
                string kind = row.GetProperty("kind").GetString()!;
                report.Add("bindings.exported-target", kind != "openLink" || name == null ? "unsupported" : input.KnownTextLists.Contains(name, StringComparer.Ordinal) ? "passed" : "failed",
                    "Exact LinkList/TextList target '" + name + "' must occur in the caller's explicit native list inventory; native-ID links need native lookup.", path);
            }
            foreach (var expected in input.ExpectedBindings)
            {
                var matches = rows.Where(r => r.GetProperty("xPath").GetString() == expected.XPath).ToArray();
                report.Add("bindings.expected-target", matches.Length == 1 && matches[0].GetProperty("name").GetString() == expected.ExpectedName ? "passed" : "failed", "Expected exact target '" + expected.ExpectedName + "'.", expected.XPath);
            }
            string before = screen.PreservationFingerprint(); string? after = null;
            if (input.UnchangedScreenAfterXml != null)
            {
                if (input.UnchangedScreenAfterXml.Length > 16 * 1024 * 1024) throw new ArgumentException("Post-operation screen exceeds the XML limit.");
                after = new ClassicXmlDocument(input.UnchangedScreenAfterXml).PreservationFingerprint();
                report.Add("bindings.screen-preserved", before == after ? "passed" : "failed", "Complete supplied screen comparison excludes volatile native IDs and export headers, retaining bindings, controls, scripts, cultures and text.");
            }
            else report.Add("bindings.screen-preserved", "not-run", "No post-operation screen supplied.");
            return new { validation = report, references = rows, beforeFingerprint = before, afterFingerprint = after,
                scope = "Supplied screen's exported direct LinkList/TextList bindings only. Names are exact; no canonical/_1 inference, script/computed-name evaluation or runtime target verification.", nativeOperationPerformed = false };
        }
        internal static object Workflow(TextListWorkflowEvidence input)
        {
            if (string.IsNullOrWhiteSpace(input.ProjectPath) || input.ProcessId <= 0 || string.IsNullOrWhiteSpace(input.HmiId) || input.Lists == null || input.Lists.Length < 1 || input.Lists.Length > 100
                || input.Lists.Any(i => i?.Delta == null)) throw new ArgumentException("Supply exact project/PID/HMI identity and 1..100 reviewed list deltas.");
            var report = new HmiValidationReport { Profile = "classic-text-list-workflow-receipt" };
            var lists = new List<object>(); var names = new HashSet<string>(StringComparer.Ordinal); bool allApplied = true;
            foreach (var item in input.Lists)
            {
                var delta = ClassicTextListValidation.Delta(item.Delta); string name = delta.GetProperty("validation").GetProperty("objectName").GetString() ?? "";
                bool passed = delta.GetProperty("validation").GetProperty("canPublish").GetBoolean() && names.Add(name);
                report.Add("workflow.reviewed-delta", passed ? "passed" : "failed", "Complete exact-delta check and unique list identity.", name);
                bool matched = false;
                if (item.ApplyReceipt.HasValue)
                {
                    var apply = item.ApplyReceipt.Value;
                    matched = Identity(apply, input, nested: true) && Str(apply, "hmiId") == input.HmiId && Str(apply, "listName") == name
                        && Str(apply, "state") == "applied" && Bool(apply, "nativeImportReturned") && Str(apply, "nativeReadback") == "matched"
                        && Str(apply, "beforeFingerprint") == Str(delta, "beforeFingerprint") && Str(apply, "candidateSha256") == Str(delta, "candidateSha256")
                        && Str(apply, "verifiedFingerprint") == Str(delta, "candidateFingerprint") && item.Delta.ReadbackXml != null
                        && Str(delta, "readbackFingerprint") == Str(delta, "candidateFingerprint") && !string.IsNullOrEmpty(Str(apply, "readbackSnapshotSha256"))
                        && Str(apply, "readbackSnapshotSha256") == TextListDocument.Hash(NativeXmlValidation.SnapshotBytes(item.Delta.ReadbackXml));
                    report.Add("workflow.native-apply-evidence", matched ? "passed" : "failed", "Supplied apply receipt must match exact project/PID/HMI/list, reviewed source/candidate, successful import/readback and supplied readback artifact bytes. Uncertain/failed receipts do not establish completion.", name);
                }
                else report.Add("workflow.native-apply-evidence", "not-run", "No native apply receipt supplied.", name);
                allApplied &= matched; lists.Add(new { name, delta, applyReceiptSha256 = item.ApplyReceipt.HasValue ? TextListDocument.Hash(item.ApplyReceipt.Value.GetRawText()) : null, suppliedNativeEvidenceMatched = matched });
            }
            bool compiled = false, saved = false; object[] diagnostics = Array.Empty<object>();
            if (input.CompileReceipt.HasValue)
            {
                var compile = input.CompileReceipt.Value;
                compiled = Identity(compile, input) && Str(compile, "targetId") == input.HmiId && Integer(compile, "errors") == 0
                    && Integer(compile, "warnings") >= 0 && new[] { "Success", "Warning" }.Contains(Str(compile, "state"), StringComparer.Ordinal);
                report.Add("workflow.compile-evidence", compiled ? "passed" : "failed", "Supplied compile receipt must identify this project/PID/HMI and report zero errors. Warnings are retained.");
                var messages = Messages(compile); bool baselineComparable = input.BaselineCompileReceipt.HasValue && Identity(input.BaselineCompileReceipt.Value, input)
                    && Str(input.BaselineCompileReceipt.Value, "targetId") == input.HmiId && !Bool(input.BaselineCompileReceipt.Value, "messagesTruncated") && !Bool(compile, "messagesTruncated");
                var baseline = baselineComparable ? Messages(input.BaselineCompileReceipt!.Value).GroupBy(m => m.GetRawText()).ToDictionary(g => g.Key, g => g.Count()) : new Dictionary<string, int>();
                diagnostics = messages.Select(m => {
                    string key = m.GetRawText(), classification = "unclassified-no-comparable-baseline";
                    if (baselineComparable) { bool inherited = baseline.TryGetValue(key, out int count) && count > 0; classification = inherited ? "inherited" : "new"; if (inherited) baseline[key] = count - 1; }
                    return (object)new { message = m, classification };
                }).ToArray();
                report.Add("workflow.compile-diagnostic-coverage", Bool(compile, "messagesTruncated") ? "unsupported" : "passed", "Truncated diagnostic reports cannot establish a complete baseline comparison.");
            }
            else report.Add("workflow.compile-evidence", "not-run", "No compile receipt supplied; no baseline warning assumption is made.");
            if (input.SaveReceipt.HasValue)
            {
                saved = Identity(input.SaveReceipt.Value, input) && Bool(input.SaveReceipt.Value, "projectSaved");
                report.Add("workflow.save-evidence", saved ? "passed" : "failed", "Supplied save receipt must identify this project/PID and report projectSaved=true. Save is a separate project-wide operation.");
            }
            else report.Add("workflow.save-evidence", "not-run", "No save receipt supplied.");
            return new { validation = report, projectPath = input.ProjectPath, processId = input.ProcessId, hmiId = input.HmiId, lists, compileDiagnostics = diagnostics,
                suppliedWorkflowComplete = report.CanPublish && allApplied && compiled && saved,
                suppliedNativeStages = new { importAndReadback = allApplied ? "reported-matched" : "not-established", compile = input.CompileReceipt.HasValue ? compiled ? "reported-zero-errors" : "failed" : "not-run", save = input.SaveReceipt.HasValue ? saved ? "reported-saved" : "failed" : "not-run", runtime = "not-run" },
                evidenceScope = "Consistency validation of caller-supplied receipts and XML only; no authentication of native provenance or operation ordering. Retain original artifacts, timestamps and request/context IDs independently.",
                nativeOperationPerformed = false, downloadPerformed = false };
        }
        private static bool Identity(JsonElement value, TextListWorkflowEvidence input, bool nested = false)
        {
            if (nested && (!value.TryGetProperty("projectIdentity", out value) || value.ValueKind != JsonValueKind.Object)) return false;
            return string.Equals(Str(value, "projectPath"), input.ProjectPath, StringComparison.OrdinalIgnoreCase) && Integer(value, "processId") == input.ProcessId;
        }
        private static string? Str(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
        private static int Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int number) ? number : -1;
        private static JsonElement[] Messages(JsonElement receipt)
        {
            if (!receipt.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > 5000) throw new ArgumentException("Compile receipts require a bounded messages array.");
            return messages.EnumerateArray().Select(m => m.Clone()).ToArray();
        }
    }
}
