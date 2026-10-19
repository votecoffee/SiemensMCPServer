using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicBehaviorInspection
    {
        [JsonRequired] public string Xml { get; set; } = "";
        public string RootXPath { get; set; } = "";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicBehaviorReferenceTarget
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedTargetXPath { get; set; } = "";
        [JsonRequired] public string NewTargetXPath { get; set; } = "";
    }
    // The fixed native tool keeps its original DTO/schema. These extra fields belong to the worker catalog.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicBehaviorOperation
    {
        [JsonRequired] public string Action { get; set; } = "";
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedFingerprint { get; set; } = "";
        public string DestinationXPath { get; set; } = "";
        public ClassicXmlEdit[] Edits { get; set; } = Array.Empty<ClassicXmlEdit>();
        public ClassicBehaviorReferenceTarget[] ReferenceTargets { get; set; } = Array.Empty<ClassicBehaviorReferenceTarget>();
    }
    internal sealed partial class ClassicXmlDocument
    {
        private sealed class BehaviorRecord
        {
            public XElement Node = null!, Canonical = null!;
            public string XPath = "";
            public string Fingerprint => TextListDocument.Hash(Canonical.ToString(SaveOptions.DisableFormatting));
        }
        private sealed class BehaviorState
        {
            public BehaviorRecord[] Records = Array.Empty<BehaviorRecord>();
            public Dictionary<string, XElement> Owners = new Dictionary<string, XElement>(StringComparer.Ordinal);
            public Dictionary<string, XElement> Ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
            public string? UnavailableReason;
        }
        private sealed class BehaviorPlan
        {
            public ClassicBehaviorOperation Change = null!;
            public BehaviorRecord Source = null!;
            public XElement Prototype = null!;
            public XElement? Destination;
            public List<KeyValuePair<string, XElement>> Targets = new List<KeyValuePair<string, XElement>>();
        }
        private static bool BehaviorOwnerLink(XElement node) => node.Name.NamespaceName.Length == 0 && (node.Name.LocalName == "Owner" || node.Name.LocalName == "Control")
            && node.Parent?.Name == "LinkList" && node.Attribute("TargetID") != null && !node.HasElements
            && node.Nodes().All(n => n is XText t && string.IsNullOrWhiteSpace(t.Value)) && node.Attributes().All(a => a.Name == "TargetID");
        private BehaviorState ReviewedBehaviorState()
        {
            if (!NativeReferenceOwner(resource)) throw new ArgumentException("Expected an unnamespaced native classic HMI resource.");
            var nodes = resource.DescendantsAndSelf().Take(50001).ToArray();
            if (Name.Length > 512 || nodes.Length > 50000 || nodes.Any(n => n.Ancestors().Take(65).Count() > 64)) throw new ArgumentException("Behavior resource exceeds 512 name characters, 50000 XML elements or 64 levels.");
            var originals = nodes.Where(Behavior).ToArray();
            if (originals.Length > 5000) throw new ArgumentException("Behavior inspection is limited to 5000 exported objects.");
            var canonical = new XDocument(document); NativeXmlIds.Normalize(canonical);
            foreach (var e in canonical.Descendants().Where(e => e.HasElements && !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Text")))
                e.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
            var copies = canonical.Root!.Elements().Single(e => e.Name == resource.Name).DescendantsAndSelf().Where(Behavior).ToArray();
            var state = new BehaviorState();
            if (document.Descendants().Attributes().Any(a => a.Name.LocalName == "UId")) state.UnavailableReason = "UId reference formats require a separate native adapter.";
            if (originals.Any(n => string.IsNullOrEmpty((string?)n.Attribute("ID")))) state.UnavailableReason = "Every exported behavior requires a native ID.";
            try { NativeXmlIds.VerifyReferences(resource, document); } catch (ArgumentException) { state.UnavailableReason = "An exported native ID reference is unresolved."; }
            foreach (var owner in nodes.Where(NativeReferenceOwner))
            {
                string path = Path(owner); if (path.Length > 4096) throw new ArgumentException("Behavior/owner paths exceed 4096 characters.");
                state.Owners.Add(path, owner);
            }
            state.Ids = document.Descendants().Attributes("ID").ToDictionary(a => a.Value, a => a.Parent!, StringComparer.Ordinal);
            state.Records = originals.Select((n, i) => new BehaviorRecord { Node = n, Canonical = copies[i], XPath = Path(n) }).ToArray();
            return state;
        }
        private string? BehaviorRemovalBlock(IEnumerable<XElement> roots)
        {
            var nodes = new HashSet<XElement>(roots.SelectMany(n => n.DescendantsAndSelf()));
            var ids = new HashSet<string>(nodes.Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
            return document.Descendants().Attributes("TargetID").Any(a => ids.Contains(a.Value) && !nodes.Contains(a.Parent!))
                ? "A retained exported object references a removed behavior; include the dependent behavior or use an explicit native import." : null;
        }
        public object InspectBehaviors(ClassicBehaviorInspection request)
        {
            if (request.RootXPath == null || request.RootXPath.Length > 4096 || request.Offset < 0 || request.Offset > 5000 || request.MaxItems < 1 || request.MaxItems > 200)
                throw new ArgumentException("Use an exact rootXPath, offset 0..5000 and maxItems 1..200.");
            var state = ReviewedBehaviorState(); XElement? root = null;
            if (request.RootXPath.Length != 0 && !state.Owners.TryGetValue(request.RootXPath, out root)) throw new ArgumentException("rootXPath must be an exact native object path in this resource.");
            var filtered = state.Records.Where(r => root == null || r.Node.AncestorsAndSelf().Contains(root)).ToArray();
            var rows = new List<object>(); int next = request.Offset, characters = 0; bool limited = false;
            foreach (var row in filtered.Skip(request.Offset).Take(request.MaxItems))
            {
                var owner = row.Node.Ancestors().First(NativeReferenceOwner);
                var localIds = new HashSet<string>(row.Node.DescendantsAndSelf().Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
                var wrapped = new XDocument(new XElement("Document", new XElement(row.Node))); var links = wrapped.Descendants().Where(BehaviorOwnerLink).ToArray();
                var targets = new List<object>(); int linkCharacters = 0;
                foreach (var link in links)
                {
                    string id = (string)link.Attribute("TargetID")!;
                    if (localIds.Contains(id) || !state.Ids.TryGetValue(id, out var target) || !NativeReferenceOwner(target)) continue;
                    var targetRow = new { xPath = Path(link), expectedTargetXPath = Path(target), targetType = target.Name.LocalName, targetName = ReferenceObjectName(target) };
                    int cost = JsonSerializer.Serialize(targetRow, JsonDefaults.Options).Length;
                    if (targets.Count >= 50 || linkCharacters + cost > 98304) break;
                    targets.Add(targetRow); linkCharacters += cost;
                }
                string? cloneReason = state.UnavailableReason ?? (row.Node.DescendantsAndSelf().Take(5001).Count() > 5000 || row.Node.ToString(SaveOptions.DisableFormatting).Length > 4 * 1024 * 1024 ? "Prototype exceeds 5000 XML elements or 4 MiB." : null);
                var summary = new { xPath = row.XPath, fingerprint = row.Fingerprint, nativeType = row.Node.Name.LocalName, composition = (string?)row.Node.Attribute("CompositionName"),
                    ownerXPath = Path(owner), ownerType = owner.Name.LocalName, ownerName = ReferenceObjectName(owner), cloneRootXPath = Path(wrapped.Root!.Elements().Single()),
                    cloneAvailable = cloneReason == null, cloneUnavailableReason = cloneReason, standaloneRemovalAvailable = state.UnavailableReason == null && BehaviorRemovalBlock(new[] { row.Node }) == null,
                    ownerLinks = targets, ownerLinkCatalogLimited = targets.Count < links.Count(l => !localIds.Contains((string)l.Attribute("TargetID")!) && state.Ids.TryGetValue((string)l.Attribute("TargetID")!, out var t) && NativeReferenceOwner(t)) };
                int rowCost = JsonSerializer.Serialize(summary, JsonDefaults.Options).Length;
                if (characters + rowCost > 258048) { limited = true; break; }
                rows.Add(summary); characters += rowCost; next++;
            }
            return new { Name, NativeType, objects = rows, matchedObjects = filtered.Length, offset = request.Offset, nextOffset = next, hasMore = next < filtered.Length, payloadLimited = limited,
                scope = "Exported behavior prototypes and exact external Owner/Control ID links. Clone destinations must match the original native owner type. Symbolic names and scripts are preserved; no runtime evaluation or native acceptance is asserted." };
        }
        public string ChangeReviewedBehaviors(ClassicBehaviorOperation[] changes)
        {
            if (changes == null || changes.Length < 1 || changes.Length > 100) throw new ArgumentException("Supply 1 to 100 native behavior changes.");
            var candidate = new ClassicXmlDocument(document.ToString(SaveOptions.DisableFormatting)); var state = candidate.ReviewedBehaviorState();
            if (state.UnavailableReason != null) throw new NotSupportedException(state.UnavailableReason);
            var plans = new List<BehaviorPlan>(); int copiedElements = 0, copiedCharacters = 0;
            foreach (var change in changes)
            {
                if (change == null || change.XPath == null || change.ExpectedFingerprint == null || change.DestinationXPath == null || change.Edits == null || change.ReferenceTargets == null)
                    throw new ArgumentException("Behavior change fields cannot be null.");
                if (change.XPath.Length > 4096 || change.DestinationXPath.Length > 4096 || change.Edits.Length > 100 || change.ReferenceTargets.Length > 50)
                    throw new ArgumentException("Behavior paths/edits exceed 4096 characters, 100 scalar edits or 50 owner-link maps.");
                var selected = candidate.Select(change.XPath); var source = state.Records.SingleOrDefault(r => r.Node == selected) ?? throw new ArgumentException("Select an exported native behavior.");
                if (source.Fingerprint != change.ExpectedFingerprint) throw new InvalidOperationException("Behavior fingerprint differs; read the resource again.");
                if (plans.Any(p => p.Source.Node.AncestorsAndSelf().Contains(selected) || selected.AncestorsAndSelf().Contains(p.Source.Node))) throw new ArgumentException("Duplicate or overlapping behavior sources require separate previews.");
                var plan = new BehaviorPlan { Change = change, Source = source, Prototype = new XElement(selected) };
                if (change.Action == "clone")
                {
                    plan.Destination = candidate.Select(change.DestinationXPath);
                    var sourceOwner = selected.Ancestors().First(NativeReferenceOwner);
                    if (!NativeReferenceOwner(plan.Destination) || plan.Destination.Name != sourceOwner.Name) throw new ArgumentException("Clone destination must match the prototype's native owner type.");
                    if (plan.Destination.Elements("ObjectList").Count() > 1) throw new ArgumentException("Clone destination has ambiguous ObjectList containers.");
                    if (plan.Destination.AncestorsAndSelf().Contains(selected)) throw new ArgumentException("A prototype cannot be cloned inside its own subtree.");
                    int count = selected.DescendantsAndSelf().Take(5001).Count();
                    if (count > 5000 || (copiedElements += count) > 10000 || (copiedCharacters += selected.ToString(SaveOptions.DisableFormatting).Length) > 4 * 1024 * 1024)
                        throw new ArgumentException("Behavior clones exceed 5000 elements per prototype, 10000 copied elements or 4 MiB of XML.");
                    if (change.Edits.Any(e => e == null || e.XPath == null || e.ExpectedValue == null || e.NewValue == null || e.XPath.Length > 4096 || e.ExpectedValue.Length > 4096 || e.NewValue.Length > 4096))
                        throw new ArgumentException("Behavior scalar edits must be bounded existing fields.");
                    var wrapped = new XDocument(new XElement("Document", new XElement(selected)));
                    var eligible = wrapped.Descendants().Where(BehaviorOwnerLink).ToDictionary(Path, StringComparer.Ordinal);
                    var localIds = new HashSet<string>(selected.DescendantsAndSelf().Attributes("ID").Select(a => a.Value), StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var map in change.ReferenceTargets)
                    {
                        if (map == null || map.XPath == null || map.ExpectedTargetXPath == null || map.NewTargetXPath == null || map.XPath.Length > 4096 || map.ExpectedTargetXPath.Length > 4096 || map.NewTargetXPath.Length > 4096
                            || !seen.Add(map.XPath) || !eligible.TryGetValue(map.XPath, out var link)) throw new ArgumentException("Use unique exact clone-relative Owner/Control link paths from inspect_behaviors.");
                        string id = (string)link.Attribute("TargetID")!;
                        if (localIds.Contains(id) || !state.Ids.TryGetValue(id, out var oldTarget) || !NativeReferenceOwner(oldTarget) || Path(oldTarget) != map.ExpectedTargetXPath)
                            throw new InvalidOperationException("Expected external owner-link target differs; inspect the behavior again.");
                        if (!state.Owners.TryGetValue(map.NewTargetXPath, out var newTarget) || oldTarget.Name != newTarget.Name || string.IsNullOrEmpty((string?)newTarget.Attribute("ID")))
                            throw new ArgumentException("New owner-link target must be an exact native object path of the same type with an exported ID.");
                        if (oldTarget == newTarget) throw new ArgumentException("Owner-link map contains no change.");
                        plan.Targets.Add(new KeyValuePair<string, XElement>(map.XPath, newTarget));
                    }
                }
                else if (change.Action != "remove" || change.DestinationXPath.Length != 0 || change.Edits.Length != 0 || change.ReferenceTargets.Length != 0)
                    throw new ArgumentException("Use clone with a destination/optional edits and owner links, or remove without clone fields.");
                plans.Add(plan);
            }
            var removed = plans.Where(p => p.Change.Action == "remove").Select(p => p.Source.Node).ToArray();
            string? blocked = candidate.BehaviorRemovalBlock(removed); if (blocked != null) throw new InvalidOperationException(blocked);
            if (plans.Any(p => p.Destination != null && removed.Any(r => p.Destination.AncestorsAndSelf().Contains(r))) ||
                plans.SelectMany(p => p.Targets).Any(t => removed.Any(r => t.Value.AncestorsAndSelf().Contains(r)))) throw new ArgumentException("A clone destination or mapped target is removed by this preview.");
            int finalCopyCharacters = 0;
            // Freeze prototypes first; copy before removing anything so no original ID can be reused.
            foreach (var plan in plans.Where(p => p.Change.Action == "clone"))
            {
                var clone = NativeXmlIds.Clone(plan.Prototype, candidate.document);
                var wrapped = new XDocument(new XElement("Document", clone));
                var links = wrapped.Descendants().Where(BehaviorOwnerLink).ToDictionary(Path, StringComparer.Ordinal);
                foreach (var map in plan.Targets) links[map.Key].SetAttributeValue("TargetID", (string)map.Value.Attribute("ID")!);
                string[] metadata = ItemReferenceMetadata(clone);
                if (plan.Change.Edits.Length != 0)
                    clone = TransferFiles.Xml(new ClassicXmlDocument(wrapped.ToString(SaveOptions.DisableFormatting), false).Edit(plan.Change.Edits)).Root!.Elements().Single();
                if (!metadata.SequenceEqual(ItemReferenceMetadata(clone))) throw new ArgumentException("Scalar edits cannot change IDs, owner links, compositions, cultures or external metadata; use explicit supported owner-link maps.");
                if ((finalCopyCharacters += clone.ToString(SaveOptions.DisableFormatting).Length) > 4 * 1024 * 1024) throw new ArgumentException("Cloned behavior XML exceeds 4 MiB.");
                if (clone.Parent != null) clone.Remove();
                var list = plan.Destination!.Element("ObjectList");
                if (list == null) { list = new XElement("ObjectList"); plan.Destination.Add(list); }
                list.Add(clone);
            }
            foreach (var node in removed) node.Remove();
            NativeXmlIds.VerifyReferences(candidate.resource, candidate.document); candidate.ReviewedBehaviorState();
            string xml = candidate.document.ToString(SaveOptions.DisableFormatting);
            if (xml.Length > 16 * 1024 * 1024) throw new ArgumentException("Behavior candidate exceeds 16 MiB.");
            return xml;
        }
    }
}
