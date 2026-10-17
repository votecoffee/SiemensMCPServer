using System;
using System.Linq;
using System.IO;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.ExternalSources;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession
    {
        private SoftwareReference FindEngineeringReference(string targetId)
        {
            CheckProject();
            return SoftwareCatalog.Discover(project!).SingleOrDefault(s => s.Id == targetId)
                ?? throw new InvalidOperationException("Engineering target ID is not present in the attached project.");
        }
        private SoftwareReference FindPlcReference(string plcId)
        {
            var reference = FindEngineeringReference(plcId);
            if (!(reference.Software is PlcSoftware)) throw new NotSupportedException("This operation requires a PLC software target. Use plc_list_targets.");
            return reference;
        }
        private static string? OnlineStateOf(SoftwareReference reference)
        {
            try { return reference.Owner.GetService<OnlineProvider>()?.State.ToString(); }
            catch { return null; }
        }
        private void RequireOfflineIfPlc(string targetId)
        {
            var reference = FindEngineeringReference(targetId);
            if (reference.Family == "plc") PlcPolicy.RequireOffline(OnlineStateOf(reference));
        }
        public object ListPlcs()
        {
            CheckProject();
            return new { plcs = SoftwareCatalog.Discover(project!).Where(s => s.Family == "plc").Select(s => new {
                plcId = s.Id, label = s.Label, name = s.Software.Name, family = s.Family, onlineState = OnlineStateOf(s),
                scope = "Offline blocks, PLC data types, tag tables/groups and external sources; no download, forcing or runtime control."
            }).ToArray() };
        }
        public object ReadPlcObject(string plcId, string path) { FindPlcReference(plcId); return ReadEngineeringObject(plcId, path); }
        public object ExportPlcObjects(string plcId, string path) { FindPlcReference(plcId); return ExportEngineeringObject(plcId, path); }
        public object ReadPlcResource(string plcId, string path)
        {
            FindPlcReference(plcId); RequireOfflineIfPlc(plcId);
            using (var exclusive = portal!.ExclusiveAccess("Codex PLC XML read"))
            {
                CheckProject(); RequireOfflineIfPlc(plcId);
                object node = ResolveEngineering(plcId, path);
                if (TransferNative.ExportMethod(node)?.GetParameters()[0].ParameterType != typeof(FileInfo)) throw new NotSupportedException("Select one native XML-exportable PLC block, data type or tag table.");
                string directory = Path.Combine(artifactRoot, "plc-xml-exports", Guid.NewGuid().ToString("N"));
                TransferNative.Export(node, directory);
                string file = Directory.GetFiles(directory, "*.xml").Single();
                string xml = File.ReadAllText(file, Encoding.UTF8);
                if (xml.Length > 16 * 1024 * 1024) throw new InvalidOperationException("PLC XML exceeds the read limit; use exported files.");
                return new { plcId, path, xml, fingerprint = TransferFiles.SemanticHash(directory), artifactDirectory = directory, files = TransferFiles.Inventory(directory),
                    scope = "Native XML representation; protected blocks can expose only their public interface." };
            }
        }
        public object PreviewPlcXmlImport(string plcId, string path, string fingerprint, string candidateXml, bool overwrite)
        {
            FindPlcReference(plcId);
            if (candidateXml == null || candidateXml.Length > 16 * 1024 * 1024) throw new ArgumentException("Supply bounded native PLC XML.");
            var document = TransferFiles.Xml(candidateXml);
            if (document.Root?.Name != "Document" || !document.Root.Elements().Any(e => e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal)))
                throw new ArgumentException("Expected native PLC SimaticML Document with SW.* resource content.");
            string directory = Path.Combine(artifactRoot, "plc-candidates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "payload.xml"), candidateXml, new UTF8Encoding(false));
            return PreviewPlc("import", plcId, path, fingerprint, null, "", null, directory, overwrite);
        }
        public object PreviewPlcSource(string plcId, string fingerprint, string name, string language, string code, bool allowBlockReplacement)
        {
            FindPlcReference(plcId);
            var requested = new PlcSourceInput { Name = name, Language = language, Code = code, AllowBlockReplacement = allowBlockReplacement };
            requested.Validate();
            var reply = features.Execute("plc", "prepare_source", new { arguments = new { name, language, code, allowBlockReplacement } });
            var source = SiemensMCPServer.Extensions.ExtensionWire.Parse<PlcSourceInput>(reply.GetProperty("source"));
            requested.RequireExactCandidate(source);
            string directory = Path.Combine(artifactRoot, "plc-source-candidates", Guid.NewGuid().ToString("N"));
            PlcSourceFiles.Write(directory, source);
            return PreviewPlc("source", plcId, "", fingerprint, null, "", null, directory, true);
        }
        private object ValidatePlcSource(EngineeringPlan plan, string directory)
        {
            var reference = FindPlcReference(plan.Before.TargetId); RequireOfflineIfPlc(reference.Id);
            if (plan.Before.Path != "") throw new ArgumentException("Source generation requires the PLC root fingerprint.");
            var source = PlcSourceFiles.Read(directory);
            var software = (PlcSoftware)reference.Software;
            if (software.ExternalSourceGroup.ExternalSources.Find(source.Name) != null) throw new InvalidOperationException("An external source with this name already exists; choose a new name.");
            return new { source.Name, source.Language, source.Code, source.AllowBlockReplacement, nativeScope = "Generate declared blocks/types in the PLC's default groups; retain the new external source. Compile the PLC separately." };
        }
        private object GeneratePlcSource(EngineeringPlan plan)
        {
            string directory = Path.Combine(plan.Directory, "candidate-files");
            ValidatePlcSource(plan, directory);
            var source = PlcSourceFiles.Read(directory);
            var software = (PlcSoftware)FindPlcReference(plan.Before.TargetId).Software;
            var native = software.ExternalSourceGroup.ExternalSources.CreateFromFile(source.Name, Path.Combine(directory, source.FileName));
            var generated = native.GenerateBlocksFromSource(GenerateBlockOption.None);
            if (generated == null || generated.Count == 0) throw new InvalidOperationException("Source generation produced no objects; transaction not committed.");
            return new { sourceName = native.Name, generated = generated.Select(g => new { nativeType = g.GetType().FullName,
                name = Convert.ToString(g.GetType().GetProperty("Name")?.GetValue(g, null)) }).ToArray(), projectSaved = false, compileRequired = true };
        }
        public object PreviewPlc(string action, string plcId, string path, string fingerprint, EngineeringEdit[]? edits,
            string nativeType, CreationArgument[]? arguments, string sourceDirectory, bool overwrite)
        {
            FindPlcReference(plcId); return PreviewEngineering(action, plcId, path, fingerprint, edits, nativeType, arguments, sourceDirectory, overwrite);
        }
        public object CompilePlc(string plcId)
        {
            var reference = FindPlcReference(plcId);
            using (var exclusive = portal!.ExclusiveAccess("Codex offline PLC compile"))
            {
                CheckProject(); RequireOfflineIfPlc(plcId);
                return CompileSoftware(plcId, reference.Software);
            }
        }
    }
}
