using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SiemensMCPServer.Mcp
{
    public sealed class ClassicBehaviorChange
    {
        [JsonRequired] public string Action { get; set; } = "";
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedFingerprint { get; set; } = "";
        public string DestinationXPath { get; set; } = "";
        public ClassicXmlEdit[] Edits { get; set; } = Array.Empty<ClassicXmlEdit>();
    }
    internal sealed partial class ClassicXmlDocument
    {
        private static readonly string[] BehaviorCompositions = { "Animations", "Events", "Dynamizations", "EventHandlers" };
        private static bool Behavior(XElement node) => node.Name.NamespaceName.Length == 0 && node.Parent?.Name == "ObjectList" && node.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)
            && node.AncestorsAndSelf().Any(e => BehaviorCompositions.Contains((string?)e.Attribute("CompositionName"), StringComparer.Ordinal));
        public object BehaviorCatalog(bool editable)
        {
            var nodes = resource.Descendants().Where(Behavior).Take(101).ToArray();
            return new { objects = nodes.Take(100).Select(n => {
                string xml = n.ToString(SaveOptions.DisableFormatting);
                return new { xPath = Path(n), nativeType = n.Name.LocalName, composition = (string?)n.Attribute("CompositionName"), fingerprint = ObjectFingerprint(n),
                    cloneOrRemove = editable, xml = xml.Length <= 65536 ? xml : null, xmlTruncated = xml.Length > 65536 };
            }).ToArray(), truncated = nodes.Length > 100, limit = 100,
                scope = "Native exported event/animation/binding objects and their Hmi.* children. Clone an existing prototype into an explicit native owner or remove an exact object. Existing fields use scalar edits. Unsupported screen objects absent from the native export cannot be edited here." };
        }
        public string ChangeBehaviors(ClassicBehaviorChange[] changes)
        {
            if (changes == null || changes.Length == 0 || changes.Length > 100) throw new ArgumentException("Supply 1 to 100 native behavior changes.");
            return ChangeReviewedBehaviors(changes.Select(c => c == null ? null! : new ClassicBehaviorOperation { Action = c.Action, XPath = c.XPath,
                ExpectedFingerprint = c.ExpectedFingerprint, DestinationXPath = c.DestinationXPath, Edits = c.Edits }).ToArray());
        }
        private XElement Select(string xpath)
        {
            var matches = (document.XPathEvaluate(xpath) as IEnumerable)?.Cast<object>().ToArray();
            if (matches?.Length != 1 || !(matches[0] is XElement node) || !node.AncestorsAndSelf().Contains(resource)) throw new ArgumentException("XPath must select exactly one native object inside this resource.");
            return node;
        }
        private string ObjectFingerprint(XElement node)
        {
            var canonical = new XDocument(document); NativeXmlIds.Normalize(canonical);
            var selected = canonical.XPathSelectElement(Path(node))!;
            selected.DescendantsAndSelf().Where(e => e.HasElements && !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Text"))
                .Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
            return TextListDocument.Hash(selected.ToString(SaveOptions.DisableFormatting));
        }
    }
}
