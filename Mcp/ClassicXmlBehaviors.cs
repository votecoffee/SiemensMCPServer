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
        private static bool Behavior(XElement node) => node.Parent?.Name == "ObjectList" && node.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)
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
            var operations = new List<Tuple<ClassicBehaviorChange, XElement, XElement?>>();
            foreach (var change in changes)
            {
                if (change == null || change.Action == null || change.XPath == null || change.ExpectedFingerprint == null || change.DestinationXPath == null || change.Edits == null) throw new ArgumentException("Behavior change fields cannot be null.");
                var selected = Select(change.XPath);
                if (!Behavior(selected)) throw new ArgumentException("Select a native event/animation/binding object from behaviorCatalog.");
                if (ObjectFingerprint(selected) != change.ExpectedFingerprint) throw new InvalidOperationException("Behavior fingerprint differs; read the resource again.");
                if (operations.Any(o => o.Item2.AncestorsAndSelf().Contains(selected) || selected.AncestorsAndSelf().Contains(o.Item2))) throw new ArgumentException("Overlapping behavior changes require separate previews.");
                XElement? destination = null;
                if (change.Action == "clone")
                {
                    destination = Select(change.DestinationXPath);
                    if (!destination.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)) throw new ArgumentException("Clone destination must be a native Hmi.* owner inside this resource.");
                }
                else if (change.Action != "remove" || change.Edits.Length != 0 || change.DestinationXPath.Length != 0) throw new ArgumentException("Use clone with an explicit destination/optional edits, or remove without destination/edits.");
                operations.Add(Tuple.Create(change, selected, destination));
            }
            foreach (var operation in operations)
            {
                if (operation.Item3 != null && operations.Any(o => o.Item1.Action == "remove" && operation.Item3.AncestorsAndSelf().Contains(o.Item2))) throw new ArgumentException("Clone destination is removed by this preview.");
                var selectedIds = new HashSet<string>(operation.Item2.DescendantsAndSelf().Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
                if (operation.Item1.Action == "remove" && document.Descendants().Attributes("TargetID").Any(a => selectedIds.Contains(a.Value) && !a.Parent!.AncestorsAndSelf().Contains(operation.Item2)))
                    throw new InvalidOperationException("Another exported object references this behavior; update that reference before removing it.");
            }
            foreach (var operation in operations)
            {
                if (operation.Item1.Action == "remove") { operation.Item2.Remove(); continue; }
                XElement clone = NativeXmlIds.Clone(operation.Item2, document);
                if (operation.Item1.Edits.Length != 0)
                {
                    // Edit paths are relative to this Document-wrapped clone, never the original screen.
                    string xml = new ClassicXmlDocument(new XDocument(new XElement("Document", clone)).ToString(SaveOptions.DisableFormatting), preserveResourceIdentity: false).Edit(operation.Item1.Edits);
                    clone = TransferFiles.Xml(xml).Root!.Elements().Single();
                }
                var list = operation.Item3!.Element("ObjectList");
                NativeXmlIds.VerifyReferences(clone, document);
                if (list == null) { list = new XElement("ObjectList"); operation.Item3.Add(list); }
                list.Add(clone);
            }
            return document.ToString(SaveOptions.DisableFormatting);
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
            selected.DescendantsAndSelf().Where(e => e.HasElements).Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
            return TextListDocument.Hash(selected.ToString(SaveOptions.DisableFormatting));
        }
    }
}
