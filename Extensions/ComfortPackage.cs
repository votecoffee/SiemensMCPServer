using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ComfortPackageSpec
    {
        [JsonRequired] public ComfortResourceSpec[] Resources { get; set; } = Array.Empty<ComfortResourceSpec>();
        [JsonRequired] public ComfortPlcSymbol[] PlcSymbols { get; set; } = Array.Empty<ComfortPlcSymbol>();
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ComfortResourceSpec
    {
        [JsonRequired] public string Kind { get; set; } = "";
        [JsonRequired] public string TemplateXml { get; set; } = "";
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public bool KeepTemplateMembers { get; set; }
        public ComfortMemberSpec[] Members { get; set; } = Array.Empty<ComfortMemberSpec>();
        public ClassicXmlEdit[] Edits { get; set; } = Array.Empty<ClassicXmlEdit>();
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ComfortMemberSpec
    {
        [JsonRequired] public string Prototype { get; set; } = "";
        [JsonRequired] public string Name { get; set; } = "";
        public ClassicXmlEdit[] Edits { get; set; } = Array.Empty<ClassicXmlEdit>();
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ComfortPlcSymbol
    {
        [JsonRequired] public string Connection { get; set; } = "";
        [JsonRequired] public string Symbol { get; set; } = "";
        [JsonRequired] public string DataType { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ComfortBindingInput
    {
        [JsonRequired] public string[] ResourceXml { get; set; } = Array.Empty<string>();
        [JsonRequired] public ComfortPlcSymbol[] PlcSymbols { get; set; } = Array.Empty<ComfortPlcSymbol>();
    }
    internal sealed class ComfortBindingReport
    {
        public bool Valid => Errors.Length == 0;
        public string[] Errors { get; set; } = Array.Empty<string>();
        public int TagCount { get; set; }
        public int ScreenTagReferences { get; set; }
        public int PlcBindings { get; set; }
        public bool NativeBindingVerified => false;
        public string Scope => "Offline comparison with supplied PLC connection/symbol/type declarations. Native connection partners, compilation and target acceptance require separate checks.";
    }
    internal sealed class ComfortPackageFile
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    internal sealed class ComfortPackageResult
    {
        public string Format => "tia-comfort-package-v1";
        public ComfortPackageFile[] Files { get; set; } = Array.Empty<ComfortPackageFile>();
        public ComfortBindingReport Validation { get; set; } = new ComfortBindingReport();
        public string Sha256 => TextListDocument.Hash(string.Join("\n", Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Path + ":" + f.Sha256)));
        public bool NativeImportAccepted => false;
        public string NextStep => "Write the reviewed files with Write-ComfortPackage.ps1, then use hmi_preview_import_bundle and hmi_apply_object_plan. Compile/save remain separate.";
    }

    // Native export prototypes supply device/version-specific XML. This worker creates no
    // Siemens handles, invents no native control schemas, and performs no filesystem writes.
    internal static class ComfortPackage
    {
        private const int MaxResources = 50;
        private const int MaxPayload = 4 * 1024 * 1024;
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        private static object SymbolSchema => new { type = "array", maxItems = 10000, items = new { type = "object", additionalProperties = false, required = new[] { "connection", "symbol", "dataType" }, properties = new {
            connection = new { type = "string", description = "Exact exported Connection link name." }, symbol = new { type = "string", description = "Exact native ControllerTag symbol spelling within that connection." }, dataType = new { type = "string", description = "PLC type; this bounded adapter requires the same HMI scalar type." }
        } } };
        internal static object Schema(bool build)
        {
            if (!build) return new { type = "object", additionalProperties = false, required = new[] { "resourceXml", "plcSymbols" }, properties = new {
                resourceXml = new { type = "array", minItems = 1, maxItems = 50, items = new { type = "string" } }, plcSymbols = SymbolSchema } };
            var edits = new { type = "array", maxItems = 1000, items = new { type = "object", required = new[] { "xPath", "expectedValue", "newValue" }, properties = new {
                xPath = new { type = "string", description = "Exact scalar selector within a Document-wrapped native prototype; export IDs cannot be edited." }, expectedValue = new { type = "string" }, newValue = new { type = "string" } } } };
            return new { type = "object", additionalProperties = false, required = new[] { "resources", "plcSymbols" }, properties = new { plcSymbols = SymbolSchema,
                resources = new { type = "array", minItems = 1, maxItems = 50, items = new { type = "object", additionalProperties = false, required = new[] { "kind", "templateXml", "name", "keepTemplateMembers" }, properties = new {
                    kind = new { type = "string", @enum = new[] { "screen", "tagTable" } }, templateXml = new { type = "string", description = "Complete native exported Document, retaining Engineering/version. Sidecar-free templates only." }, name = new { type = "string" },
                    keepTemplateMembers = new { type = "boolean", description = "Explicitly keep or replace the source controls/tags. Native layers and other template fields remain." }, edits,
                    members = new { type = "array", maxItems = 500, items = new { type = "object", additionalProperties = false, required = new[] { "prototype", "name" }, properties = new {
                        prototype = new { type = "string", description = "Exact existing ScreenItems/Tags member name in the source template." }, name = new { type = "string" }, edits } } }
                } } }
            } };
        }
        private static string NativeType(string kind) => kind == "screen" ? "Hmi.Screen.Screen"
            : kind == "tagTable" ? "Hmi.Tag.TagTable" : throw new ArgumentException("Resource kind must be screen or tagTable.");
        private static void Name(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) throw new ArgumentException("Use nonempty, bounded native names.");
        }
        private static string ObjectName(XElement element) => ClassicXmlDocument.ObjectNameField(element)?.Value ?? "";
        private static XDocument Parse(string xml)
        {
            if (xml == null || xml.Length > MaxPayload) throw new ArgumentException("Supply bounded native XML.");
            var doc = TransferFiles.Xml(xml);
            if (doc.Root?.Name != "Document") throw new ArgumentException("Expected a native Document.");
            var resources = doc.Root.Elements().Where(e => e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)).ToArray();
            if (resources.Length != 1 || resources[0].Name.NamespaceName.Length != 0
                || resources[0].Name.LocalName != "Hmi.Screen.Screen" && resources[0].Name.LocalName != "Hmi.Tag.TagTable")
                throw new ArgumentException("Supply exactly one native classic screen or tag table per Document.");
            if (doc.Descendants().Attributes().Any(a => a.Name.LocalName == "UId")) throw new NotSupportedException("UId reference formats require a separate adapter.");
            if (doc.Descendants().Any(e => string.Equals((string?)e.Attribute("external"), "path", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("Package generation currently requires sidecar-free templates. Use the native resource transfer workflow for external files.");
            NativeXmlIds.Normalize(new XDocument(doc));
            NativeXmlIds.VerifyReferences(resources[0], doc);
            Name(ObjectName(resources[0]));
            return doc;
        }
        private static XElement Resource(XDocument doc) => doc.Root!.Elements().Single(e => e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal));
        private static IEnumerable<XElement> Members(XElement resource) => resource.Descendants().Where(e => e.Parent?.Name == "ObjectList"
            && (resource.Name.LocalName == "Hmi.Tag.TagTable" ? e.Name == "Hmi.Tag.Tag" && (string?)e.Attribute("CompositionName") == "Tags"
                : e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal) && (string?)e.Attribute("CompositionName") == "ScreenItems"));
        private static void UniqueMembers(XElement resource)
        {
            var members = Members(resource).ToArray();
            foreach (var member in members) Name(ObjectName(member));
            if (members.GroupBy(ObjectName, Names).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate native member names.");
        }
        private static XElement EditMember(XElement member, ClassicXmlEdit[] edits)
        {
            if (edits == null) throw new ArgumentException("Member edits cannot be null.");
            if (edits.Length == 0) return member;
            string wrapper = new XDocument(new XElement("Document", member)).ToString(SaveOptions.DisableFormatting);
            return TransferFiles.Xml(new ClassicXmlDocument(wrapper).Edit(edits)).Root!.Elements().Single();
        }
        public static ComfortPackageResult Build(ComfortPackageSpec spec)
        {
            if (spec.Resources == null || spec.Resources.Length == 0 || spec.Resources.Length > MaxResources) throw new ArgumentException("Supply 1 to 50 resources.");
            var outputs = new List<XDocument>(); string? version = null; long workingBytes = 0;
            foreach (var requested in spec.Resources)
            {
                if (requested == null || requested.Members == null || requested.Members.Length > 500 || requested.Edits == null) throw new ArgumentException("Missing or excessive resource members.");
                Name(requested.Name);
                if (requested.TemplateXml == null || (workingBytes += Encoding.UTF8.GetByteCount(requested.TemplateXml)) > MaxPayload) throw new ArgumentException("Template/clone input exceeds 4 MiB.");
                var document = Parse(requested.TemplateXml); var resource = Resource(document);
                if (resource.Name.LocalName != NativeType(requested.Kind)) throw new ArgumentException("Template type differs from resource kind.");
                string currentVersion = (string?)document.Root!.Element("Engineering")?.Attribute("version") ?? throw new ArgumentException("Template must retain its native Engineering/version header.");
                if (version != null && currentVersion != version) throw new ArgumentException("All templates must come from the same Openness API version.");
                version = currentVersion; UniqueMembers(resource);
                var originals = Members(resource).ToArray();
                foreach (var member in requested.Members)
                {
                    if (member == null) throw new ArgumentException("Member cannot be null.");
                    Name(member.Name); Name(member.Prototype);
                    var prototype = originals.SingleOrDefault(e => Names.Equals(ObjectName(e), member.Prototype)) ?? throw new ArgumentException("Native prototype was not found: " + member.Prototype);
                    var copy = NativeXmlIds.Clone(prototype, document);
                    ClassicXmlDocument.ObjectNameField(copy)!.Value = member.Name;
                    copy = EditMember(copy, member.Edits);
                    if ((workingBytes += Encoding.UTF8.GetByteCount(copy.ToString(SaveOptions.DisableFormatting))) > MaxPayload) throw new ArgumentException("Template/clone input exceeds 4 MiB.");
                    prototype.Parent!.Add(copy);
                }
                if (!requested.KeepTemplateMembers)
                {
                    // Nested groups are not flattened or reconstructed by this adapter.
                    if (originals.Any(e => e.Ancestors().Any(a => originals.Contains(a)))) throw new NotSupportedException("Replacing nested control groups requires a reviewed native import.");
                    foreach (var original in originals) original.Remove();
                }
                resource.Element("AttributeList")!.Element("Name")!.Value = requested.Name;
                if (requested.Edits.Length != 0) document = TransferFiles.Xml(new ClassicXmlDocument(document.ToString(SaveOptions.DisableFormatting)).Edit(requested.Edits));
                resource = Resource(document); UniqueMembers(resource);
                if (ObjectName(resource) != requested.Name) throw new ArgumentException("Resource edits cannot alter the requested name.");
                Parse(document.ToString(SaveOptions.DisableFormatting));
                outputs.Add(document);
            }
            if (outputs.GroupBy(d => Resource(d).Name + "\n" + ObjectName(Resource(d)), Names).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate package resource names.");
            var numbers = outputs.Select(Resource).Where(r => r.Name.LocalName == "Hmi.Screen.Screen").Select(r => r.Element("AttributeList")?.Element("Number")?.Value).Where(n => n != null).ToArray();
            if (numbers.Any(n => !uint.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out _)) || numbers.GroupBy(n => uint.Parse(n!, CultureInfo.InvariantCulture)).Any(g => g.Count() != 1))
                throw new ArgumentException("Screen numbers must be unsigned integers and unique inside the package. Existing project number collisions require native preview/import validation.");
            var validation = ValidateBindings(new ComfortBindingInput { ResourceXml = outputs.Select(d => d.ToString(SaveOptions.DisableFormatting)).ToArray(), PlcSymbols = spec.PlcSymbols });
            if (!validation.Valid) throw new ArgumentException("Package binding validation failed: " + string.Join("; ", validation.Errors));
            var files = new List<ComfortPackageFile>(); var entries = new List<object>();
            // Tag tables precede screens both in the manifest and the native bundle importer.
            foreach (var doc in outputs.OrderBy(d => Resource(d).Name.LocalName == "Hmi.Tag.TagTable" ? 0 : 1))
            {
                var resource = Resource(doc); bool table = resource.Name.LocalName == "Hmi.Tag.TagTable";
                string destination = table ? "p:TagFolder/p:TagTables" : "p:ScreenFolder/p:Screens";
                string directory = "objects/" + entries.Count.ToString("D5", CultureInfo.InvariantCulture) + "/native";
                entries.Add(new { path = destination + "/n:" + Uri.EscapeDataString(ObjectName(resource)), importPath = destination, sourceDirectory = directory, nativeType = "Siemens.Engineering." + resource.Name.LocalName });
                files.Add(File(directory + "/payload.xml", doc.ToString(SaveOptions.DisableFormatting)));
            }
            files.Add(File("export-index.json", JsonSerializer.Serialize(new { format = "tia-engineering-export-v1", family = "classic", entries, failures = Array.Empty<string>() }, JsonDefaults.Options)));
            files.Add(File("package-report.json", JsonSerializer.Serialize(new { version, validation, nativeImportAccepted = false, scope = "Sidecar-free classic screen/tag prototypes. Root destination collections; existing template fields and native metadata retained." }, JsonDefaults.Options)));
            if (files.Sum(f => Encoding.UTF8.GetByteCount(f.Content)) > MaxPayload) throw new ArgumentException("Generated package exceeds 4 MiB.");
            return new ComfortPackageResult { Files = files.ToArray(), Validation = validation };
        }
        private static ComfortPackageFile File(string path, string text) => new ComfortPackageFile { Path = path, Content = text, Sha256 = TextListDocument.Hash(text) };
        private static string Link(XElement element, string name)
        {
            var links = element.Element("LinkList")?.Elements(name).ToArray() ?? Array.Empty<XElement>();
            if (links.Length == 0) return "";
            if (links.Length != 1 || (string?)links[0].Attribute("TargetID") != "@OpenLink" || links[0].Elements().Count() != 1 || links[0].Element("Name") == null)
                throw new ArgumentException("Unsupported or ambiguous native " + name + " link.");
            return links[0].Element("Name")!.Value;
        }
        private static string TagReference(string value) => value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2).Replace("\"\"", "\"") : value;
        public static ComfortBindingReport ValidateBindings(ComfortBindingInput input)
        {
            if (input.ResourceXml == null || input.ResourceXml.Length == 0 || input.ResourceXml.Length > MaxResources || input.PlcSymbols == null || input.PlcSymbols.Length > 10000)
                throw new ArgumentException("Supply bounded resources and PLC symbol declarations.");
            if (input.ResourceXml.Any(x => x == null) || input.ResourceXml.Sum(x => (long)Encoding.UTF8.GetByteCount(x)) > MaxPayload) throw new ArgumentException("Resource XML exceeds 4 MiB.");
            foreach (var symbol in input.PlcSymbols)
            {
                if (symbol == null) throw new ArgumentException("PLC symbol cannot be null.");
                Name(symbol.Connection); Name(symbol.Symbol); Name(symbol.DataType);
            }
            var symbols = input.PlcSymbols.GroupBy(s => s.Connection + "\n" + s.Symbol, Names).ToArray();
            if (symbols.Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate PLC connection/symbol declarations.");
            var errors = new List<string>(); var tags = new Dictionary<string, XElement>(Names); int references = 0, bindings = 0;
            var resources = input.ResourceXml.Select(Parse).Select(Resource).ToArray();
            foreach (var table in resources.Where(r => r.Name.LocalName == "Hmi.Tag.TagTable"))
            {
                foreach (var tag in Members(table))
                {
                    string name = ObjectName(tag); Name(name);
                    if (tags.ContainsKey(name)) { errors.Add("Ambiguous HMI tag name: " + name); continue; }
                    tags.Add(name, tag);
                    string connection = Link(tag, "Connection"), controller = Link(tag, "ControllerTag");
                    if ((connection.Length == 0) != (controller.Length == 0)) { errors.Add("Incomplete PLC binding for HMI tag: " + name); continue; }
                    if (connection.Length == 0) continue;
                    bindings++;
                    var symbol = input.PlcSymbols.SingleOrDefault(s => Names.Equals(s.Connection, connection) && Names.Equals(s.Symbol, controller));
                    if (symbol == null) { errors.Add("PLC symbol is absent from the supplied connection manifest: " + name); continue; }
                    string dataType = Link(tag, "DataType"), hmiType = Link(tag, "HmiDataType");
                    if (dataType.Length == 0 || !Names.Equals(dataType, symbol.DataType) || hmiType.Length != 0 && !Names.Equals(hmiType, dataType))
                        errors.Add("PLC/HMI data type differs or is unavailable: " + name);
                    if (tag.Element("AttributeList")?.Element("LogicalAddress")?.Value.Length > 0)
                        errors.Add("Symbolic PLC binding also contains an absolute address: " + name);
                }
            }
            foreach (var screen in resources.Where(r => r.Name.LocalName == "Hmi.Screen.Screen"))
                foreach (var link in screen.Descendants("LinkList").Elements("Tag"))
                {
                    references++;
                    if ((string?)link.Attribute("TargetID") != "@OpenLink" || link.Elements().Count() != 1 || link.Element("Name") == null)
                    { errors.Add("Unsupported screen tag reference in " + ObjectName(screen)); continue; }
                    string name = TagReference(link.Element("Name")!.Value);
                    if (!tags.ContainsKey(name)) errors.Add("Screen references an HMI tag absent from the package: " + name);
                }
            return new ComfortBindingReport { Errors = errors.Distinct(StringComparer.Ordinal).ToArray(), TagCount = tags.Count, ScreenTagReferences = references, PlcBindings = bindings };
        }
    }
}
