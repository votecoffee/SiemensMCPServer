using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SiemensTiaOpenness
{
    public sealed class TextEdit
    {
        [System.Text.Json.Serialization.JsonRequired]
        public string Entry { get; set; } = "";
        [System.Text.Json.Serialization.JsonRequired]
        public string Culture { get; set; } = "";
        [System.Text.Json.Serialization.JsonRequired]
        public string ExpectedText { get; set; } = "";
        [System.Text.Json.Serialization.JsonRequired]
        public string NewText { get; set; } = "";
    }

    public sealed class TextValue
    {
        public string Entry { get; set; } = "";
        public string Culture { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Editable { get; set; }
    }

    // Deliberately narrow: edit existing plain text in SingleValue entries only.
    // Preserve every unselected language, attribute, object and default entry.
    internal sealed partial class TextListDocument : ITextListDocument
    {
        private readonly XDocument document;
        private readonly XElement list;
        public string Name { get; private set; }
        public string Fingerprint { get { return Hash(CanonicalList()); } }
        public string ArtifactExtension => "xml";
        public string NativeImportScope => "One classic text list.";
        ITextListDocument ITextListDocument.Edit(TextEdit[] edits) => Edit(edits);
        public string Serialize() => ToXml();

        public TextListDocument(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Root == null || document.Root.Name != "Document")
                throw new InvalidDataException("Expected a classic Openness Document.");
            var lists = document.Root.Elements("Hmi.TextGraphicList.TextList").ToArray();
            if (lists.Length != 1 || document.Root.Elements().Any(e => e.Name.LocalName.StartsWith("Hmi.") && e != lists[0]))
                throw new InvalidDataException("Expected exactly one classic HMI text list.");
            list = lists[0];
            Name = (string)list.Element("AttributeList")?.Element("Name") ?? "";
            if (Name.Length == 0) throw new InvalidDataException("Text-list name is missing.");
            // Reject ambiguous selectors before exposing a snapshot or a preview.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Entries())
            {
                string key = EntryKey(entry);
                if (!keys.Add(key)) throw new InvalidDataException("Duplicate text-list entry: " + key);
                var cultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Items(entry))
                {
                    string culture = (string)item.Element("Culture") ?? "";
                    if (culture.Length == 0 || !cultures.Add(culture) || item.Element("Text") == null)
                        throw new InvalidDataException("Missing or duplicate text culture.");
                }
            }
        }

        public TextValue[] Read()
        {
            return Entries().SelectMany(entry => Items(entry).Select(item => new TextValue {
                Entry = EntryKey(entry), Culture = (string)item.Element("Culture"),
                Text = item.Element("Text").HasElements ? item.Element("Text").ToString(SaveOptions.DisableFormatting) : item.Element("Text").Value,
                Editable = IsSingle(entry) && !item.Element("Text").HasElements
            })).ToArray();
        }

        public TextListDocument Edit(TextEdit[] edits)
        {
            if (edits == null || edits.Length == 0 || edits.Length > 1000)
                throw new ArgumentException("Supply between 1 and 1000 text edits.");
            var candidate = new TextListDocument(ToXml());
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edit in edits)
            {
                if (edit == null || edit.Entry == null || edit.Culture == null || edit.ExpectedText == null || edit.NewText == null)
                    throw new ArgumentException("All edit fields are required.");
                if (!selected.Add(edit.Entry + "\0" + edit.Culture))
                    throw new ArgumentException("Duplicate edit for an entry and culture.");
                XElement entry = candidate.Entries().SingleOrDefault(e => EntryKey(e) == edit.Entry);
                if (entry == null || !IsSingle(entry))
                    throw new InvalidOperationException("Entry does not exist or is not SingleValue: " + edit.Entry);
                var item = Items(entry).SingleOrDefault(a => string.Equals((string)a.Element("Culture"), edit.Culture, StringComparison.OrdinalIgnoreCase));
                var text = item?.Element("Text");
                if (text == null || text.HasElements)
                    throw new InvalidOperationException("Culture does not exist or contains formatted/dynamic text; native handling is required.");
                if (text.Value != edit.ExpectedText)
                    throw new InvalidOperationException("Expected text does not match entry " + edit.Entry + ", culture " + edit.Culture);
                if (edit.NewText.Length > 65535) throw new ArgumentException("Replacement text is too long.");
                XmlConvert.VerifyXmlChars(edit.NewText);
                text.Value = edit.NewText;
            }
            return candidate;
        }

        public string ToXml() { return document.ToString(SaveOptions.DisableFormatting); }
        private IEnumerable<XElement> Entries() { return list.Element("ObjectList")?.Elements("Hmi.TextGraphicList.TextListEntry") ?? Enumerable.Empty<XElement>(); }
        private static IEnumerable<XElement> Items(XElement entry)
        {
            return entry.Elements("ObjectList").Elements("MultilingualText").Where(e => (string)e.Attribute("CompositionName") == "Text")
                .Elements("ObjectList").Elements("MultilingualTextItem").Elements("AttributeList");
        }
        private static bool IsSingle(XElement entry)
        {
            var attrs = entry.Element("AttributeList");
            return (string)attrs?.Element("EntryType") == "SingleValue" && (string)attrs.Element("From") == (string)attrs.Element("To");
        }
        private static string EntryKey(XElement entry)
        {
            var attrs = entry.Element("AttributeList");
            if (attrs == null) throw new InvalidDataException("Entry attributes are missing.");
            string flag = (string)attrs.Element("DefaultEntry");
            if (flag != "true" && flag != "false") throw new InvalidDataException("Invalid default-entry flag.");
            if (flag == "true") return "Default";
            long from, to;
            if (!long.TryParse((string)attrs.Element("From"), NumberStyles.Integer, CultureInfo.InvariantCulture, out from) ||
                !long.TryParse((string)attrs.Element("To"), NumberStyles.Integer, CultureInfo.InvariantCulture, out to))
                throw new InvalidDataException("Unsupported numeric entry selector.");
            return from == to ? from.ToString(CultureInfo.InvariantCulture) : from.ToString(CultureInfo.InvariantCulture) + ".." + to.ToString(CultureInfo.InvariantCulture);
        }
        private string CanonicalList()
        {
            var clone = new XElement(list);
            foreach (var id in clone.DescendantsAndSelf().Attributes("ID").ToArray()) id.Remove();
            foreach (var whitespace in clone.DescendantNodes().OfType<XText>().Where(t => t.Parent.HasElements &&
                !t.Ancestors().Any(e => e.Name.LocalName == "Text") && string.IsNullOrWhiteSpace(t.Value)).ToArray()) whitespace.Remove();
            foreach (var element in clone.DescendantsAndSelf())
            {
                if (!element.HasElements && element.Value.Length == 0) element.RemoveNodes();
                var attributes = element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)).ToArray();
                element.RemoveAttributes(); element.Add(attributes);
            }
            return clone.ToString(SaveOptions.DisableFormatting);
        }
        public static string Hash(string text)
        {
            return Hash(Encoding.UTF8.GetBytes(text));
        }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
