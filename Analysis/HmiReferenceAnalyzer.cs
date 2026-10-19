using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Analysis
{
    internal static class HmiReferenceAnalyzer
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        private sealed class Resource
        {
            public HmiResourceInput Input = null!;
            public XElement Root = null!;
            public Dictionary<string, XElement> Ids = null!;
            public Dictionary<XElement, string> Paths = null!;
        }
        internal static string Scalar(XElement? owner, string name)
        {
            var nodes = owner?.Elements(name).ToArray() ?? Array.Empty<XElement>();
            return nodes.Length == 1 && !nodes[0].HasElements ? nodes[0].Value : "";
        }
        private static string ObjectName(XElement? owner) => owner == null ? "" : ClassicXmlDocument.ObjectNameField(owner)?.Value ?? "";
        private static string Link(XElement owner, string role)
        {
            var links = owner.Elements("LinkList").Elements(role).ToArray();
            return links.Length == 1 && (string?)links[0].Attribute("TargetID") == "@OpenLink" && links[0].Elements().Count() == 1
                && links[0].Element("Name") != null && !links[0].Element("Name")!.HasElements ? Scalar(links[0], "Name") : "";
        }
        internal static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? value.Substring(1, value.Length - 2).Replace("\"\"", "\"") : value;
        private static string Path(XElement node) => "/" + string.Join("/", node.AncestorsAndSelf().Reverse().Select(n => n.Name.LocalName + "[" + (n.ElementsBeforeSelf(n.Name).Count() + 1).ToString(CultureInfo.InvariantCulture) + "]"));
        private static string TagName(Resource resource, XElement tag)
        {
            string name = ObjectName(tag);
            if (name.Contains("\\")) return name; // V15.1 exports already-qualified Name values.
            var parts = resource.Input.Path.Split('/'); var folders = new List<string>();
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].StartsWith("n:", StringComparison.Ordinal) && new[] { "p:Folders", "p:TagFolders" }.Contains(parts[i - 1])) folders.Add(Uri.UnescapeDataString(parts[i].Substring(2)));
            folders.AddRange(tag.Ancestors().Reverse().Where(n => n != resource.Root && n.Name == "Hmi.Tag.TagFolder").Select(ObjectName).Where(n => n.Length != 0));
            return string.Join("\\", folders.Concat(new[] { name }));
        }
        internal static (string type, DeclarationDimension[] dimensions) TypeShape(string value)
        {
            var array = Regex.Match(value, @"^Array\s*\[([^\]]+)\]\s+of\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!array.Success) return (ControllerSymbolResolver.Atomic(value), Array.Empty<DeclarationDimension>());
            var axes = array.Groups[1].Value.Split(',');
            if (axes.Length > 8) return ("", Array.Empty<DeclarationDimension>());
            var result = new List<DeclarationDimension>();
            foreach (var axis in axes)
            {
                var range = Regex.Match(axis.Trim(), @"^(-?\d+)\.\.(-?\d+)$", RegexOptions.CultureInvariant);
                if (!range.Success || !int.TryParse(range.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lower)
                    || !int.TryParse(range.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int upper) || lower > upper)
                    return ("", Array.Empty<DeclarationDimension>());
                result.Add(new DeclarationDimension { Lower = lower, Upper = upper });
            }
            return (ControllerSymbolResolver.Atomic(array.Groups[2].Value), result.ToArray());
        }
        public static HmiAnalysisReport Analyze(HmiAnalysisInput input)
        {
            if (input.Resources == null || input.Resources.Length < 1 || input.Resources.Length > 2000 || input.ControllerDocuments == null
                || input.ControllerDocuments.Length > 32 || input.ConnectionMappings == null || input.ConnectionMappings.Length > 1000
                || input.ExportFailures == null || input.UnsupportedResources == null) throw new ArgumentException("Supply 1..2000 resources, at most 32 controllers and 1000 connection mappings.");
            foreach (var mapping in input.ConnectionMappings)
            {
                if (mapping == null) throw new ArgumentException("Connection mapping cannot be null.");
                ControllerDeclarations.Name(mapping.Connection, "connection"); ControllerDeclarations.Name(mapping.ControllerId, "controller ID");
                if (mapping.HmiId == null || mapping.DefaultScope == null || mapping.ExpectedControllerName == null || mapping.HmiId.Length > 4096 || mapping.DefaultScope.Length > 1024 || mapping.ExpectedControllerName.Length > 1024)
                    throw new ArgumentException("Invalid connection mapping scope.");
            }
            if (input.ConnectionMappings.GroupBy(m => m.HmiId + "\n" + m.Connection, Names).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate/ambiguous connection mappings.");
            var controllers = input.ControllerDocuments.Select(ControllerDeclarations.Import).ToArray();
            if (controllers.GroupBy(c => c.Id, Names).Any(g => g.Count() != 1)) throw new ArgumentException("Controller IDs must be unique.");
            var resources = new List<Resource>(); var tags = new List<HmiTagDeclaration>(); var tagNodes = new Dictionary<XElement, HmiTagDeclaration>();
            var diagnostics = new List<AnalysisDiagnostic>(); var references = new List<HmiReferenceOccurrence>(); long characters = 0, elements = 0;
            foreach (var supplied in input.Resources)
            {
                if (supplied == null || supplied.HmiId == null || supplied.HmiId.Length > 4096 || supplied.Path == null || supplied.Path.Length > 4096
                    || supplied.Xml == null || supplied.Xml.Length > 16 * 1024 * 1024 || (characters += supplied.Xml.Length) > 128 * 1024 * 1024)
                    throw new ArgumentException("HMI XML exceeds resource/path/128 MiB aggregate limits.");
                var document = TransferFiles.Xml(supplied.Xml);
                var roots = document.Root?.Elements().Where(e => e.Name.NamespaceName == "" && e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)).ToArray();
                if (document.Root?.Name != "Document" || roots?.Length != 1) throw new ArgumentException("Each HMI resource must contain one native classic Hmi resource in Document.");
                var nodes = roots[0].DescendantsAndSelf().Take(100001).ToArray();
                if (nodes.Length > 100000 || (elements += nodes.Length) > 2000000 || nodes.Any(n => n.Ancestors().Take(65).Count() > 64)) throw new ArgumentException("HMI resource element/depth limits exceeded.");
                var ids = nodes.Where(n => n.Attribute("ID") != null).ToArray();
                if (ids.GroupBy(n => n.Attribute("ID")!.Value, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate native XML IDs.");
                var resource = new Resource { Input = supplied, Root = roots[0], Ids = ids.ToDictionary(n => n.Attribute("ID")!.Value, StringComparer.Ordinal),
                    Paths = nodes.ToDictionary(n => n, Path) };
                resources.Add(resource);
                foreach (var tag in nodes.Where(n => n.Name == "Hmi.Tag.Tag"))
                {
                    string name = TagName(resource, tag); ControllerDeclarations.Name(name, "HMI tag name");
                    var shape = TypeShape(Link(tag, "DataType")); var issues = new List<string>();
                    foreach (string role in new[] { "Connection", "ControllerTag", "DataType", "HmiDataType", "Tag" })
                        if (tag.Elements("LinkList").Elements(role).Any() && Link(tag, role).Length == 0) issues.Add("Unsupported or ambiguous " + role + " declaration link.");
                    string length = Scalar(tag.Element("AttributeList"), "Length");
                    if (shape.dimensions.Length != 0 && (length.Length != 0 && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out long total)
                        || shape.dimensions.Aggregate(1m, (product, axis) => product * ((decimal)axis.Upper - axis.Lower + 1)) != total))) issues.Add("HMI Length differs from declared array dimensions.");
                    var item = new HmiTagDeclaration { HmiId = supplied.HmiId, ResourcePath = supplied.Path, XPath = resource.Paths[tag], Name = name,
                        DataType = shape.type, HmiDataType = Link(tag, "HmiDataType"), Dimensions = shape.dimensions, Connection = Unquote(Link(tag, "Connection")),
                        ControllerSymbol = Unquote(Link(tag, "ControllerTag")), LogicalAddress = Scalar(tag.Element("AttributeList"), "LogicalAddress"),
                        AddressMode = Scalar(tag.Element("AttributeList"), "AddressAccessMode"),
                        Multiplexed = Scalar(tag.Element("AttributeList"), "UseMultiplexing").Equals("true", StringComparison.OrdinalIgnoreCase),
                        AliasTag = Unquote(Link(tag, "Tag")), Issues = issues.ToArray() };
                    item.Id = TextListDocument.Hash(item.HmiId + "\n" + item.ResourcePath + "\n" + item.XPath);
                    tags.Add(item); tagNodes.Add(tag, item);
                }
                foreach (var node in nodes.Where(n => new[] { "Code", "Expression", "Formula" }.Contains(n.Name.LocalName) && !string.IsNullOrWhiteSpace(n.Value)))
                    diagnostics.Add(new AnalysisDiagnostic { Code = "opaqueExpression", HmiId = supplied.HmiId, ResourcePath = supplied.Path, XPath = resource.Paths[node],
                        Message = "Code/expression text may contain additional references; it is not executed or parsed into definite links." });
            }
            if (resources.GroupBy(r => r.Input.HmiId + "\n" + r.Input.Path, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Resource HMI/path pairs must be unique.");
            if (tags.Count > 50000) throw new ArgumentException("At most 50000 HMI tags can be analyzed.");
            var bindings = HmiBindingValidator.Validate(tags.ToArray(), controllers, input.ConnectionMappings);
            var bindingById = bindings.ToDictionary(b => b.TagId, StringComparer.Ordinal);
            var lookup = new TagLookup(tags);
            foreach (var resource in resources)
            {
                var nodes = resource.Paths.Keys;
                foreach (var link in nodes.Where(n => n.Parent?.Name == "LinkList" && new[] { "Tag", "ControllerTag", "Connection", "Screen" }.Contains(n.Name.LocalName)))
                {
                    if (references.Count >= 100000) throw new ArgumentException("At most 100000 static links can be analyzed.");
                    var ancestors = link.Ancestors().Where(n => n.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)).ToArray();
                    var owner = ancestors.First(); var control = ancestors.FirstOrDefault(n => (string?)n.Attribute("CompositionName") == "ScreenItems");
                    var row = new HmiReferenceOccurrence { HmiId = resource.Input.HmiId, ResourcePath = resource.Input.Path, ResourceName = ObjectName(resource.Root),
                        ResourceType = resource.Root.Name.LocalName, XPath = resource.Paths[link], LinkType = link.Name.LocalName, ReferenceText = Scalar(link, "Name"),
                        OwnerXPath = resource.Paths[owner], OwnerType = owner.Name.LocalName, OwnerName = ObjectName(owner),
                        ControlXPath = control == null ? "" : resource.Paths[control], ControlName = ObjectName(control),
                        Context = ancestors.Reverse().Select(n => n.Name.LocalName + ":" + ObjectName(n)).ToArray() };
                    row.Id = TextListDocument.Hash(row.HmiId + "\n" + row.ResourcePath + "\n" + row.XPath);
                    string targetId = (string?)link.Attribute("TargetID") ?? "";
                    HmiTagDeclaration? tag = null; string suffix = "";
                    if (targetId != "@OpenLink")
                    {
                        if (resource.Ids.TryGetValue(NativeXmlIds.TargetKey(targetId), out var target) && tagNodes.TryGetValue(target, out var known)) tag = known;
                        else row.Issues = new[] { "Native-ID link target is unavailable or requires another reference adapter." };
                    }
                    else if (link.Elements().Count() != 1 || link.Element("Name") == null || link.Element("Name")!.HasElements)
                        row.Issues = new[] { "Symbolic link requires one scalar Name." };
                    else if (row.LinkType == "Tag")
                    {
                        var candidates = lookup.Find(row.HmiId, Unquote(row.ReferenceText), out suffix);
                        if (candidates.Length == 1) tag = candidates[0];
                        else row.Issues = new[] { candidates.Length == 0 ? "HMI tag declaration is missing." : "HMI tag name is ambiguous." };
                    }
                    else if (new[] { "ControllerTag", "Connection" }.Contains(row.LinkType) && tagNodes.TryGetValue(owner, out var owningTag))
                        tag = owningTag;
                    else if (row.LinkType == "Connection")
                    {
                        row.Connection = Unquote(row.ReferenceText); row.Status = "connectionReference";
                    }
                    else row.Issues = new[] { "Direct " + row.LinkType + " links require an owning HMI tag or another adapter." };
                    if (tag != null)
                    {
                        var binding = bindingById[tag.Id]; row.TagId = tag.Id; row.Connection = binding.Connection; row.ControllerId = binding.ControllerId;
                        row.ControllerAddress = binding.ControllerAddress; row.Status = binding.Status; row.Issues = binding.Issues; row.Resolution = binding.Resolution;
                        if (suffix.Length != 0)
                        {
                            if (!IndicesFit(suffix, tag.Dimensions)) { row.Status = "hmiArrayBounds"; row.Issues = new[] { "HMI array element/rank is outside declared bounds or uses unsupported syntax." }; }
                            else if (binding.ControllerId.Length != 0 && binding.Status == "valid")
                            {
                                row.ControllerAddress += suffix;
                                var mapping = HmiBindingValidator.Mapping(input.ConnectionMappings, tag.HmiId, tag.Connection);
                                row.Resolution = new ControllerSymbolResolver(controllers.Single(c => Names.Equals(c.Id, binding.ControllerId))).Resolve(row.ControllerAddress, mapping?.DefaultScope ?? "");
                                if (row.Resolution.Status != "resolved") { row.Status = row.Resolution.Status; row.Issues = new[] { row.Resolution.Reason }; }
                            }
                        }
                        if (ancestors.Any(n => Scalar(n.Element("AttributeList"), "Indirect").Equals("true", StringComparison.OrdinalIgnoreCase)))
                        { row.Status = "indirect"; row.Issues = row.Issues.Concat(new[] { "Indirect selection requires runtime data; the static base tag is only a possible dependency." }).ToArray(); }
                        row.Chain = new[] { row.ResourceType + ":" + row.ResourceName, row.ControlName, row.OwnerType + ":" + row.OwnerName,
                            "HMI:" + tag.Name, "Connection:" + row.Connection, "Controller:" + row.ControllerId + ":" + row.ControllerAddress }
                            .Where(s => s.Length != 0).Concat(row.Resolution?.AliasChain.Select(a => "Alias:" + a) ?? Enumerable.Empty<string>()).ToArray();
                    }
                    references.Add(row);
                }
            }
            foreach (var group in tags.GroupBy(t => t.HmiId + "\n" + t.Name, Names).Where(g => g.Count() > 1))
                foreach (var tag in group) diagnostics.Add(new AnalysisDiagnostic { Code = "ambiguousTag", HmiId = tag.HmiId, ResourcePath = tag.ResourcePath, XPath = tag.XPath, Message = "Duplicate HMI tag qualified name." });
            var sources = resources.Select(r => (object)new { r.Input.HmiId, r.Input.Path, sha256 = TextListDocument.Hash(r.Input.Xml), nativeType = r.Root.Name.LocalName }).ToArray();
            string hash = TextListDocument.Hash(JsonSerializer.Serialize(new { sources, controllers = controllers.Select(c => new { c.Id, c.SourceSha256 }), input.ConnectionMappings,
                input.ExportReportedComplete, input.ExportFailures, input.UnsupportedResources }, JsonDefaults.Options));
            return new HmiAnalysisReport { AnalysisSha256 = hash, Sources = sources, Controllers = controllers, Tags = tags.ToArray(), Bindings = bindings, References = references.ToArray(),
                Diagnostics = diagnostics.ToArray(), ExportReportedComplete = input.ExportReportedComplete, ExportFailures = input.ExportFailures, UnsupportedResources = input.UnsupportedResources };
        }
        internal static HmiTagDeclaration[] FindTags(IEnumerable<HmiTagDeclaration> tags, string hmi, string reference, out string suffix)
            => new TagLookup(tags).Find(hmi, reference, out suffix);
        private sealed class TagLookup
        {
            private readonly Dictionary<string, HmiTagDeclaration[]> full, shortNames;
            public TagLookup(IEnumerable<HmiTagDeclaration> tags)
            {
                full = tags.GroupBy(t => t.HmiId + "\n" + t.Name, Names).ToDictionary(g => g.Key, g => g.ToArray(), Names);
                shortNames = tags.GroupBy(t => t.HmiId + "\n" + t.Name.Split('\\').Last(), Names).ToDictionary(g => g.Key, g => g.ToArray(), Names);
            }
            public HmiTagDeclaration[] Find(string hmi, string reference, out string suffix)
            {
            var direct = full.TryGetValue(hmi + "\n" + reference, out var literal) ? literal : Array.Empty<HmiTagDeclaration>(); suffix = "";
            if (direct.Length != 0) return direct;
            var array = Regex.Match(reference, @"^(.+?)(\[[^\]]+\])$", RegexOptions.CultureInvariant);
            string name = array.Success ? array.Groups[1].Value : reference;
            if (array.Success) suffix = array.Groups[2].Value;
            var exact = full.TryGetValue(hmi + "\n" + name, out var matches) ? matches : Array.Empty<HmiTagDeclaration>();
            if (exact.Length != 0) return exact;
            // Native short names are accepted only when unique within this exact HMI.
            return name.Contains("\\") || !shortNames.TryGetValue(hmi + "\n" + name, out var shortMatches) ? Array.Empty<HmiTagDeclaration>() : shortMatches;
            }
        }
        private static bool IndicesFit(string suffix, DeclarationDimension[] dimensions)
        {
            var values = suffix.Substring(1, suffix.Length - 2).Split(',');
            if (values.Length != dimensions.Length) return false;
            for (int i = 0; i < values.Length; i++)
                if (!int.TryParse(values[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int index) || index < dimensions[i].Lower || index > dimensions[i].Upper) return false;
            return true;
        }
    }
}
