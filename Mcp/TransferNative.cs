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
        internal static MethodInfo? ExportMethod(object node) => node.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) })
            ?? node.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo) })
            ?? node.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) });
        private static string? SingletonImport(object node) => node.GetType().FullName == "Siemens.Engineering.Hmi.Screen.ScreenOverview" ? "ImportScreenOverview"
            : node.GetType().FullName == "Siemens.Engineering.Hmi.Screen.ScreenGlobalElements" ? "ImportScreenGlobalElements" : null;
        internal static MethodInfo? ImportMethod(object node) => SingletonImport(node) != null ? ((IEngineeringObject)node).Parent.GetType().GetMethod(SingletonImport(node)!, new[] { typeof(FileInfo), typeof(ImportOptions) })
            : node.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) })
            ?? node.GetType().GetMethod("Import", new[] { typeof(DirectoryInfo) })
            ?? node.GetType().GetMethod("Import", new[] { typeof(DirectoryInfo), typeof(string) });
        internal static void Export(object node, string directory)
        {
            Directory.CreateDirectory(directory);
            var method = ExportMethod(node) ?? throw new NotSupportedException("No supported native export signature on this object.");
            var types = method.GetParameters();
            if (types[0].ParameterType == typeof(FileInfo))
            {
                bool script = node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.RuntimeScripting.", StringComparison.Ordinal);
                method.Invoke(node, new object[] { new FileInfo(Path.Combine(directory, "payload.xml")), script ? ExportOptions.None : ExportOptions.WithDefaults });
            }
            else method.Invoke(node, types.Length == 1 ? new object[] { new DirectoryInfo(directory) } : new object[] { new DirectoryInfo(directory), "payload" });
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
            }
            else
            {
                if (!overwrite) throw new ArgumentException("Unified directory imports have native replacement semantics. Preview with overwrite=true to acknowledge that scope.");
                result = method.Invoke(node, parameters.Length == 1 ? new object[] { new DirectoryInfo(directory) } : new object[] { new DirectoryInfo(directory), "payload" });
            }
            if (result is bool accepted && !accepted) throw new InvalidOperationException("Native import returned false; transaction was not committed.");
            return result;
        }
        internal static void ValidateImport(object node, string directory, bool overwrite, bool skipExistingCycles = false)
        {
            var method = ImportMethod(node) ?? throw new NotSupportedException("No supported native import signature on the selected target.");
            if (method.GetParameters()[0].ParameterType == typeof(DirectoryInfo))
            {
                if (!overwrite) throw new ArgumentException("Unified directory imports require overwrite=true to acknowledge native replacement scope.");
                if (Directory.GetFiles(directory, "*.yml", SearchOption.TopDirectoryOnly).Length == 0) throw new ArgumentException("Unified import requires its native YML configuration and companions.");
            }
            else
            {
                var files = Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly);
                if (files.Length != 1) throw new ArgumentException("Classic import requires exactly one top-level XML resource plus sidecars.");
                if (!skipExistingCycles && node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal))
                {
                    string name = TransferFiles.XmlObjectName(File.ReadAllText(files[0]));
                    if (EngineeringNative.Children(node, "").Any(c => c.Key == "n:" + Uri.EscapeDataString(name))) throw new InvalidOperationException("Existing classic cycles cannot be overwritten.");
                }
            }
        }
    }
}
