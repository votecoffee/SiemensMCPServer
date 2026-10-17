using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SiemensMCPServer
{
    public sealed class ClassicTranslation
    {
        [JsonRequired] public string Culture { get; set; } = "";
        [JsonRequired] public string Text { get; set; } = "";
    }
    public sealed class ClassicTextListChange
    {
        [JsonRequired] public string Action { get; set; } = "";
        [JsonRequired] public string Entry { get; set; } = "";
        public string TemplateEntry { get; set; } = "";
        public string Culture { get; set; } = "";
        public string TemplateCulture { get; set; } = "";
        public string ExpectedText { get; set; } = "";
        public string ExpectedEntryFingerprint { get; set; } = "";
        public string Text { get; set; } = "";
        public ClassicTranslation[] Translations { get; set; } = Array.Empty<ClassicTranslation>();
    }
    internal sealed partial class TextListDocument
    {
        public static void RequireNativeStructureSupport(Version apiVersion, ClassicTextListChange[] changes)
        {
            if (apiVersion.Major == 15 && apiVersion.Minor == 1 && changes != null && changes.Any(c => c?.Action == "removeTranslation"))
                throw new NotSupportedException("V15.1 XML import retains active language slots. removeTranslation cannot round-trip; clear the existing text with hmi_preview_text_edits instead.");
        }

        public object Structure() => new { entries = Entries().Select(e => new {
            entry = EntryKey(e), fingerprint = EntryFingerprint(e), defaultEntry = EntryKey(e) == "Default", singleValue = IsSingle(e),
            canUseAsTemplate = EntryKey(e) != "Default" && Plain(e),
            cultures = Items(e).Select(i => (string)i.Element("Culture")!).ToArray()
        }).ToArray(), scope = "Existing classic text list. Add/remove ordinary SingleValue entries and translations; default/range/formatted entries remain protected. New cultures must already be active in the project." };

        public TextListDocument ChangeStructure(ClassicTextListChange[] changes, string[] activeCultures)
        {
            if (changes == null || changes.Length == 0 || changes.Length > 1000) throw new ArgumentException("Supply 1 to 1000 classic text-list changes.");
            var candidate = new TextListDocument(ToXml());
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                if (change == null || change.Action == null || change.Entry == null || change.Culture == null || change.TemplateEntry == null || change.TemplateCulture == null
                    || change.ExpectedText == null || change.ExpectedEntryFingerprint == null || change.Text == null || change.Translations == null) throw new ArgumentException("Change fields cannot be null.");
                string scope = change.Entry + "\0" + (change.Action.EndsWith("Entry", StringComparison.Ordinal) ? "" : change.Culture);
                if (!touched.Add(scope) || touched.Any(t => t != scope && t.Split('\0')[0] == change.Entry && (t.EndsWith("\0", StringComparison.Ordinal) || scope.EndsWith("\0", StringComparison.Ordinal))))
                    throw new ArgumentException("Overlapping entry/translation changes require separate previews.");
                var entry = candidate.Entries().SingleOrDefault(e => EntryKey(e) == change.Entry);
                switch (change.Action)
                {
                    case "addEntry":
                        if (!long.TryParse(change.Entry, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) || number.ToString(CultureInfo.InvariantCulture) != change.Entry)
                            throw new ArgumentException("New entry must be a canonical Int64 SingleValue selector.");
                        if (entry != null || candidate.Entries().Where(e => EntryKey(e) != "Default").Any(e => {
                            var attributes = e.Element("AttributeList")!;
                            long from = long.Parse(attributes.Element("From")!.Value, CultureInfo.InvariantCulture), to = long.Parse(attributes.Element("To")!.Value, CultureInfo.InvariantCulture);
                            if (from > to) throw new InvalidDataException("Invalid existing entry range.");
                            return number >= from && number <= to;
                        })) throw new InvalidOperationException("New entry overlaps an existing value/range.");
                        var template = candidate.Entries().SingleOrDefault(e => EntryKey(e) == change.TemplateEntry) ?? throw new InvalidOperationException("Template entry does not exist.");
                        RequirePlain(template);
                        var translations = Translations(change.Translations);
                        var templateCultures = Items(template).Select(i => (string)i.Element("Culture")!).ToArray();
                        if (!new HashSet<string>(templateCultures, StringComparer.OrdinalIgnoreCase).SetEquals(translations.Keys)) throw new ArgumentException("Supply explicit text for every template culture, with no extra cultures. Add translations separately.");
                        var added = ClonePlain(template, candidate.document);
                        added.Element("AttributeList")!.Element("From")!.Value = change.Entry;
                        added.Element("AttributeList")!.Element("To")!.Value = change.Entry;
                        foreach (var item in Items(added)) PlainTextLeaf(item.Element("Text"))!.Value = translations[(string)item.Element("Culture")!];
                        template.Parent!.Add(added); break;
                    case "removeEntry":
                        if (entry == null) throw new InvalidOperationException("Entry does not exist.");
                        RequirePlain(entry);
                        if (EntryFingerprint(entry) != change.ExpectedEntryFingerprint) throw new InvalidOperationException("Entry fingerprint differs; read the list again.");
                        entry.Remove(); break;
                    case "addTranslation":
                    case "removeTranslation":
                        if (entry == null) throw new InvalidOperationException("Entry does not exist.");
                        RequirePlain(entry);
                        var culture = CultureInfo.GetCultureInfo(change.Culture);
                        if (culture.IsNeutralCulture || string.IsNullOrEmpty(culture.Name)) throw new ArgumentException("Use a specific culture such as en-US.");
                        var existing = Items(entry).SingleOrDefault(i => string.Equals((string)i.Element("Culture"), culture.Name, StringComparison.OrdinalIgnoreCase));
                        if (change.Action == "addTranslation")
                        {
                            if (activeCultures == null || !activeCultures.Contains(culture.Name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Culture is not active in the project. Activate it in TIA before adding a translation.");
                            if (existing != null) throw new InvalidOperationException("Translation already exists; use text edits.");
                            var source = Items(entry).SingleOrDefault(i => string.Equals((string)i.Element("Culture"), change.TemplateCulture, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Template culture does not exist.");
                            VerifyText(change.Text);
                            var item = ClonePlain(source.Parent!, candidate.document);
                            item.Element("AttributeList")!.Element("Culture")!.Value = culture.Name;
                            PlainTextLeaf(item.Element("AttributeList")!.Element("Text"))!.Value = change.Text;
                            source.Parent!.Parent!.Add(item);
                        }
                        else
                        {
                            if (existing == null || PlainTextLeaf(existing.Element("Text"))!.Value != change.ExpectedText) throw new InvalidOperationException("Translation is missing or its expected text differs.");
                            if (Items(entry).Count() <= 1) throw new InvalidOperationException("Cannot remove the last translation from an entry.");
                            existing.Parent!.Remove();
                        }
                        break;
                    default: throw new ArgumentException("Use addEntry, removeEntry, addTranslation or removeTranslation.");
                }
            }
            return new TextListDocument(candidate.ToXml());
        }
        private static Dictionary<string, string> Translations(ClassicTranslation[] translations)
        {
            if (translations.Length == 0 || translations.Any(t => t == null || string.IsNullOrEmpty(t.Culture) || t.Text == null)
                || translations.GroupBy(t => t.Culture, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) throw new ArgumentException("Supply unique cultures and explicit translation texts.");
            foreach (var translation in translations) VerifyText(translation.Text);
            return translations.ToDictionary(t => t.Culture, t => t.Text, StringComparer.OrdinalIgnoreCase);
        }
        private static bool Plain(XElement entry) => IsSingle(entry) && !entry.Descendants("LinkList").Any() && !entry.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName == "TargetID" || a.Name.LocalName == "UId")
            && Items(entry).Any() && Items(entry).All(i => PlainTextLeaf(i.Element("Text")) != null);
        private static void RequirePlain(XElement entry)
        {
            if (EntryKey(entry) == "Default" || !Plain(entry)) throw new NotSupportedException("Structure changes require an ordinary plain SingleValue entry. Default, range, formatted and linked entries are protected.");
        }
        private static void VerifyText(string text)
        {
            if (text.Length > 65535) throw new ArgumentException("Translation is too long.");
            XmlConvert.VerifyXmlChars(text);
        }
        private static XElement ClonePlain(XElement source, XDocument document)
        {
            var used = new HashSet<string>(document.Descendants().Attributes("ID").Select(a => a.Value), StringComparer.OrdinalIgnoreCase);
            var clone = new XElement(source); int next = 1;
            foreach (var id in clone.DescendantsAndSelf().Attributes("ID"))
            {
                string value; do { value = (next++).ToString("X", CultureInfo.InvariantCulture); } while (!used.Add(value)); id.Value = value;
            }
            return clone;
        }
        private static string EntryFingerprint(XElement entry)
        {
            var clone = new XElement(entry); clone.DescendantsAndSelf().Attributes("ID").Remove();
            clone.DescendantsAndSelf().Where(e => e.HasElements).Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
            return Hash(clone.ToString(SaveOptions.DisableFormatting));
        }
    }
}
