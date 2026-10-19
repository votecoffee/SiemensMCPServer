using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    internal static class NativeXmlIds
    {
        // Native Openness links use #ID; retain bare-ID support for existing adapters.
        internal static string TargetKey(string target) => target.StartsWith("#", StringComparison.Ordinal) ? target.Substring(1) : target;
        internal static string Retarget(string target, string id) => (target.StartsWith("#", StringComparison.Ordinal) ? "#" : "") + id;
        private static XAttribute[] Ids(IEnumerable<XElement> elements)
        {
            var ids = elements.Attributes("ID").ToArray();
            if (ids.Any(a => string.IsNullOrWhiteSpace(a.Value) || a.Value.StartsWith("#", StringComparison.Ordinal) || a.Value.StartsWith("@", StringComparison.Ordinal))
                || ids.GroupBy(a => a.Value, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate or invalid native XML object IDs.");
            return ids;
        }
        internal static void Normalize(XDocument document)
        {
            var ids = Ids(document.Descendants());
            var map = ids.Select((a, i) => new { a.Value, Canonical = "#object-" + i.ToString(CultureInfo.InvariantCulture) }).ToDictionary(a => a.Value, a => a.Canonical, StringComparer.Ordinal);
            foreach (var link in document.Descendants().Attributes("TargetID"))
            {
                if (map.TryGetValue(TargetKey(link.Value), out var canonical)) link.Value = canonical;
                // Keep unresolved literals in a separate domain so they cannot mimic canonical targets.
                else if (link.Value != "@OpenLink") link.Value = "!unresolved:" + link.Value;
            }
            ids.Remove();
        }
        internal static XElement Clone(XElement template, XDocument document)
        {
            if (template.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName == "UId")) throw new NotSupportedException("This clone contains an unsupported UId reference format; use a reviewed native resource import.");
            var used = new HashSet<string>(Ids(document.Descendants()).Select(a => a.Value), StringComparer.Ordinal);
            var originalIds = new HashSet<string>(used, StringComparer.Ordinal);
            var clone = new XElement(template); var map = new Dictionary<string, string>(StringComparer.Ordinal); int next = 1;
            foreach (var id in Ids(clone.DescendantsAndSelf()))
            {
                string value; do { value = (next++).ToString("X", CultureInfo.InvariantCulture); } while (!used.Add(value));
                if (map.ContainsKey(id.Value)) throw new ArgumentException("Duplicate IDs in clone template.");
                map.Add(id.Value, value); id.Value = value;
            }
            foreach (var reference in clone.DescendantsAndSelf().Attributes("TargetID"))
            {
                string key = TargetKey(reference.Value);
                if (map.TryGetValue(key, out var value)) reference.Value = Retarget(reference.Value, value);
                else if (reference.Value != "@OpenLink" && !originalIds.Contains(key)) throw new ArgumentException("Clone template contains an unresolved native ID reference.");
            }
            return clone;
        }
        internal static void VerifyReferences(XElement node, XDocument document)
        {
            var known = new HashSet<string>(Ids(document.Descendants()).Select(a => a.Value).Concat(Ids(node.DescendantsAndSelf()).Select(a => a.Value)), StringComparer.Ordinal);
            if (node.DescendantsAndSelf().Attributes("TargetID").Any(a => a.Value != "@OpenLink" && !known.Contains(TargetKey(a.Value)))) throw new ArgumentException("Edited clone contains an unresolved native ID reference.");
        }
    }
}
