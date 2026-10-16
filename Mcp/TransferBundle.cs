using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SiemensTiaOpenness.Mcp
{
    internal sealed class TransferResource
    {
        public string Path { get; set; } = "";
        public string ImportPath { get; set; } = "";
        public string SourceDirectory { get; set; } = "";
        public string NativeType { get; set; } = "";
    }
    internal static class TransferBundle
    {
        internal static string SafePath(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(p => p == "" || p == "." || p == ".." || p.Contains(':')))
                throw new ArgumentException("Bundle contains an unsafe relative path.");
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            string prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Bundle path escapes its root.");
            return full;
        }
        internal static TransferResource[] Read(string directory)
        {
            TransferFiles.Inventory(directory);
            using (var document = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(directory, "export-index.json"))))
            {
                var root = document.RootElement;
                string? format = root.GetProperty("format").GetString();
                if (format != "tia-hmi-export-v1" && format != "tia-engineering-export-v1") throw new ArgumentException("Unsupported export bundle format.");
                if (root.GetProperty("failures").GetArrayLength() != 0) throw new ArgumentException("Bundle has export failures. Select and review a complete category export before importing.");
                var resources = JsonSerializer.Deserialize<TransferResource[]>(root.GetProperty("entries").GetRawText(), JsonDefaults.Options)!;
                if (resources.Length == 0 || resources.Length > 10000) throw new ArgumentException("Bundle has no native resources or exceeds limits.");
                foreach (var entry in resources)
                {
                    if (entry.Path == null || entry.ImportPath == null || entry.NativeType == null || entry.SourceDirectory == null) throw new ArgumentException("Bundle entry is incomplete.");
                    SafePath(directory, entry.SourceDirectory);
                    if (!entry.NativeType.StartsWith("Siemens.Engineering.Hmi.", StringComparison.Ordinal)
                        && !entry.NativeType.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal)
                        && !entry.NativeType.StartsWith("Siemens.Engineering.Graphics.", StringComparison.Ordinal)
                        && !PlcPolicy.AllowsResourceType(entry.NativeType)) throw new ArgumentException("Bundle contains an unsupported engineering resource.");
                    TransferFiles.Inventory(SafePath(directory, entry.SourceDirectory));
                }
                if (resources.GroupBy(r => r.Path, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate bundle resource paths.");
                return resources.OrderBy(r => Priority(r.ImportPath)).ThenBy(r => r.Path, StringComparer.Ordinal).ToArray();
            }
        }
        private static int Priority(string path)
        {
            string[] order = { "$projectGraphics", "Connections", "Cycles", "VBScriptFolder", "Scripts", "TypeGroup", "TagTableGroup", "BlockGroup", "ExternalSourceGroup", "TagFolder", "TagTables", "Tags", "TextLists", "HmiTextLists", "GraphicLists", "HmiGraphicLists", "ScreenTemplateFolder", "ScreenPopupFolder", "ScreenSlideinFolder", "ScreenGlobalElements", "ScreenOverview", "ScreenFolder", "Screens" };
            var parts = path.Split('/').Select(p => p.StartsWith("p:", StringComparison.Ordinal) ? Uri.UnescapeDataString(p.Substring(2)) : p).ToArray();
            for (int i = 0; i < order.Length; i++) if (parts.Contains(order[i], StringComparer.Ordinal)) return i;
            return order.Length;
        }
        internal static Dictionary<string, EngineeringSnapshot> Snapshots(string directory)
        {
            var snapshots = new Dictionary<string, EngineeringSnapshot>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(directory, "object.json", SearchOption.AllDirectories))
            {
                var state = JsonSerializer.Deserialize<EngineeringSnapshot>(File.ReadAllText(file), JsonDefaults.Options) ?? throw new ArgumentException("Invalid object snapshot.");
                if (state.Path == null || snapshots.ContainsKey(state.Path)) throw new ArgumentException("Duplicate object snapshot paths.");
                snapshots.Add(state.Path, state);
            }
            return snapshots;
        }
    }
}
