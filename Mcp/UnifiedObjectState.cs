using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SiemensTiaOpenness.Mcp
{
    public sealed class UnifiedEdit
    {
        [JsonRequired] public string Property { get; set; } = "";
        [JsonRequired] public string Culture { get; set; } = "";
        [JsonRequired] public string ExpectedValue { get; set; } = "";
        [JsonRequired] public string NewValue { get; set; } = "";
    }

    internal sealed class UnifiedScalar
    {
        public string Property { get; set; } = "";
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Editable { get; set; }
    }

    internal sealed class UnifiedText
    {
        public string Property { get; set; } = "";
        public string Culture { get; set; } = "";
        public string NativeText { get; set; } = "";
        public string? Text { get; set; }
        public bool Editable { get; set; }
    }

    internal sealed class UnifiedObjectState
    {
        public string HmiId { get; set; } = "";
        public string ScreenPath { get; set; } = "";
        public string ItemPath { get; set; } = "";
        public string NativeType { get; set; } = "";
        public UnifiedScalar[] Scalars { get; set; } = Array.Empty<UnifiedScalar>();
        public UnifiedText[] Texts { get; set; } = Array.Empty<UnifiedText>();
        public string[] DynamizedProperties { get; set; } = Array.Empty<string>();

        [JsonIgnore] public string Fingerprint => TextListDocument.Hash(JsonSerializer.Serialize(new {
            HmiId, ScreenPath, ItemPath, NativeType,
            scalars = Scalars.OrderBy(s => s.Property, StringComparer.Ordinal),
            texts = Texts.OrderBy(t => t.Property, StringComparer.Ordinal).ThenBy(t => t.Culture, StringComparer.Ordinal).Select(t => new {
                t.Property, t.Culture, nativeText = UnifiedTextCodec.Canonical(t.NativeText), t.Text, t.Editable }),
            dynamizedProperties = DynamizedProperties.OrderBy(p => p, StringComparer.Ordinal)
        }, JsonDefaults.Options));

        public UnifiedObjectState Edit(UnifiedEdit[] edits)
        {
            if (edits == null || edits.Length == 0 || edits.Length > 1000) throw new ArgumentException("Supply between 1 and 1000 Unified edits.");
            var candidate = JsonSerializer.Deserialize<UnifiedObjectState>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edit in edits)
            {
                if (edit == null || edit.Property == null || edit.Culture == null || edit.ExpectedValue == null || edit.NewValue == null)
                    throw new ArgumentException("All Unified edit fields are required.");
                if (!seen.Add(edit.Property + "\0" + edit.Culture.ToLowerInvariant())) throw new ArgumentException("Duplicate Unified edit.");
                if (IsDynamic(edit.Property)) throw new InvalidOperationException("Property has a dynamization and cannot be edited by this workflow: " + edit.Property);
                if (edit.Culture.Length == 0)
                {
                    var scalar = candidate.Scalars.SingleOrDefault(s => s.Property == edit.Property);
                    if (scalar == null || !scalar.Editable || !UnifiedScalarCodec.Allowed.Contains(edit.Property))
                        throw new InvalidOperationException("Property is not an editable supported scalar: " + edit.Property);
                    if (scalar.Value != edit.ExpectedValue) throw new InvalidOperationException("Expected value does not match " + edit.Property);
                    scalar.Value = UnifiedScalarCodec.Format(UnifiedScalarCodec.Parse(edit.NewValue, scalar.Type));
                }
                else
                {
                    var text = candidate.Texts.SingleOrDefault(t => t.Property == edit.Property && string.Equals(t.Culture, edit.Culture, StringComparison.OrdinalIgnoreCase));
                    if (text == null || !text.Editable || !UnifiedTextCodec.Allowed.Contains(edit.Property))
                        throw new InvalidOperationException("Property/culture is missing, dynamized, or contains unsupported formatting.");
                    if (text.Text != edit.ExpectedValue) throw new InvalidOperationException("Expected text does not match " + edit.Property + "/" + edit.Culture);
                    text.NativeText = UnifiedTextCodec.Replace(text.NativeText, edit.NewValue);
                    text.Text = edit.NewValue;
                }
            }
            return candidate;
        }

        internal bool IsDynamic(string property) => DynamizedProperties.Any(p => p == property || p.StartsWith(property + ".", StringComparison.Ordinal) || property.StartsWith(p + ".", StringComparison.Ordinal));

        internal void RequireAllowedCandidate(UnifiedObjectState candidate)
        {
            if (candidate.Scalars.Length != Scalars.Length || candidate.Texts.Length != Texts.Length) throw new InvalidOperationException("Extension cannot change Unified field membership.");
            var edits = new List<UnifiedEdit>();
            foreach (var scalar in Scalars)
            {
                var changed = candidate.Scalars.Single(s => s.Property == scalar.Property);
                if (changed.Value != scalar.Value) edits.Add(new UnifiedEdit { Property = scalar.Property, Culture = "", ExpectedValue = scalar.Value, NewValue = changed.Value });
            }
            foreach (var text in Texts)
            {
                var changed = candidate.Texts.Single(t => t.Property == text.Property && t.Culture == text.Culture);
                if (UnifiedTextCodec.Canonical(changed.NativeText) != UnifiedTextCodec.Canonical(text.NativeText))
                    edits.Add(new UnifiedEdit { Property = text.Property, Culture = text.Culture, ExpectedValue = text.Text!, NewValue = changed.Text! });
            }
            string expected = edits.Count == 0 ? Fingerprint : Edit(edits.ToArray()).Fingerprint;
            if (candidate.Fingerprint != expected) throw new InvalidOperationException("Extension candidate changes protected Unified identity, metadata or unsupported content.");
        }
    }

    internal static class UnifiedScalarCodec
    {
        internal static readonly HashSet<string> Allowed = new HashSet<string>(new[] { "Left", "Top", "Width", "Height", "Visible", "Enabled", "BackColor", "ForeColor" }, StringComparer.Ordinal);
        internal static readonly HashSet<string> Types = new HashSet<string>(new[] { "System.Int32", "System.UInt32", "System.Boolean", "System.Drawing.Color" }, StringComparer.Ordinal);
        public static object Parse(string text, string type)
        {
            switch (type)
            {
                case "System.Int32": return int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                case "System.UInt32": return uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
                case "System.Boolean":
                    if (text != "true" && text != "false") throw new ArgumentException("Boolean values must be true or false.");
                    return text == "true";
                case "System.Drawing.Color":
                    if (text.Length != 9 || text[0] != '#') throw new ArgumentException("Color must be #AARRGGBB.");
                    return Color.FromArgb(unchecked((int)uint.Parse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
                default: throw new NotSupportedException("Unsupported Unified scalar type: " + type);
            }
        }
        public static string Format(object value)
        {
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is Color) return "#" + unchecked((uint)((Color)value).ToArgb()).ToString("X8", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }
    }

    // Preserve a single text run and its formatting. Complex rich text stays inspection-only.
    internal static class UnifiedTextCodec
    {
        internal static readonly HashSet<string> Allowed = new HashSet<string>(new[] { "Text", "AlternateText", "ToolTipText", "DisplayName" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Tags = new HashSet<string>(new[] { "body", "p", "span", "b", "strong", "i", "em", "u", "font" }, StringComparer.Ordinal);

        public static bool TryRead(string native, out string? text)
        {
            text = null;
            if (!native.TrimStart().StartsWith("<", StringComparison.Ordinal)) { text = native; return true; }
            try
            {
                var root = Parse(native);
                if (root.Name.LocalName != "body" || root.DescendantsAndSelf().Any(e => !Tags.Contains(e.Name.LocalName))) return false;
                var runs = ContentRuns(root);
                if (runs.Length > 1) return false;
                if (runs.Length == 0 && root.DescendantsAndSelf().Count(e => e.Name.LocalName == "p") != 1) return false;
                text = runs.Length == 0 ? "" : runs[0].Value;
                return true;
            }
            catch (XmlException) { return false; }
        }

        public static string Replace(string native, string replacement)
        {
            if (replacement.Length > 65535) throw new ArgumentException("Replacement text is too long.");
            XmlConvert.VerifyXmlChars(replacement);
            string? ignored;
            if (!TryRead(native, out ignored)) throw new InvalidOperationException("Complex or dynamic rich text is inspection-only.");
            if (!native.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                // Unified V18+ rejects unformatted assignments, including initially empty captions.
                return new XElement("body", new XElement("p", replacement)).ToString(SaveOptions.DisableFormatting);
            }
            var root = Parse(native);
            var runs = ContentRuns(root);
            if (runs.Length == 1) runs[0].Value = replacement;
            else
            {
                var leaf = root.DescendantsAndSelf().Last(e => !e.HasElements);
                leaf.Add(new XText(replacement));
            }
            return root.ToString(SaveOptions.DisableFormatting);
        }

        public static string Canonical(string native)
        {
            if (!native.TrimStart().StartsWith("<", StringComparison.Ordinal)) return native;
            try
            {
                var root = Parse(native);
                foreach (var e in root.DescendantsAndSelf())
                {
                    var attributes = e.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)).ToArray();
                    e.RemoveAttributes(); e.Add(attributes);
                    if (!e.HasElements && e.Value.Length == 0) e.RemoveNodes();
                }
                return root.ToString(SaveOptions.DisableFormatting);
            }
            catch (XmlException) { return native; }
        }
        private static XText[] ContentRuns(XElement root) => root.DescendantNodes().OfType<XText>()
            .Where(t => !(t.Parent!.HasElements && string.IsNullOrWhiteSpace(t.Value))).ToArray();
        private static XElement Parse(string text)
        {
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                return XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
    }
}
