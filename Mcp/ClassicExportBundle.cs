using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SiemensMCPServer.Mcp
{
    public sealed class ClassicFolderName
    {
        [JsonRequired] public string ExportedPath { get; set; } = "";
        [JsonRequired] public string Name { get; set; } = "";
    }

    // Converts the existing WinForms export layout into a staged, indexed bundle.
    // Source bytes are never rewritten; native import still requires a separate preview/apply.
    internal static class ClassicExportBundle
    {
        private sealed class Category
        {
            public string Name = "", Navigator = "", Composition = "", Resource = "", FolderType = "";
        }
        private static readonly Category[] Categories = {
            new Category { Name = "ProjectGraphics", Navigator = "$projectGraphics", Resource = "Hmi.Globalization.MultiLingualGraphic" },
            new Category { Name = "Connections", Navigator = "p:Connections", Resource = "Hmi.Communication.Connection" },
            new Category { Name = "Cycles", Navigator = "p:Cycles", Resource = "Hmi.Cycle.Cycle" },
            new Category { Name = "VBScripts", Navigator = "p:VBScriptFolder", Composition = "VBScripts", Resource = "Hmi.RuntimeScripting.VBScript", FolderType = "Hmi.RuntimeScripting.VBScriptUserFolder" },
            new Category { Name = "TagTables", Navigator = "p:TagFolder", Composition = "TagTables", Resource = "Hmi.Tag.TagTable", FolderType = "Hmi.Tag.TagUserFolder" },
            new Category { Name = "TextLists", Navigator = "p:TextLists", Resource = "Hmi.TextGraphicList.TextList" },
            new Category { Name = "GraphicLists", Navigator = "p:GraphicLists", Resource = "Hmi.TextGraphicList.GraphicList" },
            new Category { Name = "ScreenTemplates", Navigator = "p:ScreenTemplateFolder", Composition = "ScreenTemplates", Resource = "Hmi.Screen.ScreenTemplate", FolderType = "Hmi.Screen.ScreenTemplateUserFolder" },
            new Category { Name = "PopupScreens", Navigator = "p:ScreenPopupFolder", Composition = "ScreenPopups", Resource = "Hmi.Screen.ScreenPopup", FolderType = "Hmi.Screen.ScreenPopupUserFolder" },
            new Category { Name = "SlideInScreens", Navigator = "p:ScreenSlideinFolder/p:ScreenSlideins", Resource = "Hmi.Screen.ScreenSlidein" },
            new Category { Name = "PermanentArea", Navigator = "p:ScreenOverview", Resource = "Hmi.Screen.ScreenOverview" },
            new Category { Name = "Screens", Navigator = "p:ScreenFolder", Composition = "Screens", Resource = "Hmi.Screen.Screen", FolderType = "Hmi.Screen.ScreenUserFolder" }
        };

        internal static object Prepare(string sourceDirectory, string destination, string[] categories, ClassicFolderName[] folderNames)
        {
            sourceDirectory = Path.GetFullPath(sourceDirectory); destination = Path.GetFullPath(destination);
            if (categories == null || categories.Length == 0 || categories.Distinct(StringComparer.Ordinal).Count() != categories.Length
                || categories.Any(n => !Categories.Any(c => c.Name == n))) throw new ArgumentException("Select unique category names from the classic ExportSummary.txt layout.");
            if (destination.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(sourceDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(destination) || File.Exists(destination)) throw new ArgumentException("Destination must be a new directory outside the source export.");
            var inventory = TransferFiles.Inventory(sourceDirectory);
            string summaryFile = Path.Combine(sourceDirectory, "ExportSummary.txt");
            if (!File.Exists(summaryFile)) throw new ArgumentException("Select the exact export root containing ExportSummary.txt.");
            string summary = File.ReadAllText(summaryFile);
            var statuses = Regex.Matches(summary, @"(?m)^(OK|FAILED)\s+([A-Za-z]+)(?:\s|$)").Cast<Match>().GroupBy(m => m.Groups[2].Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (string category in categories)
                if (!statuses.TryGetValue(category, out var status) || status.Length != 1 || status[0].Groups[1].Value != "OK")
                    throw new ArgumentException("Selected category has no unambiguous successful export record: " + category);
            var overrides = FolderOverrides(folderNames);
            var usedOverrides = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<TransferResource>();
            var snapshots = new Dictionary<string, EngineeringSnapshot>(StringComparer.Ordinal);
            var copies = new List<KeyValuePair<string, string>>();
            foreach (var category in Categories.Where(c => categories.Contains(c.Name, StringComparer.Ordinal)))
            {
                string prefix = category.Name + "/";
                var xmlFiles = inventory.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (!Directory.Exists(Path.Combine(sourceDirectory, category.Name))) throw new ArgumentException("Selected category directory is missing: " + category.Name);
                foreach (var xmlFile in xmlFiles)
                {
                    string relative = xmlFile.Path.Substring(prefix.Length);
                    string[] parts = relative.Split('/');
                    if (category.FolderType.Length == 0 && category.Name != "ProjectGraphics" && parts.Length != 1)
                        throw new ArgumentException("Unexpected folder hierarchy in " + xmlFile.Path);
                    var xml = TransferFiles.Xml(File.ReadAllText(Path.Combine(sourceDirectory, xmlFile.Path)));
                    var resource = xml.Root?.Elements().Where(e => e.Name.LocalName == category.Resource).ToArray();
                    if (xml.Root?.Name != "Document" || resource?.Length != 1 || xml.Root.Elements().Count(e => e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)) != 1)
                        throw new ArgumentException("Category/resource mismatch: " + xmlFile.Path);
                    string path = category.Navigator;
                    if (category.FolderType.Length != 0)
                    {
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            string folder = prefix + string.Join("/", parts.Take(i + 1));
                            string name = overrides.TryGetValue(folder, out var original) ? original : parts[i];
                            if (overrides.ContainsKey(folder)) usedOverrides.Add(folder);
                            path += "/p:Folders/n:" + Uri.EscapeDataString(name);
                            snapshots[path] = new EngineeringSnapshot { Path = path, NativeType = "Siemens.Engineering." + category.FolderType };
                        }
                        path += "/p:" + category.Composition;
                    }
                    string importPath = path;
                    if (category.Name != "PermanentArea")
                    {
                        string? name = resource![0].Element("AttributeList")?.Element("Name")?.Value;
                        if (category.Name == "SlideInScreens")
                        {
                            string type = resource![0].Element("AttributeList")?.Element("SlideinType")?.Value ?? throw new ArgumentException("Slide-in export has no SlideinType: " + xmlFile.Path);
                            path += "/s:" + Uri.EscapeDataString(type);
                        }
                        else path += "/n:" + Uri.EscapeDataString(string.IsNullOrEmpty(name) ? throw new ArgumentException("Native resource name is missing: " + xmlFile.Path) : name!);
                    }
                    string native = "objects/" + entries.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "/native";
                    entries.Add(new TransferResource { Path = path, ImportPath = importPath, SourceDirectory = native, NativeType = "Siemens.Engineering." + category.Resource });
                    string parent = xmlFile.Path.Substring(0, xmlFile.Path.LastIndexOf('/') + 1);
                    copies.Add(new KeyValuePair<string, string>(xmlFile.Path, native + "/" + parts.Last()));
                    // Sidecars stay relative to their native XML. A child resource folder owns its own companions.
                    foreach (var sidecar in inventory.Where(f => f.Path.StartsWith(parent, StringComparison.Ordinal) && !f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (xmlFiles.Any(x => x.Path != xmlFile.Path && x.Path.StartsWith(parent, StringComparison.Ordinal)
                            && x.Path.Substring(0, x.Path.LastIndexOf('/') + 1).Length > parent.Length
                            && sidecar.Path.StartsWith(x.Path.Substring(0, x.Path.LastIndexOf('/') + 1), StringComparison.Ordinal))) continue;
                        copies.Add(new KeyValuePair<string, string>(sidecar.Path, native + "/" + sidecar.Path.Substring(parent.Length)));
                    }
                }
            }
            if (entries.Count == 0) throw new ArgumentException("Selected categories contain no native resources.");
            if (entries.GroupBy(e => e.Path, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Export resources or mapped folders collide at the same native path.");
            if (overrides.Keys.Any(k => !usedOverrides.Contains(k))) throw new ArgumentException("Folder-name override does not select an exported resource folder.");
            Directory.CreateDirectory(destination);
            foreach (var copy in copies)
            {
                string target = TransferBundle.SafePath(destination, copy.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(sourceDirectory, copy.Key), target, false);
                if (TextListDocument.Hash(File.ReadAllBytes(target)) != inventory.Single(f => f.Path == copy.Key).Sha256) throw new InvalidOperationException("Source changed while preparing the bundle.");
            }
            int index = 0;
            foreach (var state in snapshots.Values.OrderBy(s => s.Path, StringComparer.Ordinal))
            {
                string folder = Path.Combine(destination, "folders", (index++).ToString("D5", System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "object.json"), JsonSerializer.Serialize(state, JsonDefaults.Options), new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(destination, "export-index.json"), JsonSerializer.Serialize(new { format = "tia-engineering-export-v1", family = "classic", entries, failures = Array.Empty<object>() }, JsonDefaults.Options), new UTF8Encoding(false));
            var report = new { sourceFormat = "classic-winforms-export", sourceDirectory, categories, folderNames, artifactDirectory = destination,
                resources = entries.Count, folders = snapshots.Count, sourceInventory = inventory,
                scope = "Selected exported resources only. The legacy exporter can log per-object failures without marking its category failed; completeness is not established. Unmapped folder names use exported directory names, which may have been sanitized. Open links may require other resources.",
                projectChanged = false, nextStep = "Read the target root, then hmi_preview_import_bundle with this artifactDirectory; review the plan before apply." };
            File.WriteAllText(Path.Combine(destination, "conversion.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            TransferBundle.Read(destination);
            if (TransferFiles.Hash(inventory) != TransferFiles.Hash(TransferFiles.Inventory(sourceDirectory))) throw new InvalidOperationException("Source changed while preparing the bundle.");
            return report;
        }
        private static Dictionary<string, string> FolderOverrides(ClassicFolderName[] names)
        {
            if (names == null || names.Length > 10000 || names.Any(n => n == null || string.IsNullOrEmpty(n.ExportedPath) || string.IsNullOrWhiteSpace(n.Name) || n.Name.Length > 256 || n.Name.Any(char.IsControl))
                || names.GroupBy(n => n.ExportedPath, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Supply unique exported folder paths and bounded native names.");
            foreach (var name in names) TransferBundle.SafePath(Path.GetTempPath(), name.ExportedPath);
            return names.ToDictionary(n => n.ExportedPath, n => n.Name, StringComparer.Ordinal);
        }
    }
}
