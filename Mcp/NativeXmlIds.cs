using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    internal static class NativeXmlIds
    {
        internal static void Normalize(XDocument document)
        {
            var ids = document.Descendants().Attributes("ID").ToArray();
            if (ids.GroupBy(a => a.Value, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate native XML object IDs.");
            var map = ids.Select((a, i) => new { a.Value, Canonical = "#object-" + i.ToString(CultureInfo.InvariantCulture) }).ToDictionary(a => a.Value, a => a.Canonical, StringComparer.Ordinal);
            foreach (var link in document.Descendants().Attributes("TargetID")) if (map.TryGetValue(link.Value, out var canonical)) link.Value = canonical;
            ids.Remove();
        }
        internal static XElement Clone(XElement template, XDocument document)
        {
            if (template.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName == "UId")) throw new NotSupportedException("This clone contains an unsupported UId reference format; use a reviewed native resource import.");
            var used = new HashSet<string>(document.Descendants().Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
            var clone = new XElement(template); var map = new Dictionary<string, string>(StringComparer.Ordinal); int next = 1;
            foreach (var id in clone.DescendantsAndSelf().Attributes("ID"))
            {
                string value; do { value = (next++).ToString("X", CultureInfo.InvariantCulture); } while (!used.Add(value));
                if (map.ContainsKey(id.Value)) throw new ArgumentException("Duplicate IDs in clone template.");
                map.Add(id.Value, value); id.Value = value;
            }
            foreach (var reference in clone.DescendantsAndSelf().Attributes("TargetID"))
            {
                if (map.TryGetValue(reference.Value, out var value)) reference.Value = value;
                else if (reference.Value != "@OpenLink" && !used.Contains(reference.Value)) throw new ArgumentException("Clone template contains an unresolved native ID reference.");
            }
            return clone;
        }
        internal static void VerifyReferences(XElement node, XDocument document)
        {
            var known = new HashSet<string>(document.Descendants().Attributes("ID").Select(a => a.Value).Concat(node.DescendantsAndSelf().Attributes("ID").Select(a => a.Value)), StringComparer.Ordinal);
            if (node.DescendantsAndSelf().Attributes("TargetID").Any(a => a.Value != "@OpenLink" && !known.Contains(a.Value))) throw new ArgumentException("Edited clone contains an unresolved native ID reference.");
        }
    }
}
