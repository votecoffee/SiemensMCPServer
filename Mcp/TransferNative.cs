using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace SiemensMCPServer.Mcp
{
    internal static class TransferNative
    {
        internal static MethodInfo? ExportMethod(object node) => ExportMethod(node.GetType());
        internal static MethodInfo? ExportMethod(Type type) => type.GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) })
            ?? type.GetMethod("Export", new[] { typeof(DirectoryInfo) })
            ?? type.GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) });
        private static string? SingletonImport(object node) => node.GetType().FullName == "Siemens.Engineering.Hmi.Screen.ScreenOverview" ? "ImportScreenOverview"
            : node.GetType().FullName == "Siemens.Engineering.Hmi.Screen.ScreenGlobalElements" ? "ImportScreenGlobalElements" : null;
        internal static MethodInfo? ImportMethod(object node) => SingletonImport(node) != null ? ((IEngineeringObject)node).Parent.GetType().GetMethod(SingletonImport(node)!, new[] { typeof(FileInfo), typeof(ImportOptions) })
            : ImportMethod(node.GetType());
        internal static MethodInfo? ImportMethod(Type type) => type.GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) })
            ?? type.GetMethod("Import", new[] { typeof(DirectoryInfo) })
            ?? type.GetMethod("Import", new[] { typeof(DirectoryInfo), typeof(string) });
        internal static void Export(object node, string directory)
        {
            Directory.CreateDirectory(directory);
            var method = ExportMethod(node) ?? throw new NotSupportedException("No supported native export signature on this object.");
            var types = method.GetParameters();
            if (types[0].ParameterType == typeof(FileInfo))
            {
                bool script = node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.RuntimeScripting.", StringComparison.Ordinal);
                DirectoryTransferNative.Complete(method.Invoke(node, new object[] { new FileInfo(Path.Combine(directory, "payload.xml")), script ? ExportOptions.None : ExportOptions.WithDefaults }));
            }
            else DirectoryTransferNative.Export(node, method, directory);
        }
        internal static object? Import(object node, string directory, bool overwrite, bool allowMissingReferences = false, string? evidenceRoot = null)
        {
            var method = ImportMethod(node) ?? throw new NotSupportedException("No supported native import signature on this object.");
            object target = SingletonImport(node) != null ? ((IEngineeringObject)node).Parent : node;
            var parameters = method.GetParameters();
            object? result;
            if (parameters[0].ParameterType == typeof(FileInfo))
            {
                // A single resource XML with its original sidecars. Bulk ordering is supplied by the bundle manifest.
                var xml = Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly);
                if (xml.Length != 1) throw new ArgumentException("Classic import requires exactly one top-level resource XML and any required sidecars.");
                TransferFiles.ValidateXmlCompanions(directory);
                bool textLists = node.GetType().FullName == "Siemens.Engineering.Hmi.TextGraphicList.TextListComposition";
                if (textLists) ValidateTextList(xml[0]);
                if (node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal))
                {
                    string name = TransferFiles.XmlObjectName(File.ReadAllText(xml[0]));
                    var comp = (IEngineeringComposition)node;
                    for (int i = 0; i < comp.Count; i++)
                        if (Convert.ToString(comp[i].GetType().GetProperty("Name")!.GetValue(comp[i], null)) == name) throw new InvalidOperationException("Existing cycles cannot be imported or overwritten.");
                    overwrite = false;
                }
                var batchMethod = allowMissingReferences ? node.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions), typeof(SWImportOptions) }) : null;
                NativeXmlStage? stage = null;
                try
                {
                    string nativeFile = xml[0];
                    if (textLists)
                    {
                        stage = NativeXmlStage.Create(Path.GetFileName(xml[0]));
                        TransferFiles.Copy(directory, stage.DirectoryPath);
                        stage.Verify(File.ReadAllBytes(xml[0]));
                        stage.Report.ResourceInventorySha256 = TransferFiles.Hash(TransferFiles.Inventory(stage.DirectoryPath));
                        foreach (var file in TransferFiles.Inventory(stage.DirectoryPath))
                            if (Path.Combine(stage.NativeDirectory, file.Path).Length > 140) throw new IOException("A native staging companion exceeds the short-path profile.");
                        if (evidenceRoot != null)
                        {
                            stage.Report.EvidenceDirectory = Path.Combine(evidenceRoot, "native-staging", Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(stage.Report.EvidenceDirectory);
                        }
                        nativeFile = stage.Report.NativePath; stage.Report.State = "import-dispatched"; stage.Report.NativeDispatched = true;
                    }
                    result = batchMethod != null ? batchMethod.Invoke(target, new object[] { new FileInfo(nativeFile), overwrite ? ImportOptions.Override : ImportOptions.None, SWImportOptions.IgnoreMissingReferencedObjects })
                        : method.Invoke(target, new object[] { new FileInfo(nativeFile), overwrite ? ImportOptions.Override : ImportOptions.None });
                    DirectoryTransferNative.Complete(result);
                    if (stage != null) { stage.Report.NativeCallReturned = true; stage.Report.State = "native-import-returned; transaction outcome belongs to apply receipt"; }
                }
                catch (Exception ex) { if (stage != null) { stage.Report.State = stage.Report.NativeDispatched ? "native-outcome-needs-inspection" : "not-dispatched"; stage.Report.Error = ex.GetBaseException().Message; } throw; }
                finally
                {
                    if (stage != null)
                    {
                        stage.Dispose();
                        if (stage.Report.EvidenceDirectory != null) File.WriteAllText(Path.Combine(stage.Report.EvidenceDirectory, "staging.json"), JsonSerializer.Serialize(stage.Report, JsonDefaults.Options), new UTF8Encoding(false));
                    }
                }
            }
            else
            {
                result = DirectoryTransferNative.Import(node, method, directory, overwrite);
            }
            return result;
        }
        internal static void ValidateImport(object node, string directory, bool overwrite, bool skipExistingCycles = false)
        {
            var method = ImportMethod(node) ?? throw new NotSupportedException("No supported native import signature on the selected target.");
            if (method.GetParameters()[0].ParameterType == typeof(DirectoryInfo))
            {
                if (!overwrite) throw new ArgumentException("Unified directory imports require overwrite=true to acknowledge native replacement scope.");
                DirectoryTransferNative.Validate(node.GetType().FullName!, directory, method.GetParameters().Length == 2);
            }
            else
            {
                var files = Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly);
                if (files.Length != 1) throw new ArgumentException("Classic import requires exactly one top-level XML resource plus sidecars.");
                TransferFiles.ValidateXmlCompanions(directory);
                if (node.GetType().FullName == "Siemens.Engineering.Hmi.TextGraphicList.TextListComposition") ValidateTextList(files[0]);
                if (!skipExistingCycles && node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal))
                {
                    string name = TransferFiles.XmlObjectName(File.ReadAllText(files[0]));
                    if (EngineeringNative.Children(node, "").Any(c => c.Key == "n:" + Uri.EscapeDataString(name))) throw new InvalidOperationException("Existing classic cycles cannot be overwritten.");
                }
            }
        }
        private static void ValidateTextList(string file)
        {
            var version = typeof(Project).Assembly.GetName().Version;
            bool known = version.Major == 15 && version.Minor == 1;
            NativeXmlValidation.Analyze(File.ReadAllBytes(file), known ? "V15.1" : "", known).RequirePassed();
        }
    }
}
