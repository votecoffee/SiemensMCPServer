using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Analysis
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ControllerDocumentInput
    {
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public string Format { get; set; } = "";
        [JsonRequired] public string Content { get; set; } = "";
    }
    internal sealed class DeclarationDimension
    {
        public int Lower { get; set; }
        public int Upper { get; set; }
    }
    internal sealed class ControllerMember
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public DeclarationDimension[] Dimensions { get; set; } = Array.Empty<DeclarationDimension>();
        public string ExternalAccess { get; set; } = "";
        public bool Hidden { get; set; }
    }
    internal sealed class ControllerSymbol
    {
        public string Name { get; set; } = "";
        public string Scope { get; set; } = "";
        public string DataType { get; set; } = "";
        public DeclarationDimension[] Dimensions { get; set; } = Array.Empty<DeclarationDimension>();
        public string AliasFor { get; set; } = "";
        public string Address { get; set; } = "";
        public string ExternalAccess { get; set; } = "";
        public string QualifiedName => Scope.Length == 0 ? Name : Scope + "." + Name;
    }
    internal sealed class ControllerType
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "struct";
        public string BaseType { get; set; } = "";
        public DeclarationDimension[] Dimensions { get; set; } = Array.Empty<DeclarationDimension>();
        public ControllerMember[] Members { get; set; } = Array.Empty<ControllerMember>();
    }
    internal sealed class ControllerDeclaration
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Format { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string SoftwareRevision { get; set; } = "";
        public bool PartialExport { get; set; }
        public ControllerSymbol[] Symbols { get; set; } = Array.Empty<ControllerSymbol>();
        public ControllerType[] Types { get; set; } = Array.Empty<ControllerType>();
        public string[] Limitations { get; set; } = Array.Empty<string>();
    }
    internal static class ControllerDeclarations
    {
        internal static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        internal static string Name(string? value, string label = "name")
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > 1024 || value.Any(char.IsControl))
                throw new ArgumentException("A nonempty " + label + " up to 1024 characters is required.");
            return value;
        }
        private static string Attr(XElement node, string name) => (string?)node.Attribute(name) ?? "";
        private static XElement[] Children(XElement? node, string name) => node == null ? Array.Empty<XElement>() : node.Elements().Where(e => e.Name.LocalName == name && e.Name.Namespace == node.Name.Namespace).ToArray();
        private static XElement? Child(XElement? node, string name) => Children(node, name).SingleOrDefault();
        public static ControllerDeclaration Import(ControllerDocumentInput input)
        {
            Name(input.Id, "controller ID");
            if (input.Content == null || input.Content.Length == 0 || input.Content.Length > 64 * 1024 * 1024) throw new ArgumentException("Controller XML must be within 64 MiB of characters.");
            var document = TransferFiles.Xml(input.Content);
            var nodes = document.Descendants().Take(2000001).ToArray();
            if (nodes.Length > 2000000 || nodes.Any(e => e.Ancestors().Take(65).Count() > 64)) throw new ArgumentException("Controller XML exceeds element/depth limits.");
            ControllerDeclaration result = input.Format == "rockwell-l5x" ? Rockwell(document)
                : input.Format == "plcopen-xml" ? PlcOpen(document) : throw new ArgumentException("format must be rockwell-l5x or plcopen-xml.");
            result.Id = input.Id; result.Format = input.Format; result.SourceSha256 = TextListDocument.Hash(input.Content);
            if (result.Symbols.Length > 100000 || result.Types.Length > 10000 || result.Types.Sum(t => (long)t.Members.Length) > 100000)
                throw new ArgumentException("Controller declarations exceed 100000 symbols/members or 10000 types.");
            if (result.Symbols.GroupBy(s => s.QualifiedName, Names).Any(g => g.Count() != 1)
                || result.Types.GroupBy(t => t.Name, Names).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate or ambiguous controller declarations.");
            foreach (var type in result.Types)
                if (type.Members.GroupBy(m => m.Name, Names).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate controller type members.");
            return result;
        }
        private static DeclarationDimension[] RockwellDimensions(string value)
        {
            value = value.Trim();
            if (value.Length == 0 || value == "0") return Array.Empty<DeclarationDimension>();
            // Native L5X tags use space-separated ranks; accept comma-separated declaration exports too.
            var values = value.Contains(',') ? value.Split(',') : System.Text.RegularExpressions.Regex.Split(value, @"\s+");
            if (values.Length > 3) throw new ArgumentException("L5X arrays support at most three dimensions.");
            return values.Select(v => int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size > 0
                ? new DeclarationDimension { Lower = 0, Upper = size - 1 } : throw new ArgumentException("Invalid L5X array dimension.")).ToArray();
        }
        private static ControllerDeclaration Rockwell(XDocument document)
        {
            if (document.Root?.Name != "RSLogix5000Content") throw new ArgumentException("Expected an unnamespaced RSLogix5000Content L5X document.");
            var controller = document.Root.Elements("Controller").SingleOrDefault() ?? throw new ArgumentException("L5X must contain one Controller context.");
            var types = new List<ControllerType>(); var symbols = new List<ControllerSymbol>();
            foreach (var type in controller.Element("DataTypes")?.Elements("DataType") ?? Enumerable.Empty<XElement>())
                types.Add(new ControllerType {
                    Name = Name(Attr(type, "Name")),
                    Members = (type.Element("Members")?.Elements("Member") ?? Enumerable.Empty<XElement>()).Select(member => new ControllerMember {
                        Name = Name(Attr(member, "Name")), DataType = Attr(member, "DataType") == "BIT" ? "BOOL" : Name(Attr(member, "DataType"), "member type"),
                        Dimensions = RockwellDimensions(Attr(member, "Dimension")), Hidden = new[] { "true", "1" }.Contains(Attr(member, "Hidden"), Names),
                        ExternalAccess = Attr(member, "ExternalAccess") }).ToArray()
                });
            Action<XElement?, string> addTags = (tags, scope) => {
                foreach (var tag in tags?.Elements("Tag") ?? Enumerable.Empty<XElement>())
                {
                    string kind = Attr(tag, "TagType");
                    if (!new[] { "Base", "Alias", "Produce", "Produced", "Consumed", "Consume" }.Contains(kind, Names)) throw new ArgumentException("Unsupported L5X TagType: " + kind);
                    string alias = Attr(tag, "AliasFor"), dataType = Attr(tag, "DataType");
                    if (Names.Equals(kind, "Alias") && alias.Length == 0) throw new ArgumentException("Alias tags require AliasFor.");
                    if (!Names.Equals(kind, "Alias") && alias.Length != 0) throw new ArgumentException("Non-alias tags cannot supply AliasFor.");
                    if (dataType.Length == 0 && alias.Length == 0) throw new ArgumentException("Base tags require DataType.");
                    if (dataType.Length != 0) Name(dataType, "tag type");
                    if (alias.Length != 0) Name(alias, "alias target");
                    symbols.Add(new ControllerSymbol { Name = Name(Attr(tag, "Name")), Scope = scope, DataType = dataType,
                        Dimensions = RockwellDimensions(Attr(tag, "Dimensions")), AliasFor = alias, ExternalAccess = Attr(tag, "ExternalAccess") });
                }
            };
            addTags(controller.Element("Tags"), "");
            foreach (var program in controller.Element("Programs")?.Elements("Program") ?? Enumerable.Empty<XElement>())
                addTags(program.Element("Tags"), "Program:" + Name(Attr(program, "Name")));
            return new ControllerDeclaration { Name = Name(Attr(controller, "Name")), SoftwareRevision = Attr(document.Root, "SoftwareRevision"),
                PartialExport = !Names.Equals(Attr(document.Root, "TargetType"), "Controller"), Symbols = symbols.ToArray(), Types = types.ToArray(),
                Limitations = new[] { "Declaration-only L5X import; module/AOI/predefined structures absent from DataTypes remain unresolved. Logic, values and connection partners are not imported.",
                    "Rockwell symbolic addresses are returned as declared paths; physical offsets are not inferred." } };
        }
        private static ControllerDeclaration PlcOpen(XDocument document)
        {
            var root = document.Root;
            if (root?.Name.LocalName != "project" || !new[] { "http://www.plcopen.org/xml/tc6_0200", "http://www.plcopen.org/xml/tc6_0201" }.Contains(root.Name.NamespaceName))
                throw new ArgumentException("Expected a PLCopen TC6 2.0/2.01 project.");
            var types = new List<ControllerType>(); var symbols = new List<ControllerSymbol>(); var limitations = new List<string> {
                "PLCopen configuration/resource global variables and POU interface declarations only. POU instances are not expanded, and generic/enum/subrange/pointer/vendor types remain unresolved." };
            foreach (var type in Children(Child(Child(root, "types"), "dataTypes"), "dataType"))
            {
                var shape = ParsePlcType(Child(type, "baseType"), types, Name(Attr(type, "name")));
                if (!types.Any(t => Names.Equals(t.Name, Attr(type, "name"))))
                    types.Add(new ControllerType { Name = Name(Attr(type, "name")), Kind = shape.type.Length == 0 ? "unsupported" : "alias", BaseType = shape.type, Dimensions = shape.dimensions });
            }
            Action<XElement, string> variables = (container, scope) => {
                foreach (var variable in Children(container, "variable"))
                {
                    var shape = ParsePlcType(Child(variable, "type"), types, scope + "." + Name(Attr(variable, "name")));
                    symbols.Add(new ControllerSymbol { Name = Name(Attr(variable, "name")), Scope = scope, DataType = shape.type, Dimensions = shape.dimensions,
                        Address = Attr(variable, "address"), ExternalAccess = "" });
                }
            };
            foreach (var config in Children(Child(Child(root, "instances"), "configurations"), "configuration"))
            {
                string configName = Name(Attr(config, "name"));
                foreach (var vars in Children(config, "globalVars")) variables(vars, configName);
                foreach (var resource in Children(config, "resource"))
                    foreach (var vars in Children(resource, "globalVars")) variables(vars, configName + "." + Name(Attr(resource, "name")));
            }
            foreach (var pou in Children(Child(Child(root, "types"), "pous"), "pou"))
                foreach (var vars in Child(pou, "interface")?.Elements() ?? Enumerable.Empty<XElement>())
                    if (new[] { "inputVars", "outputVars", "inOutVars", "localVars", "globalVars" }.Contains(vars.Name.LocalName))
                        variables(vars, "POU:" + Name(Attr(pou, "name")));
            string projectName = Attr(Child(root, "contentHeader") ?? root, "name");
            return new ControllerDeclaration { Name = projectName.Length == 0 ? "PLCopen project" : Name(projectName), Symbols = symbols.ToArray(), Types = types.ToArray(),
                PartialExport = true, Limitations = limitations.ToArray() };
        }
        private static (string type, DeclarationDimension[] dimensions) ParsePlcType(XElement? wrapper, List<ControllerType> types, string anonymousName)
        {
            var shape = wrapper?.Elements().SingleOrDefault();
            if (shape == null) return ("", Array.Empty<DeclarationDimension>());
            string kind = shape.Name.LocalName;
            if (kind == "derived") return (Name(Attr(shape, "name"), "derived type"), Array.Empty<DeclarationDimension>());
            if (kind == "array")
            {
                var dimensions = Children(shape, "dimension").Select(d => {
                    if (!int.TryParse(Attr(d, "lower"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lower)
                        || !int.TryParse(Attr(d, "upper"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int upper) || lower > upper)
                        throw new ArgumentException("PLCopen array bounds must be integer literals.");
                    return new DeclarationDimension { Lower = lower, Upper = upper };
                }).ToArray();
                if (dimensions.Length == 0 || dimensions.Length > 8) throw new ArgumentException("PLCopen arrays require 1..8 dimensions.");
                var element = ParsePlcType(Child(shape, "baseType"), types, anonymousName + "$element");
                if (element.dimensions.Length != 0) return ("", dimensions); // Nested array types are not flattened.
                return (element.type, dimensions);
            }
            if (kind == "struct")
            {
                string typeName = anonymousName;
                var members = Children(shape, "variable").Select(v => {
                    var field = ParsePlcType(Child(v, "type"), types, typeName + "." + Name(Attr(v, "name")));
                    return new ControllerMember { Name = Name(Attr(v, "name")), DataType = field.type, Dimensions = field.dimensions };
                }).ToArray();
                types.Add(new ControllerType { Name = typeName, Members = members });
                return (typeName, Array.Empty<DeclarationDimension>());
            }
            string atomic = ControllerSymbolResolver.Atomic(kind);
            return (ControllerSymbolResolver.IsAtomic(atomic) ? atomic : "", Array.Empty<DeclarationDimension>());
        }
    }
}
