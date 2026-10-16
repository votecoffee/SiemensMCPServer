using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SiemensTiaOpenness.Extensions;

namespace SiemensTiaOpenness.Mcp
{
    internal sealed partial class HmiSession
    {
        private readonly ExtensionPlanStore extensionPlans = new ExtensionPlanStore();
        public object PreviewExtension(string module, string operation, string targetId, string path, string fingerprint, JsonElement arguments)
        {
            CheckProject();
            var manager = features as ExtensionManager ?? throw new InvalidOperationException("Extension manager is unavailable.");
            var descriptor = manager.Operation(module, operation);
            if (!descriptor.Preview || descriptor.Route == "local" || descriptor.Route == "internal") throw new ArgumentException("This operation has no broker preview route.");
            object preview;
            switch (descriptor.Route)
            {
                case "classicXml":
                    preview = classicXmlWorkflow!.PreviewTransformed(targetId, path, fingerprint,
                        xml => manager.Execute(module, operation, new { snapshot = xml, arguments }).GetProperty("candidateXml").GetString()!, new { module, operation, arguments });
                    break;
                case "unifiedObject":
                    string screen = arguments.GetProperty("screenPath").GetString()!, item = arguments.GetProperty("itemPath").GetString()!;
                    if (path != screen) throw new ArgumentException("For Unified object previews, path must match arguments.screenPath.");
                    preview = unifiedWorkflow!.PreviewTransformed(targetId, screen, item, fingerprint,
                        state => ExtensionWire.Parse<UnifiedObjectState>(manager.Execute(module, operation, new { snapshot = state, arguments }).GetProperty("state")), new { module, operation, arguments });
                    break;
                case "plcSource":
                    FindPlcReference(targetId); RequireOfflineIfPlc(targetId);
                    if (path != "") throw new ArgumentException("PLC source generation requires an empty root path.");
                    var source = ExtensionWire.Parse<PlcSourceInput>(manager.Execute(module, operation, new { arguments }).GetProperty("source"));
                    string directory = Path.Combine(artifactRoot, "plc-source-candidates", Guid.NewGuid().ToString("N"));
                    PlcSourceFiles.Write(directory, source);
                    preview = PreviewPlc("source", targetId, "", fingerprint, null, "", null, directory, true);
                    break;
                default: throw new ArgumentException("Unsupported broker route.");
            }
            var native = JsonSerializer.SerializeToElement(preview, JsonDefaults.Options);
            string id = native.GetProperty("planId").GetString()!, hash = native.GetProperty("candidateSha256").GetString()!;
            extensionPlans.Add(id, new ExtensionPlanBinding { Module = module, Generation = manager.Generation(module), Route = descriptor.Route, Hash = hash });
            return new { planId = id, candidateSha256 = hash, extension = manager.Identity(module), operation, nativePreview = native, projectSaved = false };
        }
        public object ApplyExtension(string id, string hash)
        {
            CheckProject();
            var manager = features as ExtensionManager ?? throw new InvalidOperationException("Extension manager is unavailable.");
            var plan = extensionPlans.Take(id, hash, manager.Generation);
            switch (plan.Route)
            {
                case "classicXml": return ApplyClassicXml(id, hash);
                case "unifiedObject": return ApplyUnified(id, hash);
                case "plcSource": return ApplyEngineeringPlan(id, hash);
                default: throw new InvalidOperationException("Unknown extension plan route.");
            }
        }
    }
}
