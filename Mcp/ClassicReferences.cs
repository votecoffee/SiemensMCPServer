using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicReferenceInspection
    {
        [JsonRequired] public string Xml { get; set; } = "";
        public string RootXPath { get; set; } = "";
        public string LinkType { get; set; } = "";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicReferenceSelection
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedFingerprint { get; set; } = "";
        [JsonRequired] public string ExpectedName { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicReferenceEdit
    {
        [JsonRequired] public ClassicReferenceSelection Selection { get; set; } = null!;
        [JsonRequired] public string NewName { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicReferenceRequest
    {
        [JsonRequired] public ClassicReferenceEdit[] Edits { get; set; } = Array.Empty<ClassicReferenceEdit>();
    }
    internal sealed partial class ClassicXmlDocument
    {
        private static readonly HashSet<string> EditableReferenceTypes = new HashSet<string>(new[] { "Tag", "ControllerTag", "Connection" }, StringComparer.Ordinal);
        private sealed class ReferenceRecord
        {
            public XElement Node = null!, Owner = null!;
            public XElement? Control, Target;
            public string XPath = "", Fingerprint = "";
            public string? Name, UnavailableReason;
            public bool Open => (string?)Node.Attribute("TargetID") == "@OpenLink";
        }
        private sealed class ReferenceState
        {
            public ReferenceRecord[] Links = Array.Empty<ReferenceRecord>();
            public Dictionary<string, XElement> Roots = new Dictionary<string, XElement>(StringComparer.Ordinal);
        }
        private static bool NativeReference(XElement e) => e.Parent?.Name == "LinkList" && e.Name.NamespaceName.Length == 0 && e.Attribute("TargetID") != null;
        private static bool NativeReferenceOwner(XElement e) => e.Name.NamespaceName.Length == 0 && e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal);
        private static bool ReferenceNameValid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096;
        private ReferenceState ReferencesState()
        {
            if (!NativeReferenceOwner(resource)) throw new ArgumentException("Expected an unnamespaced native classic HMI resource.");
            var elements = resource.DescendantsAndSelf().Take(50001).ToArray();
            if (Name.Length > 512 || elements.Length > 50000 || elements.Any(e => e.Ancestors().Take(65).Count() > 64))
                throw new ArgumentException("Reference inspection exceeds 512 resource-name characters, 50000 XML elements or 64 levels.");
            var links = elements.Where(NativeReference).ToArray();
            if (links.Length > 5000) throw new ArgumentException("Reference inspection is limited to 5000 exported links.");
            var canonical = new XDocument(document); NativeXmlIds.Normalize(canonical);
            foreach (var e in canonical.Descendants().Where(e => e.HasElements && !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Text")))
                e.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
            var copies = canonical.Root!.Elements().Single(NativeReferenceOwner).DescendantsAndSelf().Where(NativeReference).ToArray();
            var ids = document.Descendants().Attributes("ID").ToDictionary(a => a.Value, a => a.Parent!, StringComparer.Ordinal);
            string? invalidReference = null;
            try { NativeXmlIds.VerifyReferences(resource, document); }
            catch (ArgumentException) { invalidReference = "An exported native ID reference is unresolved; inspect or repair the native resource first."; }
            var state = new ReferenceState();
            foreach (var owner in elements.Where(NativeReferenceOwner))
            {
                string path = Path(owner);
                if (path.Length > 4096) throw new ArgumentException("Reference owner paths exceed 4096 characters.");
                state.Roots.Add(path, owner);
            }
            state.Links = links.Select((link, i) => {
                var names = link.Elements("Name").ToArray();
                bool simple = names.Length == 1 && !names[0].HasAttributes && !names[0].HasElements && names[0].Nodes().All(n => n is XText)
                    && ReferenceNameValid(names[0].Value) && link.Elements().Count() == 1 && link.Nodes().All(n => n is XElement || n is XText t && string.IsNullOrWhiteSpace(t.Value))
                    && link.Attributes().All(a => a.Name == "TargetID");
                var row = new ReferenceRecord { Node = link, XPath = Path(link), Name = names.Length == 1 && !names[0].HasElements && names[0].Value.Length <= 4096 ? names[0].Value : null,
                    Owner = link.Ancestors().First(NativeReferenceOwner), Control = link.Ancestors().FirstOrDefault(ScreenItem) };
                if (row.XPath.Length > 4096) throw new ArgumentException("Reference paths exceed 4096 characters.");
                ids.TryGetValue(NativeXmlIds.TargetKey((string)link.Attribute("TargetID")!), out row.Target);
                if (row.Target != null && Path(row.Target).Length > 4096) throw new ArgumentException("Reference target paths exceed 4096 characters.");
                row.UnavailableReason = invalidReference ?? (!row.Open ? "Native-ID links are inspection-only; this operation does not change object ownership or identity." :
                    !EditableReferenceTypes.Contains(link.Name.LocalName) ? "Only symbolic Tag, ControllerTag and Connection link roles are supported." :
                    !simple ? "A supported symbolic link requires one existing scalar Name and no additional metadata." : null);
                // Normalize volatile IDs once for the complete resource; retain authored Text whitespace.
                row.Fingerprint = TextListDocument.Hash(copies[i].ToString(SaveOptions.DisableFormatting));
                return row;
            }).ToArray();
            return state;
        }
        private static string? ReferenceObjectName(XElement? node)
        {
            var name = node == null ? null : ObjectNameField(node);
            return name != null && name.Value.Length <= 512 ? name.Value : null;
        }
        private static object ReferenceSummary(ReferenceRecord row) => new { xPath = row.XPath, linkType = row.Node.Name.LocalName, kind = row.Open ? "openLink" : "nativeId", name = row.Name,
            ownerXPath = Path(row.Owner), ownerType = row.Owner.Name.LocalName, ownerName = ReferenceObjectName(row.Owner),
            controlXPath = row.Control == null ? null : Path(row.Control), controlName = ReferenceObjectName(row.Control),
            targetXPath = row.Target == null ? null : Path(row.Target), targetType = row.Target?.Name.LocalName, targetName = ReferenceObjectName(row.Target),
            editable = row.UnavailableReason == null, readOnlyReason = row.UnavailableReason,
            selection = row.UnavailableReason == null ? new ClassicReferenceSelection { XPath = row.XPath, ExpectedFingerprint = row.Fingerprint, ExpectedName = row.Name! } : null };
        public object ReferenceCatalog(ClassicReferenceInspection request)
        {
            if (request.RootXPath == null || request.RootXPath.Length > 4096 || request.LinkType == null || request.LinkType.Length > 128 || request.Offset < 0 || request.Offset > 5000 || request.MaxItems < 1 || request.MaxItems > 200)
                throw new ArgumentException("Use an exact rootXPath, linkType up to 128 characters, offset 0..5000 and maxItems 1..200.");
            var state = ReferencesState(); XElement? root = null;
            if (request.RootXPath.Length != 0 && !state.Roots.TryGetValue(request.RootXPath, out root)) throw new ArgumentException("rootXPath must be an exact native owner/control path from this resource.");
            var filtered = state.Links.Where(r => (root == null || r.Node.Ancestors().Contains(root)) && (request.LinkType.Length == 0 || r.Node.Name.LocalName == request.LinkType)).ToArray();
            var rows = new List<object>(); int next = request.Offset, characters = 0; bool limited = false;
            foreach (var row in filtered.Skip(request.Offset).Take(request.MaxItems))
            {
                var summary = ReferenceSummary(row); int cost = JsonSerializer.Serialize(summary, JsonDefaults.Options).Length;
                if (characters + cost > 258048) { limited = true; break; }
                rows.Add(summary); characters += cost; next++;
            }
            return new { Name, NativeType, resourceXPath = Path(resource), references = rows, matchedReferences = filtered.Length, offset = request.Offset, nextOffset = next,
                hasMore = next < filtered.Length, payloadLimited = limited,
                scope = "Exported direct LinkList links only. Symbolic Tag, ControllerTag and Connection Names may be changed exactly; native-ID ownership/type links stay inspection-only. No script/computed-name analysis, target lookup, type checking or runtime evaluation." };
        }
        public object RebindReferences(ClassicReferenceRequest request)
        {
            if (request?.Edits == null || request.Edits.Length < 1 || request.Edits.Length > 200) throw new ArgumentException("Supply 1 to 200 exact symbolic reference edits.");
            var candidate = new ClassicXmlDocument(document.ToString(SaveOptions.DisableFormatting)); var state = candidate.ReferencesState();
            var operations = new List<Tuple<ReferenceRecord, string>>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edit in request.Edits)
            {
                var selection = edit?.Selection;
                if (selection == null || selection.XPath == null || selection.ExpectedFingerprint == null || selection.ExpectedName == null || !ReferenceNameValid(edit!.NewName))
                    throw new ArgumentException("Each reference edit requires its exact returned selection and a nonblank newName of 1..4096 characters.");
                XmlConvert.VerifyXmlChars(edit.NewName);
                var row = state.Links.SingleOrDefault(r => r.XPath == selection.XPath) ?? throw new ArgumentException("Select an exact link path from inspect_references.");
                if (row.Fingerprint != selection.ExpectedFingerprint || row.Name != selection.ExpectedName) throw new InvalidOperationException("Reference fingerprint/name changed; inspect the resource again.");
                if (row.UnavailableReason != null) throw new ArgumentException(row.UnavailableReason);
                if (!seen.Add(row.XPath)) throw new ArgumentException("Duplicate reference edit target.");
                if (edit.NewName == row.Name) throw new ArgumentException("Reference edit has no change.");
                operations.Add(Tuple.Create(row, edit.NewName));
            }
            foreach (var operation in operations) operation.Item1.Node.Element("Name")!.Value = operation.Item2;
            NativeXmlIds.VerifyReferences(candidate.resource, candidate.document);
            string xml = candidate.document.ToString(SaveOptions.DisableFormatting);
            if (xml.Length > 16 * 1024 * 1024) throw new ArgumentException("Candidate XML exceeds 16 MiB.");
            return new { candidateXml = xml, changes = new { edits = operations.Select(o => new { xPath = o.Item1.XPath, linkType = o.Item1.Node.Name.LocalName,
                ownerXPath = Path(o.Item1.Owner), controlXPath = o.Item1.Control == null ? null : Path(o.Item1.Control), oldName = o.Item1.Name, newName = o.Item2 }).ToArray(),
                scope = "Exact existing symbolic Name replacements only. Names and native quoting are used verbatim; IDs, link roles, other bindings, captions, scripts and companion files retain their values. Destination existence, data type, connection partner and runtime acceptance require separate native verification." } };
        }
    }
}
