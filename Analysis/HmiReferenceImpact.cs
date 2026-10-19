using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Analysis
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiProposedChange
    {
        [JsonRequired] public string Kind { get; set; } = "";
        [JsonRequired] public string Name { get; set; } = "";
        public string HmiId { get; set; } = "";
        public string ControllerId { get; set; } = "";
        public string NewName { get; set; } = "";
        public string NewConnection { get; set; } = "";
        public string NewSymbol { get; set; } = "";
        public string NewControllerId { get; set; } = "";
    }
    internal sealed class HmiImpactItem
    {
        public int ChangeIndex { get; set; }
        public string Reason { get; set; } = "";
        public string Certainty { get; set; } = "";
        public HmiReferenceOccurrence Reference { get; set; } = null!;
        public HmiBindingValidation? ProposedBinding { get; set; }
    }
    internal sealed class HmiImpactReport
    {
        public string AnalysisSha256 { get; set; } = "";
        public string ProposalSha256 { get; set; } = "";
        public HmiProposedChange[] Changes { get; set; } = Array.Empty<HmiProposedChange>();
        public HmiImpactItem[] Items { get; set; } = Array.Empty<HmiImpactItem>();
        public string[] Issues { get; set; } = Array.Empty<string>();
        public AnalysisDiagnostic[] BlindSpots { get; set; } = Array.Empty<AnalysisDiagnostic>();
        public string Scope => "Read-only impact against one hash-bound export snapshot. Each proposal is assessed independently against the baseline. No names, bindings, metadata or native project are changed; opaque/dynamic/unexported references may add further impacts.";
    }
    internal static class HmiReferenceImpact
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        internal static bool Within(string candidate, string parent) => Names.Equals(candidate, parent) || candidate.Length > parent.Length
            && candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && (candidate[parent.Length] == '.' || candidate[parent.Length] == '[');
        public static HmiImpactReport Report(HmiAnalysisInput input, HmiAnalysisReport analysis, string expectedHash, HmiProposedChange[] changes)
        {
            if (expectedHash != analysis.AnalysisSha256) throw new InvalidOperationException("Analysis fingerprint changed; analyze this snapshot again before reporting impact.");
            if (changes == null || changes.Length == 0 || changes.Length > 100) throw new ArgumentException("Supply 1..100 proposed changes.");
            var items = new List<HmiImpactItem>(); var issues = new List<string>();
            for (int index = 0; index < changes.Length; index++)
            {
                var change = changes[index];
                if (change == null || !new[] { "tag", "connection", "binding", "controllerSymbol" }.Contains(change.Kind)) throw new ArgumentException("Change kind must be tag, connection, binding or controllerSymbol.");
                ControllerDeclarations.Name(change.Name, "change target");
                foreach (var value in new[] { change.HmiId, change.ControllerId, change.NewName, change.NewConnection, change.NewSymbol, change.NewControllerId })
                    if (value == null || value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Proposed change values must be bounded plain strings.");
                if (change.Kind == "controllerSymbol" && change.ControllerId.Length == 0) throw new ArgumentException("controllerSymbol impact requires an explicit controllerId.");
                var tags = analysis.Tags.Where(t => (change.HmiId.Length == 0 || Names.Equals(t.HmiId, change.HmiId))
                    && (change.Kind == "connection" ? Names.Equals(t.Connection, change.Name) : Names.Equals(t.Name, change.Name))).ToArray();
                var selected = new HashSet<string>(tags.Select(t => t.Id), StringComparer.Ordinal);
                var proposed = new Dictionary<string, HmiBindingValidation>(StringComparer.Ordinal);
                if (change.Kind != "controllerSymbol" && tags.Length == 0) issues.Add("Proposal " + index + " has no matching declared HMI tag/connection.");
                if (change.Kind == "tag" && change.NewName.Length != 0 && analysis.Tags.Any(t => !selected.Contains(t.Id)
                    && (change.HmiId.Length == 0 || Names.Equals(change.HmiId, t.HmiId)) && Names.Equals(t.Name, change.NewName)))
                    issues.Add("Proposal " + index + " collides with an existing HMI tag name.");
                if (change.Kind == "binding" || change.Kind == "connection" && change.NewControllerId.Length != 0)
                {
                    foreach (var tag in tags)
                    {
                        var copy = JsonSerializer.Deserialize<HmiTagDeclaration>(JsonSerializer.Serialize(tag, JsonDefaults.Options), JsonDefaults.Options)!;
                        if (change.NewConnection.Length != 0) copy.Connection = change.NewConnection;
                        if (change.NewSymbol.Length != 0) { copy.ControllerSymbol = change.NewSymbol; copy.LogicalAddress = ""; }
                        var mappings = input.ConnectionMappings;
                        if (change.Kind == "connection" && change.NewControllerId.Length != 0)
                        {
                            var previous = HmiBindingValidator.Mapping(mappings, tag.HmiId, tag.Connection);
                            mappings = mappings.Where(m => !(Names.Equals(m.Connection, tag.Connection) && Names.Equals(m.HmiId, tag.HmiId))).Concat(new[] {
                                new HmiConnectionMapping { HmiId = tag.HmiId, Connection = tag.Connection, ControllerId = change.NewControllerId,
                                    DefaultScope = previous?.DefaultScope ?? "", AddressSyntax = previous?.AddressSyntax ?? "canonical" } }).ToArray();
                        }
                        proposed.Add(tag.Id, HmiBindingValidator.Validate(new[] { copy }, analysis.Controllers, mappings, analysis.ConnectionDefinitions, analysis.ConnectionMetadataAvailable).Single());
                    }
                }
                int before = items.Count;
                foreach (var reference in analysis.References.Where(r => change.HmiId.Length == 0 || Names.Equals(r.HmiId, change.HmiId)))
                {
                    bool match = change.Kind == "controllerSymbol"
                        ? Names.Equals(reference.ControllerId, change.ControllerId) && (Within(reference.ControllerAddress, change.Name)
                            || reference.Resolution != null && new[] { reference.Resolution.CanonicalSymbol }.Concat(reference.Resolution.AliasChain).Concat(reference.Resolution.DeclarationPaths).Any(p => Within(p, change.Name)))
                            || Names.Equals(reference.ControllerId, change.ControllerId) && reference.Resolution?.Dimensions.Length > 0
                                && new[] { reference.Resolution.CanonicalSymbol }.Concat(reference.Resolution.AliasChain).Any(p => Within(change.Name, p))
                        : selected.Contains(reference.TagId) || change.Kind == "connection" && Names.Equals(reference.Connection, change.Name);
                    bool possible = false;
                    if (!match && new[] { "tag", "binding" }.Contains(change.Kind) && reference.LinkType == "Tag" && reference.TagId.Length == 0)
                    {
                        string name = HmiReferenceAnalyzer.Unquote(reference.ReferenceText);
                        if (Names.Equals(name, change.Name) || name.StartsWith(change.Name + "[", StringComparison.OrdinalIgnoreCase)) { match = true; possible = true; }
                    }
                    if (!match) continue;
                    items.Add(new HmiImpactItem { ChangeIndex = index, Reference = reference,
                        Certainty = !possible && new[] { "valid", "internal", "connectionReference", "screenReference" }.Contains(reference.Status) ? "definite" : "possible",
                        Reason = change.Kind == "controllerSymbol" ? "Controller address/member or alias/base declaration dependency." : "HMI " + change.Kind + " dependency.",
                        ProposedBinding = proposed.TryGetValue(reference.TagId, out var binding) ? binding : null });
                }
                if (items.Count == before) issues.Add("Proposal " + index + " has no definite exported static reference matches; review unresolved/opaque regions.");
            }
            if (items.Count > 100000) throw new ArgumentException("Impact report exceeds 100000 rows.");
            return new HmiImpactReport { AnalysisSha256 = analysis.AnalysisSha256,
                ProposalSha256 = TextListDocument.Hash(JsonSerializer.Serialize(changes, JsonDefaults.Options)), Changes = changes, Items = items.ToArray(),
                Issues = issues.ToArray(), BlindSpots = analysis.Diagnostics };
        }
    }
}
