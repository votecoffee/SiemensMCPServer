using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SiemensTiaOpenness.Mcp
{
    public sealed class ClassicXmlEdit
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedValue { get; set; } = "";
        [JsonRequired] public string NewValue { get; set; } = "";
    }
    internal sealed class ClassicXmlDocument
    {
        private readonly XDocument document;
        private readonly XElement resource;
        public string Name { get; }
        public string NativeType => resource.Name.LocalName;
        public ClassicXmlDocument(string xml)
        {
            document = TransferFiles.Xml(xml);
            if (document.Root?.Name != "Document") throw new ArgumentException("Expected a native classic HMI Document.");
            var resources = document.Root.Elements().Where(e => e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)).ToArray();
            if (resources.Length != 1) throw new ArgumentException("Exactly one classic HMI resource is required.");
            resource = resources[0];
            Name = resource.Element("AttributeList")?.Element("Name")?.Value ?? "";
        }
        public string Edit(ClassicXmlEdit[] edits)
        {
            if (edits == null || edits.Length < 1 || edits.Length > 1000) throw new ArgumentException("Supply 1 to 1000 existing scalar XML edits.");
            var touched = new HashSet<XObject>();
            // Resolve and verify every selector against the original before changing anything.
            var changes = new List<KeyValuePair<XObject, string>>();
            foreach (var edit in edits)
            {
                if (edit == null || edit.XPath == null || edit.ExpectedValue == null || edit.NewValue == null || edit.NewValue.Length > 4 * 1024 * 1024)
                    throw new ArgumentException("All edit fields are required and values must be bounded.");
                if (Regex.IsMatch(edit.XPath, @"@\s*(ID|UId)\b", RegexOptions.CultureInvariant)) throw new ArgumentException("Use structural/name selectors, not volatile export IDs.");
                var matches = (document.XPathEvaluate(edit.XPath) as IEnumerable)?.Cast<object>().ToArray() ?? throw new ArgumentException("XPath must select one existing element or attribute.");
                if (matches.Length != 1 || !(matches[0] is XObject node)) throw new ArgumentException("XPath must select exactly one existing scalar.");
                XElement? owner = node is XAttribute attribute ? attribute.Parent : node as XElement;
                if (owner == null || !owner.AncestorsAndSelf().Contains(resource)) throw new ArgumentException("Edits must stay inside the selected HMI resource.");
                if (!touched.Add(node)) throw new ArgumentException("Duplicate XML edit target.");
                string old;
                if (node is XAttribute attr)
                {
                    if (attr.IsNamespaceDeclaration || attr.Name.LocalName == "ID" || attr.Name.LocalName == "UId") throw new ArgumentException("Export IDs and namespace declarations cannot be changed.");
                    old = attr.Value;
                }
                else if (node is XElement element && !element.HasElements && !element.Nodes().Any(n => !(n is XText)))
                {
                    if (element == resource.Element("AttributeList")?.Element("Name")) throw new ArgumentException("This workflow preserves the resource identity. Use an explicit create/rename operation instead.");
                    old = element.Value;
                }
                else throw new ArgumentException("Only existing scalar elements/attributes can be edited. Complex XML requires a reviewed native resource import.");
                if (old != edit.ExpectedValue) throw new InvalidOperationException("Expected value differs at " + edit.XPath);
                changes.Add(new KeyValuePair<XObject, string>(node, edit.NewValue));
            }
            if (changes.All(c => (c.Key is XAttribute a ? a.Value : ((XElement)c.Key).Value) == c.Value)) throw new ArgumentException("No changes in preview.");
            foreach (var change in changes)
                if (change.Key is XAttribute attribute) attribute.Value = change.Value;
                else ((XElement)change.Key).Value = change.Value;
            return document.ToString(SaveOptions.DisableFormatting);
        }
    }
}
