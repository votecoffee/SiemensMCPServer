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
    internal sealed class ClassicXmlField
    {
        public string XPath { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Editable { get; set; }
        public string? ReadOnlyReason { get; set; }
    }
    internal sealed partial class ClassicXmlDocument
    {
        private readonly XDocument document;
        private readonly XElement resource;
        private readonly bool preserveResourceIdentity;
        public string Name { get; }
        public string NativeType => resource.Name.LocalName;
        public ClassicXmlDocument(string xml, bool preserveResourceIdentity = true)
        {
            this.preserveResourceIdentity = preserveResourceIdentity;
            document = TransferFiles.Xml(xml);
            if (document.Root?.Name != "Document") throw new ArgumentException("Expected a native classic HMI Document.");
            var resources = document.Root.Elements().Where(e => e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)).ToArray();
            if (resources.Length != 1) throw new ArgumentException("Exactly one classic HMI resource is required.");
            resource = resources[0];
            Name = resource.Element("AttributeList")?.Element("Name")?.Value ?? "";
        }
        public object Catalog(bool resourceEditable)
        {
            const int limit = 1000;
            var fields = ScalarNodes().Take(limit + 1).Select(node => {
                string? reason = ReadOnlyReason(node);
                return new ClassicXmlField { XPath = Path(node), Value = node is XAttribute a ? a.Value : ((XElement)node).Value,
                    Editable = resourceEditable && reason == null, ReadOnlyReason = resourceEditable ? reason : "This native resource is inspection-only." };
            }).ToArray();
            return new { fields = fields.Take(limit).ToArray(), truncated = fields.Length > limit, limit,
                scope = "Existing XML scalars. Editable means eligible for a preview; native semantic acceptance remains pending. Inspect the full XML for fields beyond this bounded catalog." };
        }
        private IEnumerable<XObject> ScalarNodes()
        {
            foreach (var element in resource.DescendantsAndSelf())
            {
                if (!element.HasElements && element.Nodes().All(n => n is XText)) yield return element;
                foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) yield return attribute;
            }
        }
        private string? ReadOnlyReason(XObject node)
        {
            if (node is XAttribute attribute && (attribute.IsNamespaceDeclaration || attribute.Name.LocalName == "ID" || attribute.Name.LocalName == "UId" || attribute.Name.LocalName == "CompositionName"))
                return "Export identity, namespace and composition metadata cannot be changed by scalar edits.";
            if (preserveResourceIdentity && node == resource.Element("AttributeList")?.Element("Name")) return "Resource identity requires an explicit create/rename operation.";
            if (resource.Name.LocalName == "Hmi.Screen.ScreenSlidein" && node == resource.Element("AttributeList")?.Element("SlideinType"))
                return "The slide-in edge identifies this native resource and cannot be changed by scalar edits.";
            if (node is XElement element && (element == resource || element.Name.LocalName == "ObjectList" || element.Name.LocalName == "AttributeList" || element.Name.LocalName == "LinkList"))
                return "Structural XML containers require a reviewed native resource import.";
            return null;
        }
        private static string Path(XObject node)
        {
            XElement owner = node is XAttribute attribute ? attribute.Parent! : (XElement)node;
            var ancestors = owner.AncestorsAndSelf().Reverse().ToArray();
            if (ancestors.Length > 128) throw new ArgumentException("XML scalar path exceeds the 128-level inspection limit.");
            string result = "/" + string.Join("/", ancestors.Select(e => Step(e.Name) + "[" + (e.ElementsBeforeSelf(e.Name).Count() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"));
            return node is XAttribute attr ? result + "/@" + Step(attr.Name) : result;
        }
        private static string Step(XName name) => name.NamespaceName.Length == 0 ? name.LocalName
            : "*[local-name()=" + Literal(name.LocalName) + " and namespace-uri()=" + Literal(name.NamespaceName) + "]";
        private static string Literal(string value) => !value.Contains("'") ? "'" + value + "'" : !value.Contains("\"") ? "\"" + value + "\"" : "concat('" + value.Replace("'", "',\"'\",'") + "')";
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
                string? reason = ReadOnlyReason(node);
                if (reason != null) throw new ArgumentException(reason);
                string old;
                if (node is XAttribute attr)
                {
                    old = attr.Value;
                }
                else if (node is XElement element && !element.HasElements && !element.Nodes().Any(n => !(n is XText)))
                {
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
