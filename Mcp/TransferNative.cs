using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace SiemensTiaOpenness.Mcp
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
        internal static object? Import(object node, string directory, bool overwrite, bool allowMissingReferences = false)
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
                if (node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal))
                {
                    string name = TransferFiles.XmlObjectName(File.ReadAllText(xml[0]));
                    var comp = (IEngineeringComposition)node;
                    for (int i = 0; i < comp.Count; i++)
                        if (Convert.ToString(comp[i].GetType().GetProperty("Name")!.GetValue(comp[i], null)) == name) throw new InvalidOperationException("Existing cycles cannot be imported or overwritten.");
                    overwrite = false;
                }
                var batchMethod = allowMissingReferences ? node.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions), typeof(SWImportOptions) }) : null;
                result = batchMethod != null ? batchMethod.Invoke(target, new object[] { new FileInfo(xml[0]), overwrite ? ImportOptions.Override : ImportOptions.None, SWImportOptions.IgnoreMissingReferencedObjects })
                    : method.Invoke(target, new object[] { new FileInfo(xml[0]), overwrite ? ImportOptions.Override : ImportOptions.None });
                DirectoryTransferNative.Complete(result);
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
                if (!skipExistingCycles && node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal))
                {
                    string name = TransferFiles.XmlObjectName(File.ReadAllText(files[0]));
                    if (EngineeringNative.Children(node, "").Any(c => c.Key == "n:" + Uri.EscapeDataString(name))) throw new InvalidOperationException("Existing classic cycles cannot be overwritten.");
                }
            }
        }
    }
}
