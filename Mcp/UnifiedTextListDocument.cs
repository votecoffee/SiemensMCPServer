using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SiemensTiaOpenness.Mcp
{
    internal static class TextListDocuments
    {
        public static ITextListDocument Parse(string snapshot) => snapshot.TrimStart().StartsWith("{", StringComparison.Ordinal)
            ? (ITextListDocument)new UnifiedTextListDocument(snapshot) : new TextListDocument(snapshot);
    }

    internal sealed class UnifiedTextListBundle
    {
        public string Format { get; set; } = "unified-yaml-v2";
        public string ListName { get; set; } = "";
        public string Configuration { get; set; } = "";
        public string TextLibrary { get; set; } = "";
        public string[] NativeListNames { get; set; } = Array.Empty<string>();
    }

    // Siemens V21's documented YAML v2 schema. Preserve configuration and patch only
    // the source spans of existing translation scalars, retaining all other bytes/lines.
    internal sealed class UnifiedTextListDocument : ITextListDocument
    {
        public UnifiedTextListBundle Bundle { get; }
        private readonly YamlMappingNode config;
        private readonly YamlMappingNode library;
        private readonly Dictionary<string, YamlMappingNode> lists = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> referenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Row> rows = new List<Row>();
        public string Name => Bundle.ListName;
        public string ArtifactExtension => "json";
        public string NativeImportScope => "All exported Unified user text lists are recreated by native paired-YAML import. Configuration and unselected translations remain unchanged; system lists are excluded.";
        public string Fingerprint => TextListDocument.Hash(JsonSerializer.Serialize(new {
            format = Bundle.Format, listName = Name, configuration = Canonical(config), textLibrary = Canonical(library),
            nativeListNames = Bundle.NativeListNames.OrderBy(n => n, StringComparer.Ordinal)
        }, JsonDefaults.Options));

        public UnifiedTextListDocument(string snapshot)
        {
            Bundle = JsonSerializer.Deserialize<UnifiedTextListBundle>(snapshot, JsonDefaults.Options) ?? throw new InvalidDataException("Missing Unified YAML bundle.");
            if (Bundle.Format != "unified-yaml-v2" || string.IsNullOrEmpty(Bundle.ListName) || Bundle.NativeListNames == null)
                throw new InvalidDataException("Unsupported Unified snapshot format.");
            config = ParseYaml(Bundle.Configuration);
            library = ParseYaml(Bundle.TextLibrary);
            foreach (var container in Map(Required(config, "TextListContainers")).Children.Values)
            {
                var value = Map(container);
                if (Scalar(Required(value, "ResourceListType")) != "TextList") throw new InvalidDataException("Only user-defined TextList containers are supported.");
                foreach (var entry in Map(Required(value, "ResourceLists")).Children)
                {
                    string name = Scalar(entry.Key);
                    if (lists.ContainsKey(name)) throw new InvalidDataException("Duplicate Unified text-list name.");
                    lists.Add(name, Map(entry.Value));
                }
            }
            if (!lists.ContainsKey(Name)) throw new NotSupportedException("Selected Unified list was not exported. Siemens skips lists containing formatted text.");
            if (!Bundle.NativeListNames.Contains(Name, StringComparer.Ordinal) || Bundle.NativeListNames.Distinct(StringComparer.Ordinal).Count() != Bundle.NativeListNames.Length ||
                lists.Keys.Any(n => !Bundle.NativeListNames.Contains(n, StringComparer.Ordinal))) throw new InvalidDataException("Native list identity does not match the YAML export.");
            foreach (var list in lists.Values)
                foreach (var entry in Map(Required(list, "Entries")).Children.Values)
                {
                    string reference = Scalar(Required(Map(entry), "Text"));
                    referenceCounts[reference] = referenceCounts.TryGetValue(reference, out int count) ? count + 1 : 1;
                }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in Map(Required(lists[Name], "Entries")).Children)
            {
                var entry = Map(pair.Value);
                string type = Scalar(Required(entry, "Type"));
                string flag = Scalar(Required(entry, "IsDefaultEntry"));
                if (flag != "true" && flag != "false") throw new InvalidDataException("Unsupported default-entry flag.");
                string key = flag == "true" ? "Default" : type == "SingleValue" ? ParseNumber(Scalar(Required(entry, "FromValue"))) : "entry:" + Scalar(pair.Key);
                if (!keys.Add(key)) throw new InvalidDataException("Ambiguous Unified text-list entry selector.");
                string reference = Scalar(Required(entry, "Text"));
                var matches = new List<Tuple<YamlMappingNode, string>>();
                foreach (var lib in Map(Required(library, "TextLibraries")).Children)
                {
                    string prefix = Scalar(lib.Key) + ".";
                    if (reference.StartsWith(prefix, StringComparison.Ordinal) && Map(Required(Map(lib.Value), "Entries")).Children.ContainsKey(new YamlScalarNode(reference.Substring(prefix.Length))))
                        matches.Add(Tuple.Create(Map(lib.Value), reference.Substring(prefix.Length)));
                }
                if (matches.Count != 1) throw new InvalidDataException("Text reference is missing or ambiguous: " + reference);
                var textLibrary = matches[0].Item1;
                if (Scalar(Required(textLibrary, "Type")) != "Text") throw new InvalidDataException("Unsupported Unified text-library type.");
                var languages = Sequence(Required(textLibrary, "Languages")).Children.Select(Scalar).ToArray();
                if (languages.Length == 0 || languages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != languages.Length)
                    throw new InvalidDataException("Missing or duplicate Unified language slots.");
                var textEntry = Map(Required(Map(Required(textLibrary, "Entries")), matches[0].Item2));
                var texts = Sequence(Required(textEntry, "Text")).Children;
                if (texts.Count != languages.Length) throw new InvalidDataException("Text slots do not match the ordered language list.");
                for (int i = 0; i < languages.Length; i++)
                {
                    var text = texts[i] as YamlScalarNode ?? throw new InvalidDataException("Expected a plain translation scalar.");
                    bool single = type == "SingleValue" && ParseNumber(Scalar(Required(entry, "FromValue"))) == ParseNumber(Scalar(Required(entry, "ToValue")));
                    rows.Add(new Row { Key = key, Culture = languages[i], Node = text, Reference = reference,
                        Editable = single && referenceCounts[reference] == 1 && text.Style != ScalarStyle.Literal && text.Style != ScalarStyle.Folded });
                }
            }
        }

        public TextValue[] Read() => rows.Select(r => new TextValue { Entry = r.Key, Culture = r.Culture, Text = r.Node.Value ?? "", Editable = r.Editable }).ToArray();
        public string Serialize() => JsonSerializer.Serialize(Bundle, JsonDefaults.Options);

        public ITextListDocument Edit(TextEdit[] edits)
        {
            if (edits == null || edits.Length == 0 || edits.Length > 1000) throw new ArgumentException("Supply between 1 and 1000 text edits.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patches = new List<Tuple<int, int, string>>();
            foreach (var edit in edits)
            {
                if (edit == null || edit.Entry == null || edit.Culture == null || edit.ExpectedText == null || edit.NewText == null)
                    throw new ArgumentException("All edit fields are required.");
                if (!seen.Add(edit.Entry + "\0" + edit.Culture)) throw new ArgumentException("Duplicate text edit.");
                var row = rows.SingleOrDefault(r => r.Key == edit.Entry && string.Equals(r.Culture, edit.Culture, StringComparison.OrdinalIgnoreCase));
                if (row == null || !row.Editable) throw new InvalidOperationException("Unified entry/culture is missing, shared by other entries, a range, or uses unsupported scalar formatting.");
                if ((row.Node.Value ?? "") != edit.ExpectedText) throw new InvalidOperationException("Expected Unified text does not match.");
                if (edit.NewText.Length > 65535) throw new ArgumentException("Replacement text is too long.");
                System.Xml.XmlConvert.VerifyXmlChars(edit.NewText);
                int start = checked((int)row.Node.Start.Index), length = checked((int)(row.Node.End.Index - row.Node.Start.Index));
                // JSON double-quoted strings are valid YAML flow scalars, including escaped newlines.
                patches.Add(Tuple.Create(start, length, JsonSerializer.Serialize(edit.NewText)));
            }
            string changed = Bundle.TextLibrary;
            foreach (var patch in patches.OrderByDescending(p => p.Item1)) changed = changed.Remove(patch.Item1, patch.Item2).Insert(patch.Item1, patch.Item3);
            var candidate = new UnifiedTextListBundle { ListName = Name, Configuration = Bundle.Configuration, TextLibrary = changed, NativeListNames = Bundle.NativeListNames.ToArray() };
            var parsed = new UnifiedTextListDocument(JsonSerializer.Serialize(candidate, JsonDefaults.Options));
            // Validate escaping and spans before a candidate can reach the native importer.
            foreach (var edit in edits)
                if (parsed.Read().Single(r => r.Entry == edit.Entry && string.Equals(r.Culture, edit.Culture, StringComparison.OrdinalIgnoreCase)).Text != edit.NewText)
                    throw new InvalidOperationException("YAML scalar patch did not round-trip exactly.");
            return parsed;
        }

        private static YamlMappingNode ParseYaml(string text)
        {
            if (text == null || text.Length > 16 * 1024 * 1024 || text.Split('\n')[0].Trim('\uFEFF', '\r', ' ', '\t') != "#Version: 2.0")
                throw new InvalidDataException("Expected Siemens Unified YAML #Version: 2.0 and both native export files.");
            var stream = new YamlStream();
            using (var reader = new StringReader(text)) stream.Load(reader);
            if (stream.Documents.Count != 1) throw new InvalidDataException("Only one YAML document per export file is supported.");
            var root = Map(stream.Documents[0].RootNode);
            int count = 0;
            Validate(root, 0, ref count);
            return root;
        }
        private static void Validate(YamlNode node, int depth, ref int count)
        {
            if (depth > 64 || ++count > 100000 || !node.Anchor.IsEmpty || !node.Tag.IsEmpty) throw new InvalidDataException("YAML anchors, custom tags or excessive nesting are unsupported.");
            if (node is YamlMappingNode mapping)
                foreach (var pair in mapping.Children) { Scalar(pair.Key); Validate(pair.Key, depth + 1, ref count); Validate(pair.Value, depth + 1, ref count); }
            else if (node is YamlSequenceNode sequence) foreach (var child in sequence.Children) Validate(child, depth + 1, ref count);
            else if (!(node is YamlScalarNode)) throw new InvalidDataException("Unsupported YAML node.");
        }
        private static object Canonical(YamlNode node)
        {
            if (node is YamlScalarNode scalar) return new { scalar = scalar.Value };
            if (node is YamlSequenceNode sequence) return sequence.Children.Select(Canonical).ToArray();
            return Map(node).Children.OrderBy(p => Scalar(p.Key), StringComparer.Ordinal).Select(p => new { key = Scalar(p.Key), value = Canonical(p.Value) }).ToArray();
        }
        private static string ParseNumber(string value) => long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        private static YamlMappingNode Map(YamlNode node) => node as YamlMappingNode ?? throw new InvalidDataException("Expected a YAML mapping.");
        private static YamlSequenceNode Sequence(YamlNode node) => node as YamlSequenceNode ?? throw new InvalidDataException("Expected a YAML sequence.");
        private static string Scalar(YamlNode node) => (node as YamlScalarNode)?.Value ?? throw new InvalidDataException("Expected a YAML string scalar.");
        private static YamlNode Required(YamlMappingNode node, string name) => node.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : throw new InvalidDataException("Missing YAML member: " + name);
        private sealed class Row
        {
            public string Key = "", Culture = "", Reference = "";
            public YamlScalarNode Node = null!;
            public bool Editable;
        }
    }
}
