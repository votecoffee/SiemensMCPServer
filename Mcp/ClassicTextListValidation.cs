using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicDeltaValidation
    {
        [JsonRequired] public string BeforeXml { get; set; } = "";
        [JsonRequired] public string CandidateXml { get; set; } = "";
        [JsonRequired] public string ExpectedBeforeFingerprint { get; set; } = "";
        public string ExpectedCandidateSha256 { get; set; } = "";
        public string? ReadbackXml { get; set; }
        public ClassicTextListChange[] Changes { get; set; } = Array.Empty<ClassicTextListChange>();
        public TextEdit[] Edits { get; set; } = Array.Empty<TextEdit>();
        public string[] ActiveCultures { get; set; } = Array.Empty<string>();
        public string TargetVersion { get; set; } = "V15.1";
        public bool BlankOtherNewTranslations { get; set; }
        public string AuthoredCulture { get; set; } = "";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicByteValidation
    {
        [JsonRequired] public string XmlBytesBase64 { get; set; } = "";
        public string TargetVersion { get; set; } = "V15.1";
        public string ExpectedSha256 { get; set; } = "";
        public string? BaselineBytesBase64 { get; set; }
    }

    internal static class ClassicTextListValidation
    {
        internal static object Schema(string operation)
        {
            var text = new { type = "string" };
            if (operation == "validate_native_text_list") return new { type = "object", additionalProperties = false, required = new[] { "xmlBytesBase64" }, properties = new {
                xmlBytesBase64 = text, targetVersion = text, expectedSha256 = text, baselineBytesBase64 = text } };
            return new { type = "object", additionalProperties = false, required = new[] { "beforeXml", "candidateXml", "expectedBeforeFingerprint" }, properties = new {
                beforeXml = text, candidateXml = text, expectedBeforeFingerprint = text, expectedCandidateSha256 = text, readbackXml = text, targetVersion = text,
                activeCultures = new { type = "array", items = text }, changes = new { type = "array", items = new { type = "object" } }, edits = new { type = "array", items = new { type = "object" } },
                blankOtherNewTranslations = new { type = "boolean" }, authoredCulture = text } };
        }

        internal static object Bytes(ClassicByteValidation input)
        {
            if (input.XmlBytesBase64 == null || input.XmlBytesBase64.Length > 23 * 1024 * 1024) throw new ArgumentException("Supply bounded source bytes as base64.");
            var report = NativeXmlValidation.Analyze(Convert.FromBase64String(input.XmlBytesBase64), input.TargetVersion, true);
            if (input.ExpectedSha256.Length != 0)
                report.Add("source.hash", report.SourceSha256 == input.ExpectedSha256 ? "passed" : "failed", "Compare the exact source bytes with the reviewed hash.");
            if (input.BaselineBytesBase64 != null)
            {
                if (input.BaselineBytesBase64.Length > 23 * 1024 * 1024) throw new ArgumentException("Baseline exceeds the source-byte limit.");
                var baseline = NativeXmlValidation.Analyze(Convert.FromBase64String(input.BaselineBytesBase64), input.TargetVersion, true);
                foreach (var check in report.Checks.Where(c => c.Status != "passed"))
                    check.Classification = baseline.Checks.Any(c => c.Id == check.Id && c.Location == check.Location && c.Message == check.Message && c.Status == check.Status) ? "inherited-still-blocking" : "new";
            }
            return report;
        }

        internal static JsonElement Delta(ClassicDeltaValidation input)
        {
            RequireXml(input.BeforeXml); RequireXml(input.CandidateXml);
            var report = new HmiValidationReport { Profile = "siemens.classic-text-list.delta-preservation", TargetVersion = input.TargetVersion, SourceSha256 = TextListDocument.Hash(input.BeforeXml), SourceEncoding = "decoded-string; original byte encoding is not established" };
            string candidateHash = TextListDocument.Hash(input.CandidateXml);
            string? beforeFingerprint = null, candidateFingerprint = null, intendedFingerprint = null, readbackFingerprint = null;
            object? rows = null;
            try
            {
                var before = new TextListDocument(input.BeforeXml);
                var candidate = new TextListDocument(input.CandidateXml);
                beforeFingerprint = before.Fingerprint; candidateFingerprint = candidate.Fingerprint;
                report.ObjectName = before.Name;
                report.Add("delta.baseline", before.Fingerprint == input.ExpectedBeforeFingerprint ? "passed" : "failed", "Source matches the explicitly reviewed native baseline fingerprint.");
                if (input.ExpectedCandidateSha256.Length != 0)
                    report.Add("delta.candidate-bytes", candidateHash == input.ExpectedCandidateSha256 ? "passed" : "failed", "Candidate UTF-8 snapshot hash matches the reviewed value.");
                if (input.Changes == null || input.Edits == null || input.ActiveCultures == null) throw new ArgumentException("Delta arrays cannot be null.");
                if (input.TargetVersion == "V15.1") TextListDocument.RequireNativeStructureSupport(new Version(15, 1), input.Changes);
                else if (input.TargetVersion.Length != 0) report.Add("delta.native-version", "unsupported", "Native structure policy is checked for V15.1 only.");
                if (input.BlankOtherNewTranslations)
                {
                    if (string.IsNullOrWhiteSpace(input.AuthoredCulture)) throw new ArgumentException("Choose an explicit authoredCulture for the blank-other-new-translations policy.");
                    foreach (var change in input.Changes)
                    {
                        bool violation = change.Action == "addEntry" && change.Translations.Any(t => !string.Equals(t.Culture, input.AuthoredCulture, StringComparison.OrdinalIgnoreCase) && t.Text != "")
                            || change.Action == "addTranslation" && !string.Equals(change.Culture, input.AuthoredCulture, StringComparison.OrdinalIgnoreCase) && change.Text != "";
                        if (violation) report.Add("delta.new-translations", "failed", "New other-language values must be explicitly blank under the selected policy.", "entry=" + change.Entry);
                    }
                    if (!report.Checks.Any(c => c.Id == "delta.new-translations")) report.Add("delta.new-translations", "passed", "New other-language values are explicitly blank; existing translations are preserved by the exact delta.");
                }
                var intended = input.Changes.Length == 0 ? before : before.ChangeStructure(input.Changes, input.ActiveCultures);
                if (input.Edits.Length != 0) intended = intended.Edit(input.Edits);
                intendedFingerprint = intended.Fingerprint;
                report.Add("delta.exact-preservation", candidate.Fingerprint == intended.Fingerprint ? "passed" : "failed",
                    "Compare the complete list with the declared entry/translation operations: unselected keys, defaults, cultures and metadata must remain unchanged. Volatile native IDs are excluded from the semantic fingerprint.");
                var original = before.Read().ToDictionary(Key, v => v);
                var expected = intended.Read().ToDictionary(Key, v => v);
                var actual = candidate.Read().ToDictionary(Key, v => v);
                var differences = original.Keys.Union(expected.Keys).Union(actual.Keys).Where(k => Text(original, k) != Text(actual, k) || Text(expected, k) != Text(actual, k)).ToArray();
                rows = differences.Take(1000).Select(k => new { entry = (actual.ContainsKey(k) ? actual[k] : expected.ContainsKey(k) ? expected[k] : original[k]).Entry,
                    culture = (actual.ContainsKey(k) ? actual[k] : expected.ContainsKey(k) ? expected[k] : original[k]).Culture,
                    before = Text(original, k), expected = Text(expected, k), candidate = Text(actual, k), matchesPlan = Text(expected, k) == Text(actual, k) }).ToArray();
                report.Add("delta.report-coverage", differences.Length <= 1000 ? "passed" : "failed", "The retained difference report must be complete within 1000 changed translation rows.");
                if (input.ReadbackXml != null)
                {
                    RequireXml(input.ReadbackXml);
                    readbackFingerprint = new TextListDocument(input.ReadbackXml).Fingerprint;
                    report.Add("readback.supplied-export", readbackFingerprint == candidate.Fingerprint ? "passed" : "failed", "Supplied post-import export matches the complete candidate semantically; this local check does not perform or authenticate a native import.");
                }
                else report.Add("readback.supplied-export", "not-run", "No post-import export supplied.");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.IO.InvalidDataException || ex is System.Xml.XmlException || ex is NotSupportedException)
            { report.Add("delta.declared-plan", "failed", ex.Message); }
            return JsonSerializer.SerializeToElement(new { validation = report, candidateSha256 = candidateHash, beforeFingerprint, candidateFingerprint, intendedFingerprint, readbackFingerprint,
                differences = rows, hashScope = "UTF-8 bytes of the supplied XML strings; use validate_native_text_list for original declaration/BOM bytes.",
                nativeImportPerformed = false, projectChanged = false, downloadPerformed = false }, JsonDefaults.Options);
        }

        private static string Key(TextValue value) => value.Entry + "\0" + value.Culture.ToLowerInvariant();
        private static string? Text(Dictionary<string, TextValue> values, string key) => values.TryGetValue(key, out var value) ? value.Text : null;
        private static void RequireXml(string xml) { if (xml == null || xml.Length == 0 || xml.Length > NativeXmlValidation.MaximumBytes) throw new ArgumentException("Supply XML within the 16 MiB character limit."); }
    }
}
