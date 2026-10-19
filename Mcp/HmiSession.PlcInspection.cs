using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession : IPlcInspectionBackend
    {
        public object ReadPlcExtension(string operation, string kind, JsonElement arguments)
            => new PlcInspectionWorkflow(this, features).Read(operation, kind, arguments);
        public PlcSourceSnapshot CapturePlcSource(string plcId, string path, bool withDependencies)
        {
            CheckProject();
            using (var exclusive = portal!.ExclusiveAccess("Codex offline PLC source export"))
            {
                var reference = FindPlcReference(plcId); PlcPolicy.RequireOffline(OnlineStateOf(reference));
                object node = EngineeringNative.Resolve(reference.Software, path);
                if (!(node is IGenerateSource source)) throw new NotSupportedException("This PLC object has no native readable-source export. Select an SCL/STL block, data block or PLC data type.");
                string language, extension;
                if (node is PlcType) { language = "UDT"; extension = ".udt"; }
                else if (node is DataBlock) { language = "DB"; extension = ".db"; }
                else if (node is PlcBlock block && (block.ProgrammingLanguage.ToString() == "SCL" || block.ProgrammingLanguage.ToString() == "STL"))
                { language = block.ProgrammingLanguage.ToString(); extension = language == "SCL" ? ".scl" : ".awl"; }
                else throw new NotSupportedException("Native readable-source export supports SCL/STL, data blocks and PLC types. LAD/FBD and other languages require native XML export; no source translation is synthesized.");
                string directory = Path.Combine(artifactRoot, "plc-readable-sources", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string fileName = "payload" + extension;
                ((PlcSoftware)reference.Software).ExternalSourceGroup.GenerateSource(new[] { source }, new FileInfo(Path.Combine(directory, fileName)),
                    withDependencies ? GenerateOptions.WithDependencies : GenerateOptions.None);
                var snapshot = PlcInspectionFiles.Read(directory, fileName);
                snapshot.PlcId = plcId; snapshot.Path = path; snapshot.NativeType = node.GetType().FullName!;
                snapshot.Language = language; snapshot.WithDependencies = withDependencies;
                return snapshot;
            }
        }
        public PlcReferenceSnapshot CapturePlcReferences(string plcId, string path, PlcReferenceCapture capture)
        {
            capture.Validate(); CheckProject();
            using (var exclusive = portal!.ExclusiveAccess("Codex offline PLC cross-reference read"))
            {
                var reference = FindPlcReference(plcId); PlcPolicy.RequireOffline(OnlineStateOf(reference));
                object node = EngineeringNative.Resolve(reference.Software, path);
                if (!PlcPolicy.AllowsResourceType(node.GetType().FullName ?? "") || !(node is IEngineeringServiceProvider provider))
                    throw new NotSupportedException("Select a PLC block, type or tag that exposes the native cross-reference service.");
                Type serviceType = PlcInspectionNative.CrossReferenceType ?? throw new NotSupportedException("The selected API does not expose native CrossReferenceService. V15.1 has no route; the inspected V19 profile includes the service. No XML approximation is substituted.");
                var getService = typeof(IEngineeringServiceProvider).GetMethods().Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                object service = getService.MakeGenericMethod(serviceType).Invoke(provider, null)
                    ?? throw new NotSupportedException("The selected PLC object does not expose native CrossReferenceService.");
                var method = serviceType.GetMethods().Single(m => m.Name == "GetCrossReferences" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                object filter = Enum.Parse(method.GetParameters()[0].ParameterType, capture.Filter, false);
                object result = method.Invoke(service, new[] { filter }) ?? throw new InvalidOperationException("Native cross-reference service returned no result.");
                var snapshot = PlcReferenceReader.Read(result, capture.MaxRows);
                snapshot.PlcId = plcId; snapshot.Path = path; snapshot.Filter = capture.Filter;
                return snapshot;
            }
        }
    }
    internal static class PlcInspectionNative
    {
        internal static Type? CrossReferenceType => new[] { typeof(Project).Assembly, typeof(PlcSoftware).Assembly }.Distinct()
            .Select(a => a.GetType("Siemens.Engineering.CrossReference.CrossReferenceService", false)).FirstOrDefault(t => t != null);
        internal static object Capabilities() => new { readableSourceApiPresent = typeof(PlcExternalSourceSystemGroup).GetMethods().Any(m => m.Name == "GenerateSource" && m.GetParameters().Length == 3),
            nativeCrossReferenceApiPresent = CrossReferenceType != null, sourceLanguages = new[] { "SCL", "STL", "DB", "UDT" },
            verifiedOfflineRequired = true, nativeAcceptance = "pending", scope = "Disk-only native source export and native cross-reference snapshots; no import, block generation, compile, save or controller access." };
    }
}
