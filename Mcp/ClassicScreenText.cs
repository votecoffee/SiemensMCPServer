using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenTextInspection
    {
        [JsonRequired] public string Xml { get; set; } = "";
        public int Offset { get; set; }
        public int MaxRuns { get; set; } = 100;
        public int MaxCharacters { get; set; } = 1024;
        public string Culture { get; set; } = "";
        public string Query { get; set; } = "";
        public bool CaseSensitive { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenTextSelection
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string Culture { get; set; } = "";
        [JsonRequired] public string ExpectedTextFingerprint { get; set; } = "";
        [JsonRequired] public int RunIndex { get; set; }
        [JsonRequired] public string ExpectedText { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenTextEdit
    {
        [JsonRequired] public ClassicScreenTextSelection Selection { get; set; } = null!;
        [JsonRequired] public string NewText { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenTextRequest
    {
        [JsonRequired] public ClassicScreenTextEdit[] Edits { get; set; } = Array.Empty<ClassicScreenTextEdit>();
    }
    internal sealed partial class ClassicXmlDocument
    {
        private const int ScreenTextRunLimit = 4096;
        private static readonly HashSet<string> CaptionInlineTags = new HashSet<string>(new[] { "span", "b", "strong", "i", "em", "u", "font", "sub", "sup", "br" }, StringComparer.Ordinal);
        private static readonly HashSet<string> CaptionAttributes = new HashSet<string>(new[] { "style", "color", "face", "size", "align", "dir", "lang" }, StringComparer.Ordinal);
        private sealed class CaptionRun
        {
            public XText? Node;
            public XElement? EmptySlot;
            public string Text => Node?.Value ?? "";
        }
        private sealed class CaptionField
        {
            public XElement? Text;
            public XElement? EncodedBody;
            public XDocument? EncodedDocument;
            public string XPath = "", OwnerXPath = "", OwnerName = "", Property = "", Culture = "", Fingerprint = "", Storage = "plain";
            public string? UnsupportedReason;
            public List<CaptionRun> Runs = new List<CaptionRun>();
        }
        private CaptionField[] CaptionFields()
        {
            if (!new[] { "Hmi.Screen.Screen", "Hmi.Screen.ScreenTemplate", "Hmi.Screen.ScreenPopup", "Hmi.Screen.ScreenSlidein", "Hmi.Screen.ScreenOverview", "Hmi.Screen.ScreenGlobalElements" }.Contains(NativeType))
                throw new ArgumentException("Screen text requires a native classic screen resource.");
            var nodes = resource.DescendantsAndSelf().Take(50001).ToArray();
            if (Name.Length > 512 || nodes.Length > 50000 || nodes.Any(n => n.Ancestors().Take(65).Count() > 64))
                throw new ArgumentException("Screen text exceeds 512 name characters, 50000 XML elements or 64 levels.");
            var result = new List<CaptionField>(); int totalRuns = 0;
            foreach (var multilingual in nodes.Where(n => n.Name == "MultilingualText" && n.Parent?.Name == "ObjectList"
                && n.Parent.Parent?.Name.NamespaceName == "" && n.Parent.Parent.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)))
            {
                XElement owner = multilingual.Parent!.Parent!;
                string property = (string?)multilingual.Attribute("CompositionName") ?? "";
                string ownerName = owner.Element("AttributeList")?.Element("Name")?.Value ?? "";
                string ownerPath = Path(owner);
                if (property.Length > 128 || ownerName.Length > 512 || ownerPath.Length > 4096) throw new ArgumentException("Screen text owner/property metadata exceeds inspection bounds.");
                var items = multilingual.Elements("ObjectList").Elements("MultilingualTextItem").ToArray();
                var duplicateCultures = items.Select(i => i.Element("AttributeList")?.Element("Culture")?.Value ?? "")
                    .GroupBy(c => c, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                bool duplicateProperty = owner.Elements("ObjectList").Elements("MultilingualText").Count(m => (string?)m.Attribute("CompositionName") == property) != 1;
                foreach (var item in items)
                {
                    var attributes = item.Elements("AttributeList").ToArray();
                    var cultures = attributes.SelectMany(a => a.Elements("Culture")).ToArray();
                    var texts = attributes.SelectMany(a => a.Elements("Text")).ToArray();
                    string culture = cultures.Length == 1 ? cultures[0].Value : "";
                    if (culture.Length > 128) throw new ArgumentException("Screen text culture exceeds 128 characters.");
                    var field = new CaptionField { Text = texts.Length == 1 ? texts[0] : null, XPath = Path(texts.Length == 1 ? texts[0] : item),
                        OwnerXPath = ownerPath, OwnerName = ownerName, Property = property, Culture = culture };
                    if (field.XPath.Length > 4096) throw new ArgumentException("Screen text paths exceed 4096 characters.");
                    if (attributes.Length != 1 || cultures.Length != 1 || cultures[0].HasElements || string.IsNullOrWhiteSpace(culture) || texts.Length != 1)
                        field.UnsupportedReason = "Exactly one AttributeList, scalar Culture and Text field are required.";
                    else if (property.Length == 0 || duplicateProperty || duplicateCultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
                        field.UnsupportedReason = "Missing/duplicate text property or duplicate culture is inspection-only.";
                    else if (multilingual.Ancestors().Any(a => BehaviorCompositions.Contains((string?)a.Attribute("CompositionName"), StringComparer.Ordinal)))
                        field.UnsupportedReason = "Behavior/dynamization text is outside the screen caption workflow.";
                    if (field.Text != null)
                    {
                        field.Fingerprint = TextListDocument.Hash(field.Text.ToString(SaveOptions.DisableFormatting));
                        if (field.UnsupportedReason == null) ParseCaption(field);
                    }
                    totalRuns += Math.Max(1, field.Runs.Count);
                    if (totalRuns > 20000) throw new ArgumentException("Screen text exceeds 20000 inspected runs.");
                    result.Add(field);
                }
            }
            return result.ToArray();
        }
        private static void ParseCaption(CaptionField field)
        {
            XElement text = field.Text!;
            if (text.HasAttributes || text.Value.Length > 65536)
            { field.UnsupportedReason = "Text metadata or content beyond 65536 characters requires native handling."; return; }
            XElement content = text;
            if (text.HasElements)
            {
                var roots = text.Elements().ToArray();
                if (roots.Length != 1 || roots[0].Name != "body" || text.Nodes().Any(n => !(n is XElement) && !(n is XText t && string.IsNullOrWhiteSpace(t.Value))))
                { field.UnsupportedReason = "Formatted text requires one body root without mixed outer content."; return; }
                content = roots[0]; field.Storage = "embeddedBody";
            }
            else if (text.Value.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                try
                {
                    using (var reader = XmlReader.Create(new StringReader(text.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                        field.EncodedDocument = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                    content = field.EncodedDocument.Root!;
                    if (content?.Name != "body" || field.EncodedDocument.Declaration != null || field.EncodedDocument.Nodes().Any(n => n != content && !(n is XText t && string.IsNullOrWhiteSpace(t.Value))))
                        throw new XmlException("Expected only a body document.");
                    field.EncodedBody = content; field.Storage = "encodedBody";
                }
                catch (XmlException) { field.UnsupportedReason = "Unrecognized serialized markup is inspection-only."; return; }
            }
            if (content == text)
            {
                if (text.Nodes().Any(n => !(n is XText))) { field.UnsupportedReason = "Unsupported text node kind."; return; }
                AddCaptionRuns(text, field.Runs); return;
            }
            var elements = content.DescendantsAndSelf().Take(513).ToArray();
            if (elements.Length > 512 || elements.Any(e => e != content && e.Ancestors().TakeWhile(a => a != content).Count() > 32))
            { field.UnsupportedReason = "Formatted caption exceeds 512 elements or 32 levels."; return; }
            foreach (var element in elements)
            {
                string tag = element.Name.LocalName;
                if (element.Name.NamespaceName != "" || !(element == content || tag == "p" && element.Parent == content || CaptionInlineTags.Contains(tag) && element.Parent != content)
                    || element.Nodes().Any(n => !(n is XElement) && !(n is XText))
                    || element.Attributes().Any(a => a.IsNamespaceDeclaration || !(a.Name.NamespaceName == "" && CaptionAttributes.Contains(a.Name.LocalName) || a.Name == XNamespace.Xml + "space") || a.Value.Length > 2048)
                    || tag == "br" && element.Nodes().Any()
                    || element == content && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                { field.UnsupportedReason = "Unknown/dynamic rich-text elements, attributes or node kinds are inspection-only."; return; }
            }
            if (!content.Elements("p").Any()) { field.UnsupportedReason = "Formatted captions require existing paragraph elements."; return; }
            AddCaptionRuns(content, field.Runs);
            if (field.Runs.Count == 0) field.UnsupportedReason = "No existing text run or empty text slot is available.";
        }
        private static void AddCaptionRuns(XElement element, List<CaptionRun> runs)
        {
            if (element.Name == "br") return;
            if (!element.Nodes().Any()) { runs.Add(new CaptionRun { EmptySlot = element }); return; }
            foreach (var node in element.Nodes())
                if (node is XElement child) AddCaptionRuns(child, runs);
                else if (node is XText text && !(element.Name == "body" && string.IsNullOrWhiteSpace(text.Value))) runs.Add(new CaptionRun { Node = text });
        }
        public object ScreenTextCatalog(ClassicScreenTextInspection request)
        {
            if (request.Offset < 0 || request.Offset > 20000 || request.MaxRuns < 1 || request.MaxRuns > 200 || request.MaxCharacters < 1 || request.MaxCharacters > ScreenTextRunLimit
                || request.Culture == null || request.Culture.Length > 128 || request.Query == null || request.Query.Length > 256)
                throw new ArgumentException("Screen text inspection bounds: offset 0..20000, maxRuns 1..200, maxCharacters 1..4096, culture <=128, query <=256.");
            var matches = new List<object>(); int total = 0, next = request.Offset, characters = 0; bool limited = false;
            foreach (var field in CaptionFields())
            {
                if (request.Culture.Length != 0 && !string.Equals(field.Culture, request.Culture, StringComparison.OrdinalIgnoreCase)) continue;
                int count = field.UnsupportedReason == null ? field.Runs.Count : 1;
                for (int index = 0; index < count; index++)
                {
                    string value = field.UnsupportedReason == null ? field.Runs[index].Text : field.Text?.Value ?? "";
                    if (request.Query.Length != 0 && value.IndexOf(request.Query, request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int position = total++;
                    if (position < request.Offset || matches.Count >= request.MaxRuns || limited) continue;
                    bool clipped = value.Length > request.MaxCharacters;
                    string? reason = field.UnsupportedReason ?? (value.Length > ScreenTextRunLimit ? "Text run exceeds the 4096-character edit limit." : clipped ? "Text is clipped; increase maxCharacters to inspect the complete run before editing." : null);
                    var row = new { xPath = field.XPath, ownerXPath = field.OwnerXPath, ownerName = field.OwnerName, property = field.Property, culture = field.Culture,
                        storage = field.Storage, textFingerprint = field.Fingerprint, runIndex = field.UnsupportedReason == null ? (int?)index : null,
                        text = value.Substring(0, Math.Min(value.Length, request.MaxCharacters)), textClipped = clipped, canPreview = reason == null, unsupportedReason = reason,
                        selection = reason == null ? new ClassicScreenTextSelection { XPath = field.XPath, Culture = field.Culture, ExpectedTextFingerprint = field.Fingerprint, RunIndex = index, ExpectedText = value } : null };
                    int cost = JsonSerializer.Serialize(row, JsonDefaults.Options).Length;
                    if (characters + cost > 258048) { limited = true; continue; }
                    matches.Add(row); characters += cost; next++;
                }
            }
            return new { Name, NativeType, runs = matches, matchedRuns = total, offset = request.Offset, nextOffset = next, hasMore = next < total, payloadLimited = limited,
                scope = "Stored multilingual screen text runs only. Exact field fingerprints, culture and run indexes bind selections. Formatting, paragraph boundaries and other languages are preserved; runtime bindings, rendered appearance and native acceptance are not evaluated." };
        }
        public object EditScreenTexts(ClassicScreenTextRequest request)
        {
            if (request?.Edits == null || request.Edits.Length < 1 || request.Edits.Length > 200) throw new ArgumentException("Supply 1 to 200 exact screen text-run edits.");
            // Work on a clone so failed validation/serialization never changes this document.
            var candidate = new ClassicXmlDocument(document.ToString(SaveOptions.DisableFormatting));
            var fields = candidate.CaptionFields().ToDictionary(f => f.XPath, StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal); var changes = new List<Tuple<ClassicScreenTextEdit, CaptionField, CaptionRun>>();
            foreach (var edit in request.Edits)
            {
                var selection = edit?.Selection;
                if (selection == null || selection.XPath == null || selection.Culture == null || selection.ExpectedTextFingerprint == null || selection.ExpectedText == null || edit!.NewText == null
                    || edit.NewText.Length > ScreenTextRunLimit || selection.XPath.Length > 4096 || selection.RunIndex < 0)
                    throw new ArgumentException("Every text edit requires a bounded selection and newText of at most 4096 characters.");
                if (!selected.Add(selection.XPath + "\0" + selection.RunIndex.ToString(CultureInfo.InvariantCulture))) throw new ArgumentException("Duplicate screen text-run edit.");
                if (!fields.TryGetValue(selection.XPath, out var field) || field.UnsupportedReason != null || selection.RunIndex >= field.Runs.Count)
                    throw new ArgumentException("Select an available exact text path/run from inspect_screen_texts; unknown or dynamic content cannot be edited.");
                var run = field.Runs[selection.RunIndex];
                if (selection.Culture != field.Culture || selection.ExpectedTextFingerprint != field.Fingerprint || selection.ExpectedText != run.Text)
                    throw new InvalidOperationException("Screen caption culture, formatting fingerprint or expected text changed; inspect it again.");
                if (run.Text.Length > ScreenTextRunLimit) throw new ArgumentException("Selected text run exceeds the 4096-character edit limit.");
                XmlConvert.VerifyXmlChars(edit.NewText);
                if (edit.NewText != run.Text) changes.Add(Tuple.Create(edit, field, run));
            }
            if (changes.Count == 0) throw new ArgumentException("The screen text preview contains no changes.");
            foreach (var change in changes)
                if (change.Item3.Node != null) change.Item3.Node.Value = change.Item1.NewText;
                else change.Item3.EmptySlot!.Add(new XText(change.Item1.NewText));
            foreach (var field in changes.Select(c => c.Item2).Distinct().Where(f => f.EncodedBody != null)) field.Text!.Value = field.EncodedDocument!.ToString(SaveOptions.DisableFormatting);
            return new { candidateXml = candidate.document.ToString(SaveOptions.DisableFormatting), changes = new {
                affectedTextFields = changes.Select(c => c.Item2).Distinct().Count(),
                edits = changes.Select(c => new { c.Item1.Selection, c.Item1.NewText, storage = c.Item2.Storage }).ToArray(),
                scope = "Only selected static text runs or existing empty text slots. No language slots, formatting elements, attributes, identities or references are added/removed." } };
        }
    }
}
