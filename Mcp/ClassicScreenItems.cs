using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenItemInspection
    {
        [JsonRequired] public string Xml { get; set; } = "";
        public string RootXPath { get; set; } = "";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenItemSelection
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public string ExpectedFingerprint { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenItemName
    {
        [JsonRequired] public string SourceXPath { get; set; } = "";
        [JsonRequired] public string NewName { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenItemChange
    {
        [JsonRequired] public string Action { get; set; } = "";
        [JsonRequired] public ClassicScreenItemSelection Selection { get; set; } = null!;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DestinationXPath { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ClassicScreenItemName[]? Names { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ClassicXmlEdit[]? Edits { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicScreenItemRequest
    {
        [JsonRequired] public ClassicScreenItemChange[] Changes { get; set; } = Array.Empty<ClassicScreenItemChange>();
    }
    internal sealed partial class ClassicXmlDocument
    {
        private sealed class ScreenItemRecord
        {
            public XElement Node = null!, Canonical = null!;
            public string XPath = "", Name = "";
            public string Fingerprint => TextListDocument.Hash(Canonical.ToString(SaveOptions.DisableFormatting));
        }
        private sealed class ScreenItemState
        {
            public ScreenItemRecord[] Items = Array.Empty<ScreenItemRecord>();
            public Dictionary<string, XElement> Containers = new Dictionary<string, XElement>(StringComparer.Ordinal);
            public string? UnsupportedReason;
        }
        private sealed class ScreenItemOperation
        {
            public ClassicScreenItemChange Change = null!;
            public ScreenItemRecord Source = null!;
            public ScreenItemRecord[] Members = Array.Empty<ScreenItemRecord>();
            public XElement? Destination, Clone, Prototype;
        }
        private static bool ScreenItem(XElement e) => e.Name.NamespaceName == "" && e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
            && (string?)e.Attribute("CompositionName") == "ScreenItems" && e.Parent?.Name == "ObjectList"
            && !e.Ancestors().Any(a => BehaviorCompositions.Contains((string?)a.Attribute("CompositionName"), StringComparer.Ordinal));
        private static bool ItemNameValid(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl);
        private static string[] ItemReferenceMetadata(XElement node) => node.DescendantsAndSelf().Attributes()
            .Where(a => new[] { "ID", "UId", "TargetID", "CompositionName", "external" }.Contains(a.Name.LocalName))
            .Select(a => a.Name + "=" + a.Value).Concat(node.Descendants().Where(e => e.Name.LocalName == "Culture").Select(e => "Culture=" + e.Value)).ToArray();
        private static int ExternalItemIdLinks(XElement? node)
        {
            if (node == null) return 0;
            var local = new HashSet<string>(node.DescendantsAndSelf().Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
            return node.DescendantsAndSelf().Attributes("TargetID").Count(a => a.Value != "@OpenLink" && !local.Contains(NativeXmlIds.TargetKey(a.Value)));
        }
        private ScreenItemState ScreenItemsState()
        {
            if (!new[] { "Hmi.Screen.Screen", "Hmi.Screen.ScreenTemplate", "Hmi.Screen.ScreenPopup", "Hmi.Screen.ScreenSlidein", "Hmi.Screen.ScreenOverview", "Hmi.Screen.ScreenGlobalElements" }.Contains(NativeType))
                throw new ArgumentException("Screen item changes require a native classic screen resource.");
            var nodes = resource.DescendantsAndSelf().Take(50001).ToArray();
            if (Name.Length > 512 || nodes.Length > 50000 || nodes.Any(n => n.Ancestors().Take(65).Count() > 64)) throw new ArgumentException("Screen items exceed 512 resource-name characters, 50000 elements or 64 XML levels.");
            var items = nodes.Where(ScreenItem).ToArray();
            if (items.Length > 2000) throw new ArgumentException("Screen item inspection is limited to 2000 exported controls.");
            var canonical = new XDocument(document); NativeXmlIds.Normalize(canonical);
            foreach (var e in canonical.Descendants().Where(e => e.HasElements && !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Text")))
                e.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
            var copies = canonical.Descendants().Where(ScreenItem).ToArray(); var state = new ScreenItemState();
            if (document.Descendants().Attributes().Any(a => a.Name.LocalName == "UId")) state.UnsupportedReason = "UId identity/reference formats require a separate native adapter.";
            try { NativeXmlIds.VerifyReferences(resource, document); }
            catch (ArgumentException) { state.UnsupportedReason = "An exported native ID reference is unresolved; use a reviewed native resource import."; }
            state.Items = items.Select((e, i) => new ScreenItemRecord { Node = e, Canonical = copies[i], XPath = Path(e), Name = ObjectNameField(e)?.Value ?? "" }).ToArray();
            foreach (var item in state.Items)
            {
                if (item.XPath.Length > 4096) throw new ArgumentException("Screen item paths exceed 4096 characters.");
                if (!ItemNameValid(item.Name) || ObjectNameField(item.Node) == null || string.IsNullOrEmpty((string?)item.Node.Attribute("ID")))
                    state.UnsupportedReason = "Every exported control requires an ID and one unambiguous scalar ObjectName or Name of 1..128 characters.";
                state.Containers[Path(item.Node.Parent!)] = item.Node.Parent!;
            }
            if (state.Items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) state.UnsupportedReason = "Duplicate exported control names require a reviewed native resource import.";
            return state;
        }
        private string? RemovalBlock(ScreenItemRecord[] removed)
        {
            var nodes = new HashSet<XElement>(removed.SelectMany(i => i.Node.DescendantsAndSelf()));
            var ids = new HashSet<string>(nodes.Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
            if (document.Descendants().Attributes("TargetID").Any(a => ids.Contains(NativeXmlIds.TargetKey(a.Value)) && !nodes.Contains(a.Parent!)))
                return "A retained exported object references a removed native ID; remove the dependent object too or use an explicit native import.";
            var names = new HashSet<string>(removed.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var link in document.Descendants().Where(e => (string?)e.Attribute("TargetID") == "@OpenLink" && !nodes.Contains(e)))
                foreach (var name in link.Elements("Name"))
                {
                    string value = name.Value;
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
                    if (names.Contains(value)) return "A retained symbolic open link matches a removed control name; resolve that link before removal.";
                }
            return null;
        }
        public object ScreenItemCatalog(ClassicScreenItemInspection request)
        {
            if (request.RootXPath == null || request.RootXPath.Length > 4096 || request.Offset < 0 || request.Offset > 2000 || request.MaxItems < 1 || request.MaxItems > 200)
                throw new ArgumentException("Use an exact rootXPath, offset 0..2000 and maxItems 1..200.");
            var state = ScreenItemsState(); XElement? root = null;
            if (request.RootXPath.Length != 0) root = state.Items.SingleOrDefault(i => i.XPath == request.RootXPath)?.Node ?? throw new ArgumentException("rootXPath must be an exact returned control path.");
            var filtered = state.Items.Where(i => root == null || i.Node.AncestorsAndSelf().Contains(root)).ToArray();
            var rows = new List<object>(); int next = request.Offset, characters = 0; bool limited = false;
            foreach (var item in filtered.Skip(request.Offset).Take(request.MaxItems))
            {
                var members = state.Items.Where(i => i.Node.AncestorsAndSelf().Contains(item.Node)).ToArray();
                string? cloneReason = state.UnsupportedReason ?? (members.Length > 500 || item.Node.DescendantsAndSelf().Take(5001).Count() > 5000 ? "Prototype exceeds 500 controls or 5000 XML elements." : null);
                string? removalReason = state.UnsupportedReason ?? RemovalBlock(members);
                var row = new { xPath = item.XPath, item.Name, nativeType = item.Node.Name.LocalName, containerXPath = Path(item.Node.Parent!), subtreeItems = members.Length,
                    cloneAvailable = cloneReason == null, cloneUnavailableReason = cloneReason, standaloneRemovalAvailable = removalReason == null, removalUnavailableReason = removalReason,
                    selection = new ClassicScreenItemSelection { XPath = item.XPath, ExpectedFingerprint = item.Fingerprint } };
                int cost = JsonSerializer.Serialize(row, JsonDefaults.Options).Length;
                if (characters + cost > 258048) { limited = true; break; }
                rows.Add(row); characters += cost; next++;
            }
            return new { Name, NativeType, items = rows, matchedItems = filtered.Length, offset = request.Offset, nextOffset = next, hasMore = next < filtered.Length, payloadLimited = limited,
                scope = "Existing exported classic ScreenItems and containers already holding controls. Native IDs and exact symbolic open-link names are checked for removal; code strings, computed names, runtime behavior and native acceptance are not evaluated." };
        }
        public object ChangeScreenItems(ClassicScreenItemRequest request)
        {
            if (request?.Changes == null || request.Changes.Length < 1 || request.Changes.Length > 50) throw new ArgumentException("Supply 1 to 50 exact screen item changes.");
            var candidate = new ClassicXmlDocument(document.ToString(SaveOptions.DisableFormatting)); var state = candidate.ScreenItemsState();
            if (state.UnsupportedReason != null) throw new NotSupportedException(state.UnsupportedReason);
            var operations = new List<ScreenItemOperation>(); int newControls = 0, prototypeCharacters = 0;
            foreach (var change in request.Changes)
            {
                var selection = change?.Selection;
                if (selection == null || selection.XPath == null || selection.ExpectedFingerprint == null) throw new ArgumentException("Each change requires an exact returned selection.");
                var source = state.Items.SingleOrDefault(i => i.XPath == selection.XPath) ?? throw new ArgumentException("Select an exact control path from inspect_screen_items.");
                if (source.Fingerprint != selection.ExpectedFingerprint) throw new InvalidOperationException("Screen item fingerprint changed; inspect the screen again.");
                if (operations.Any(o => (o.Source.Node.AncestorsAndSelf().Contains(source.Node) || source.Node.AncestorsAndSelf().Contains(o.Source.Node))
                    && !(o.Source.Node == source.Node && o.Change.Action == "clone" && change!.Action == "clone")))
                    throw new ArgumentException("Duplicate or overlapping source controls require separate previews.");
                var operation = new ScreenItemOperation { Change = change!, Source = source, Members = state.Items.Where(i => i.Node.AncestorsAndSelf().Contains(source.Node)).ToArray() };
                if (change!.Action == "clone")
                {
                    if (change.DestinationXPath == null || !state.Containers.TryGetValue(change.DestinationXPath, out var destination)) throw new ArgumentException("Clone destination must be an exact returned containerXPath already holding controls.");
                    if (destination.AncestorsAndSelf().Contains(source.Node)) throw new ArgumentException("A prototype cannot be cloned inside its own subtree.");
                    if ((newControls += operation.Members.Length) > 500 || source.Node.DescendantsAndSelf().Take(5001).Count() > 5000) throw new ArgumentException("Clone preview exceeds 500 new controls or a 5000-element prototype.");
                    if ((prototypeCharacters += source.Node.ToString(SaveOptions.DisableFormatting).Length) > 4 * 1024 * 1024) throw new ArgumentException("Prototype copies exceed the 4 MiB preview budget.");
                    if (change.Names == null || change.Names.Length != operation.Members.Length || change.Names.Any(n => n == null || n.SourceXPath == null || !ItemNameValid(n.NewName))
                        || change.Names.Select(n => n.SourceXPath).Distinct(StringComparer.Ordinal).Count() != change.Names.Length
                        || change.Names.Any(n => !operation.Members.Any(m => m.XPath == n.SourceXPath)))
                        throw new ArgumentException("Supply exactly one explicit name mapping for every control in the cloned subtree, including its root.");
                    if (change.Edits != null && (change.Edits.Length > 100 || change.Edits.Any(e => e == null || e.XPath == null || e.ExpectedValue == null || e.NewValue == null || e.XPath.Length > 4096 || e.ExpectedValue.Length > 4096 || e.NewValue.Length > 4096)))
                        throw new ArgumentException("Optional clone edits allow 0..100 bounded existing scalar changes.");
                    operation.Destination = destination;
                    // Later clones may be inserted into this original source's container.
                    // Keep every prototype bound to the initial snapshot of the batch.
                    operation.Prototype = new XElement(source.Node);
                }
                else if (change.Action != "remove" || change.DestinationXPath != null || change.Names != null || change.Edits != null)
                    throw new ArgumentException("Use clone with explicit destination/names/optional scalar edits, or remove with only its selection.");
                operations.Add(operation);
            }
            var removed = operations.Where(o => o.Change.Action == "remove").SelectMany(o => o.Members).ToArray();
            string? blocked = candidate.RemovalBlock(removed); if (blocked != null) throw new InvalidOperationException(blocked);
            if (operations.Any(o => o.Destination != null && removed.Any(r => o.Destination.AncestorsAndSelf().Contains(r.Node)))) throw new ArgumentException("A clone destination is removed by this preview.");
            var names = new HashSet<string>(state.Items.Except(removed).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var name in operations.Where(o => o.Change.Action == "clone").SelectMany(o => o.Change.Names!))
                if (!names.Add(name.NewName)) throw new ArgumentException("Cloned control names must be unique across retained and new screen items (case-insensitive).");
            int extraCharacters = 0;
            // Clone while all original IDs remain reserved; deletion must not free an ID for accidental reuse.
            foreach (var op in operations.Where(o => o.Change.Action == "clone"))
            {
                var copy = NativeXmlIds.Clone(op.Prototype!, candidate.document);
                var members = new[] { copy }.Concat(copy.Descendants().Where(ScreenItem)).ToArray();
                string[] expectedNames = op.Members.Select(m => op.Change.Names!.Single(n => n.SourceXPath == m.XPath).NewName).ToArray();
                for (int i = 0; i < members.Length; i++) ObjectNameField(members[i])!.Value = expectedNames[i];
                string[] references = ItemReferenceMetadata(copy);
                if (op.Change.Edits?.Length > 0)
                    copy = TransferFiles.Xml(new ClassicXmlDocument(new XDocument(new XElement("Document", copy)).ToString(SaveOptions.DisableFormatting), false).Edit(op.Change.Edits)).Root!.Elements().Single();
                if (!ItemReferenceMetadata(copy).SequenceEqual(references)) throw new ArgumentException("Clone scalar edits cannot change native IDs, TargetID links, composition, culture identities or external-file metadata.");
                members = new[] { copy }.Concat(copy.Descendants().Where(ScreenItem)).ToArray();
                if (!members.Select(m => ObjectNameField(m)?.Value).SequenceEqual(expectedNames)) throw new ArgumentException("Scalar clone edits cannot override explicit control names.");
                if ((extraCharacters += copy.ToString(SaveOptions.DisableFormatting).Length) > 4 * 1024 * 1024) throw new ArgumentException("Cloned XML exceeds the 4 MiB preview budget.");
                if (copy.Parent != null) copy.Remove();
                op.Destination!.Add(copy); op.Clone = copy;
            }
            foreach (var op in operations.Where(o => o.Change.Action == "remove")) op.Source.Node.Remove();
            NativeXmlIds.VerifyReferences(candidate.resource, candidate.document);
            var final = candidate.ScreenItemsState(); if (final.UnsupportedReason != null) throw new ArgumentException(final.UnsupportedReason);
            string xml = candidate.document.ToString(SaveOptions.DisableFormatting);
            if (xml.Length > 16 * 1024 * 1024) throw new ArgumentException("Candidate XML exceeds 16 MiB.");
            return new { candidateXml = xml, changes = new { operations = operations.Select(o => new { o.Change.Action, o.Change.Selection, o.Change.DestinationXPath, o.Change.Names, o.Change.Edits,
                affectedControls = o.Members.Length, newRootXPath = o.Clone == null ? null : Path(o.Clone),
                retainedOpenLinks = o.Clone?.DescendantsAndSelf().Attributes("TargetID").Count(a => a.Value == "@OpenLink") ?? 0,
                retainedExternalNativeIdLinks = ExternalItemIdLinks(o.Clone) }).ToArray(),
                scope = "Native prototype copies/removals inside one screen. IDs/internal ID links are remapped; external ID links, symbolic names, events, scripts, geometry and sidecars retain their existing values unless explicitly edited. No symbolic-reference rewriting, coordinate conversion or runtime evaluation is performed." } };
        }
    }
}
