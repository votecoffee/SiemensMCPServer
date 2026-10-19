using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Analysis
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ControllerFileInput
    {
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public string Format { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiAnalysisFileRequest
    {
        [JsonRequired] public string ExportDirectory { get; set; } = "";
        public string HmiId { get; set; } = "";
        public ControllerFileInput[] ControllerFiles { get; set; } = Array.Empty<ControllerFileInput>();
        public HmiConnectionMapping[] ConnectionMappings { get; set; } = Array.Empty<HmiConnectionMapping>();
        public HmiProposedChange[] Changes { get; set; } = Array.Empty<HmiProposedChange>();
        public string ExpectedAnalysisSha256 { get; set; } = "";
    }
    internal sealed class AnalysisSourceFile
    {
        public string Path { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
    }
    internal sealed class LoadedHmiAnalysis
    {
        public HmiAnalysisInput Analysis { get; set; } = null!;
        public AnalysisSourceFile[] Sources { get; set; } = Array.Empty<AnalysisSourceFile>();
    }
    // Explicit file-command adapter. Worker/MCP operations call only the pure serialized-content analyzers.
    internal static class HmiAnalysisFiles
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static string FileHash(string path)
        {
            using (var algorithm = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static AnalysisSourceFile Source(string path) => new AnalysisSourceFile { Path = path, Bytes = new FileInfo(path).Length, Sha256 = FileHash(path) };
        private static string Read(string path, long limit, List<AnalysisSourceFile> sources)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > limit) throw new ArgumentException("Linked/oversized analysis input file: " + path);
            var before = Source(path); string content = File.ReadAllText(path, Utf8);
            if (FileHash(path) != before.Sha256) throw new IOException("Analysis source changed while reading: " + path);
            sources.Add(before); return content;
        }
        private static string RelativeDirectory(string root, string relative)
        {
            relative = relative.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new ArgumentException("Export index sourceDirectory must remain inside the export root.");
            string directory = root;
            foreach (string part in relative.Split('/'))
            {
                directory = Path.Combine(directory, part);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked export paths are unsupported.");
            }
            return directory;
        }
        private static void Snapshots(string directory, List<string> files, int depth)
        {
            if (depth > 64 || files.Count > 20000 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked or excessive export snapshot tree.");
            foreach (string file in Directory.GetFiles(directory, "object.json"))
            {
                if (files.Count >= 20000) throw new ArgumentException("Too many export snapshots.");
                files.Add(file);
            }
            foreach (string child in Directory.GetDirectories(directory)) Snapshots(child, files, depth + 1);
        }
        public static LoadedHmiAnalysis Load(HmiAnalysisFileRequest request)
        {
            if (request.ExportDirectory == null || request.HmiId == null || !Path.IsPathRooted(request.ExportDirectory) || request.ControllerFiles == null || request.ControllerFiles.Length > 32
                || request.ConnectionMappings == null || request.Changes == null) throw new ArgumentException("Use an absolute export directory and bounded controller files/mappings.");
            string root = Path.GetFullPath(request.ExportDirectory);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked export roots are unsupported.");
            var sources = new List<AnalysisSourceFile>(); var resources = new List<HmiResourceInput>(); var unsupported = new List<string>(); var failures = new List<string>();
            string indexText = Read(Path.Combine(root, "export-index.json"), 8 * 1024 * 1024, sources);
            string hmi;
            using (var index = JsonDocument.Parse(indexText))
            {
                var metadata = index.RootElement;
                if (metadata.GetProperty("format").GetString() != "tia-engineering-export-v1" || metadata.GetProperty("family").GetString() != "classic")
                    throw new ArgumentException("This command currently requires an indexed classic/Comfort export. Unified needs a separate resource adapter.");
                hmi = request.HmiId.Length != 0 ? request.HmiId : metadata.TryGetProperty("targetId", out var target) ? target.GetString() ?? "" : "";
                if (hmi.Length == 0) hmi = "classic-export";
                if (metadata.TryGetProperty("path", out var subtree) && !string.IsNullOrEmpty(subtree.GetString())) failures.Add("Export covers a subtree; whole-HMI coverage is not established.");
                if (metadata.TryGetProperty("failures", out var failed))
                    foreach (var item in failed.EnumerateArray()) failures.Add(item.GetRawText());
                var entries = metadata.GetProperty("entries").EnumerateArray().ToArray();
                if (entries.Length > 2000) throw new ArgumentException("Export index exceeds 2000 entries.");
                long total = 0;
                foreach (var entry in entries)
                {
                    string path = entry.GetProperty("path").GetString() ?? throw new ArgumentException("Missing exported object path.");
                    string native = RelativeDirectory(root, entry.GetProperty("sourceDirectory").GetString() ?? "");
                    var xml = Directory.GetFiles(native, "*.xml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToArray();
                    if (xml.Length == 0) { unsupported.Add(path + ": no supported native classic XML resource."); continue; }
                    foreach (string file in xml)
                    {
                        if ((total += new FileInfo(file).Length) > 256L * 1024 * 1024) throw new ArgumentException("Export XML exceeds 256 MiB on disk.");
                        string text = Read(file, 32 * 1024 * 1024, sources);
                        resources.Add(new HmiResourceInput { HmiId = hmi, Path = xml.Length == 1 ? path : path + "/file:" + Uri.EscapeDataString(Path.GetFileName(file)), Xml = text });
                    }
                }
            }
            var controllers = new List<ControllerDocumentInput>();
            foreach (var file in request.ControllerFiles)
            {
                if (file == null || !Path.IsPathRooted(file.Path)) throw new ArgumentException("Controller file paths must be absolute.");
                controllers.Add(new ControllerDocumentInput { Id = file.Id, Format = file.Format, Content = Read(Path.GetFullPath(file.Path), 128L * 1024 * 1024, sources) });
            }
            var snapshots = new List<string>(); Snapshots(root, snapshots, 0); var connections = new List<HmiConnectionDefinition>();
            foreach (string file in snapshots)
            {
                using (var snapshot = JsonDocument.Parse(Read(file, 16 * 1024 * 1024, sources)))
                {
                    var node = snapshot.RootElement;
                    if (!node.TryGetProperty("nativeType", out var type)) continue;
                    string nativeType = type.GetString() ?? "";
                    if (!nativeType.EndsWith(".Connection", StringComparison.Ordinal) && !nativeType.EndsWith(".HmiConnection", StringComparison.Ordinal)) continue;
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var attribute in node.GetProperty("attributes").EnumerateArray())
                    {
                        string name = attribute.GetProperty("name").GetString() ?? "";
                        string value = attribute.GetProperty("value").GetString() ?? "";
                        attributes.Add(name, value);
                    }
                    if (!attributes.TryGetValue("Name", out var connectionName) || connectionName.Length == 0) { unsupported.Add("A native connection snapshot has no scalar Name."); continue; }
                    connections.Add(new HmiConnectionDefinition { HmiId = hmi, Name = connectionName, NativeType = nativeType,
                        Path = node.GetProperty("path").GetString() ?? "", Attributes = attributes });
                }
            }
            return new LoadedHmiAnalysis { Analysis = new HmiAnalysisInput { Resources = resources.ToArray(), ControllerDocuments = controllers.ToArray(),
                ConnectionMappings = request.ConnectionMappings, ConnectionDefinitions = connections.ToArray(), ConnectionMetadataAvailable = connections.Count != 0,
                ExportFailures = failures.ToArray(), UnsupportedResources = unsupported.ToArray(),
                ExportReportedComplete = failures.Count == 0 && unsupported.Count == 0 }, Sources = sources.ToArray() };
        }
        private static void JsonFile(string output, string name, object value)
        {
            using (var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(stream, value, JsonDefaults.Options);
        }
        private static string Csv(string value)
        {
            // Quoting CSV alone does not stop spreadsheet formula interpretation. JSON retains exact source text.
            if (value.Length != 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        public static object Run(string requestPath, string outputDirectory)
        {
            if (!Path.IsPathRooted(requestPath) || !Path.IsPathRooted(outputDirectory)) throw new ArgumentException("Request/output paths must be absolute.");
            var requestSources = new List<AnalysisSourceFile>();
            string requestText = Read(requestPath, 1024 * 1024, requestSources);
            var request = JsonSerializer.Deserialize<HmiAnalysisFileRequest>(requestText, JsonDefaults.Options) ?? throw new ArgumentException("Missing file request.");
            string output = Path.GetFullPath(outputDirectory), inputRoot = Path.GetFullPath(request.ExportDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if ((output.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(inputRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Write reports outside the source export directory.");
            if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Output directory must be new; existing reports are never overwritten.");
            var loaded = Load(request); var report = HmiReferenceAnalyzer.Analyze(loaded.Analysis);
            var impact = request.Changes.Length == 0 ? null : HmiReferenceImpact.Report(loaded.Analysis, report,
                request.ExpectedAnalysisSha256.Length == 0 ? report.AnalysisSha256 : request.ExpectedAnalysisSha256, request.Changes);
            var allSources = requestSources.Concat(loaded.Sources).ToArray();
            foreach (var source in allSources) if (FileHash(source.Path) != source.Sha256) throw new IOException("Analysis source changed: " + source.Path);
            Directory.CreateDirectory(output);
            JsonFile(output, "sources.json", new { files = allSources, resources = report.Sources });
            JsonFile(output, "controllers.json", report.Controllers);
            JsonFile(output, "connections.json", new { report.ConnectionMetadataAvailable, report.ConnectionDefinitions, mappings = loaded.Analysis.ConnectionMappings });
            JsonFile(output, "tags.json", report.Tags);
            JsonFile(output, "bindings.json", report.Bindings);
            JsonFile(output, "references.json", report.References);
            JsonFile(output, "diagnostics.json", new { report.Diagnostics, report.ExportFailures, report.UnsupportedResources });
            if (impact != null) JsonFile(output, "impact.json", impact);
            using (var csv = new StreamWriter(Path.Combine(output, "references.csv"), false, Utf8))
            {
                csv.WriteLine("HmiId,ResourcePath,Control,OwnerType,Owner,LinkType,Reference,Connection,ControllerId,ControllerAddress,Status");
                foreach (var row in report.References) csv.WriteLine(string.Join(",", new[] { row.HmiId, row.ResourcePath, row.ControlName, row.OwnerType,
                    row.OwnerName, row.LinkType, row.ReferenceText, row.Connection, row.ControllerId, row.ControllerAddress, row.Status }.Select(Csv)));
            }
            var summary = new { report.EngineVersion, report.AnalysisSha256, resources = report.Sources.Length, controllers = report.Controllers.Length, tags = report.Tags.Length, references = report.References.Length,
                bindingStatuses = report.Bindings.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count()),
                referenceStatuses = report.References.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()),
                diagnostics = report.Diagnostics.Length, report.ExportReportedComplete, exportFailures = report.ExportFailures.Length, unsupportedResources = report.UnsupportedResources.Length,
                impactReferences = impact?.Items.Length ?? 0, report.NativeBindingVerified, report.Scope };
            JsonFile(output, "summary.json", summary);
            var files = Directory.GetFiles(output).OrderBy(f => f, StringComparer.Ordinal).Select(f => new { path = Path.GetFileName(f), bytes = new FileInfo(f).Length, sha256 = FileHash(f) }).ToArray();
            JsonFile(output, "manifest.json", new { format = "tia-hmi-analysis-files-v1", report.EngineVersion, report.AnalysisSha256, files });
            return new { outputDirectory = output, summaryPath = Path.Combine(output, "summary.json"), manifestPath = Path.Combine(output, "manifest.json"), summary };
        }
    }
}
