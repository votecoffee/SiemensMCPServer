using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SiemensMCPServer
{
    internal sealed class HmiValidationCheck
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "passed";
        public string Location { get; set; } = "";
        public string Message { get; set; } = "";
        public string Classification { get; set; } = "not-compared";
        public string Applicability { get; set; } = "required-for-selected-profile";
    }

    internal sealed class HmiValidationReport
    {
        public string Profile { get; set; } = "";
        public string RuleSetVersion { get; set; } = "1.0.0";
        public string ToolVersion { get; set; } = typeof(HmiValidationReport).Assembly.GetName().Version?.ToString() ?? "unknown";
        public string TargetVersion { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string SourceEncoding { get; set; } = "";
        public string? ObjectName { get; set; }
        public string[] RequiredCompanions { get; set; } = Array.Empty<string>();
        public List<HmiValidationCheck> Checks { get; set; } = new List<HmiValidationCheck>();
        public string Status => Checks.Any(c => c.Status == "failed") ? "failed" : Checks.Any(c => c.Status == "unsupported") ? "unsupported" : "passed";
        public bool CanPublish => Status == "passed";
        public string Summary => Profile + ": " + Status + "; " + Checks.Count(c => c.Status == "failed") + " failed check(s). Native acceptance remains separate.";
        public object Acceptance => new { nativeImport = "not-run", nativeReadback = "not-run", compile = "not-run", save = "not-run", runtime = "not-run", downloadPerformed = false };
        public void Add(string id, string status, string message, string location = "") => Checks.Add(new HmiValidationCheck { Id = id, Status = status, Message = message, Location = location });
        public void RequirePassed()
        {
            if (!CanPublish) throw new InvalidDataException(string.Join("; ", Checks.Where(c => c.Status == "failed" || c.Status == "unsupported").Select(c => c.Id + " at " + c.Location + ": " + c.Message)));
        }
    }

    // One byte-level encoding gate is shared by preparation, validation and native transfers.
    // A parser given an already-decoded string cannot validate the source declaration spelling.
    internal static class NativeXmlValidation
    {
        public const int MaximumBytes = 64 * 1024 * 1024;
        private static readonly Regex Declaration = new Regex(@"\A<\?xml\s+[^?]{0,4096}\?>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex EncodingField = new Regex("\\bencoding\\s*=\\s*(['\"])([^'\"]+)\\1", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static string Decode(byte[] bytes, out string encoding, out int bom)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("Supply XML bytes within the 64 MiB transfer limit.");
            Encoding decoder;
            if (bytes.Take(4).SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.Take(4).SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff }))
                throw new NotSupportedException("UTF-32 is outside this native XML profile.");
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = "utf-16le"; bom = 2; decoder = new UnicodeEncoding(false, true, true); }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = "utf-16be"; bom = 2; decoder = new UnicodeEncoding(true, true, true); }
            else { encoding = "utf-8"; bom = bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0; decoder = new UTF8Encoding(false, true); }
            return decoder.GetString(bytes, bom, bytes.Length - bom);
        }

        public static HmiValidationReport Analyze(byte[] bytes, string targetVersion = "", bool textListMetadata = false)
        {
            var report = new HmiValidationReport { Profile = textListMetadata ? "siemens.classic-text-list.native-metadata" : "siemens.native-xml.encoding", TargetVersion = targetVersion };
            if (bytes != null) report.SourceSha256 = TextListDocument.Hash(bytes);
            string text;
            try { text = Decode(bytes!, out string encoding, out _); report.SourceEncoding = encoding; }
            catch (NotSupportedException ex) { report.Add("xml.encoding", "unsupported", ex.Message, "XML declaration"); return report; }
            catch (Exception ex) when (ex is DecoderFallbackException || ex is InvalidDataException)
            { report.Add("xml.encoding", "failed", ex.Message, "XML declaration"); return report; }
            var declaration = Declaration.Match(text);
            var field = declaration.Success ? EncodingField.Match(declaration.Value) : Match.Empty;
            string spelling = field.Success ? field.Groups[2].Value : "";
            bool utf8 = report.SourceEncoding == "utf-8";
            if (spelling.Equals("utf8", StringComparison.OrdinalIgnoreCase))
                report.Add("xml.encoding.native-alias", "failed", "TIA V15.1 rejects the utf8 declaration alias. Use utf-8; parser success and an unchanged baseline do not excuse this failure.", "XML declaration/encoding");
            else if (spelling.Length != 0 && !(utf8 ? spelling.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                : spelling.Equals("utf-16", StringComparison.OrdinalIgnoreCase) || spelling.Equals(report.SourceEncoding, StringComparison.OrdinalIgnoreCase)))
                report.Add("xml.encoding.bytes", "failed", "XML declaration does not match supported strict UTF-8 or BOM-marked UTF-16 source bytes.", "XML declaration/encoding");
            else report.Add("xml.encoding", "passed", "Declaration bytes and strict decoding agree; existing valid UTF-16 is preserved.", "XML declaration");

            XDocument document;
            try
            {
                using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes }))
                    document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                report.Add("xml.well-formed", "passed", "Bounded XML parsed without external entities.", "/");
            }
            catch (XmlException ex) { report.Add("xml.well-formed", "failed", ex.Message, "line " + ex.LineNumber + ":" + ex.LinePosition); return report; }
            if (!textListMetadata) return report;
            if (targetVersion != "V15.1") report.Add("metadata.profile", "unsupported", "The native TextLists metadata profile is scoped to V15.1; other target versions require separate acceptance.", "/Document/Engineering");
            var root = document.Root;
            if (root?.Name != "Document" || root.Element("Engineering")?.Attribute("version")?.Value != targetVersion)
                report.Add("metadata.engineering", "failed", "Expected an Openness Document with the exact selected Engineering version.", "/Document/Engineering");
            else report.Add("metadata.engineering", "passed", "Engineering metadata matches the selected version.", "/Document/Engineering");
            try
            {
                var list = new TextListDocument(text);
                report.ObjectName = list.Name;
                report.Add("text-list.identity", "passed", "Exactly one named classic text list with unambiguous keys/cultures.", "/Document/Hmi.TextGraphicList.TextList");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is InvalidOperationException)
            { report.Add("text-list.identity", "failed", ex.Message, "/Document"); }
            var ids = document.Descendants().Attributes("ID").ToArray();
            foreach (var group in ids.GroupBy(a => a.Value, StringComparer.Ordinal).Where(g => g.Key.Length == 0 || g.Count() > 1))
                report.Add("metadata.unique-ids", "failed", "Duplicate or empty native ID: " + group.Key, "/Document//*[@ID]");
            if (!report.Checks.Any(c => c.Id == "metadata.unique-ids")) report.Add("metadata.unique-ids", "passed", "Native XML IDs are unique within this document.");
            foreach (var node in document.Descendants())
            {
                var children = node.Elements().ToArray();
                int links = Array.FindIndex(children, e => e.Name == "LinkList"), objects = Array.FindIndex(children, e => e.Name == "ObjectList");
                if (links >= 0 && objects >= 0 && links > objects)
                    report.Add("metadata.link-order", "failed", "LinkList must precede ObjectList; native links can otherwise be lost.", Location(node));
                if (node.Name == "Text" && node.Parent?.Name == "AttributeList" && node.Parent.Parent?.Name == "MultilingualTextItem"
                    && !string.IsNullOrEmpty(node.Value) && (node.Elements("body").Count() != 1 || !node.Element("body")!.Elements("p").Any()))
                    report.Add("metadata.native-text", "failed", "Nonempty V15.1 screen/list text needs native body/p markup; empty translations remain empty.", Location(node));
            }
            foreach (string rule in new[] { "metadata.link-order", "metadata.native-text" })
                if (!report.Checks.Any(c => c.Id == rule)) report.Add(rule, "passed", "Known V15.1 metadata condition satisfied.");
            var companions = new List<string>();
            foreach (var node in document.Descendants().Where(e => string.Equals((string?)e.Attribute("external"), "path", StringComparison.OrdinalIgnoreCase)))
            {
                string path = node.Value.Replace('\\', '/');
                if (node.HasElements || path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(p => p == "" || p == "." || p == ".."))
                    report.Add("xml.companion-scope", "failed", "Companions must use safe resource-relative paths.", Location(node));
                else companions.Add(path);
            }
            report.RequiredCompanions = companions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return report;
        }

        public static string Read(byte[] bytes)
        {
            Analyze(bytes).RequirePassed();
            return Decode(bytes, out _, out _);
        }

        public static byte[] SnapshotBytes(string text)
        {
            var declaration = Declaration.Match(text);
            var field = declaration.Success ? EncodingField.Match(declaration.Value) : Match.Empty;
            string spelling = field.Success ? field.Groups[2].Value.ToLowerInvariant() : "utf-8";
            if (spelling == "utf-8") return new UTF8Encoding(false, true).GetBytes(text);
            if (spelling == "utf-16" || spelling == "utf-16le" || spelling == "utf-16be")
            {
                var encoding = new UnicodeEncoding(spelling == "utf-16be", true, true);
                return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            }
            throw new InvalidDataException("Snapshot declaration uses an unsupported native encoding; use byte-level preflight before creating an artifact.");
        }

        public static byte[] RepairDeclaration(byte[] original)
        {
            string text = Decode(original, out string encoding, out int bom);
            var declaration = Declaration.Match(text);
            var field = declaration.Success ? EncodingField.Match(declaration.Value) : Match.Empty;
            if (!field.Success || !field.Groups[2].Value.Equals("utf8", StringComparison.OrdinalIgnoreCase)) return original.ToArray();
            if (encoding != "utf-8") throw new InvalidDataException("Declaration-only utf8 repair requires UTF-8 body bytes; valid native UTF-16 must not be transcoded.");
            int offset = bom + Encoding.UTF8.GetByteCount(text.Substring(0, field.Groups[2].Index));
            int length = Encoding.UTF8.GetByteCount(field.Groups[2].Value);
            return original.Take(offset).Concat(Encoding.ASCII.GetBytes("utf-8")).Concat(original.Skip(offset + length)).ToArray();
        }

        private static string Location(XElement element) => "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(e => e.Name.LocalName + (e.Attribute("ID") == null ? "" : "[@ID=" + e.Attribute("ID")!.Value + "]")));
    }
}
