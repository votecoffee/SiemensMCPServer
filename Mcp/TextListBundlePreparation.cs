using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    internal static class TextListBundlePreparation
    {
        internal static object Prepare(string source, string destination, bool repairDeclaration, string targetVersion, string[]? selectedNames = null)
        {
            source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
            if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(destination) || File.Exists(destination)) throw new ArgumentException("Destination must be new and outside the source.");
            if (selectedNames != null && (selectedNames.Length == 0 || selectedNames.Length > 10000 || selectedNames.Any(string.IsNullOrWhiteSpace)
                || selectedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selectedNames.Length)) throw new ArgumentException("Select unique exact native text-list names, or omit selection for the entire TextLists category.");
            string parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
            string work = Path.Combine(parent, ".text-list-work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            var report = new HmiValidationReport { Profile = "classic-text-list-bundle", TargetVersion = targetVersion };
            string sourceHash = "", archiveHash = ""; bool published = false;
            TransferZip.Reader? archiveSource = null;
            byte[] ReadSource(string relative) => archiveSource != null ? archiveSource.Read(relative) : File.ReadAllBytes(TransferBundle.SafePath(source, relative));
            string ReadText(string relative) { using (var reader = new StreamReader(new MemoryStream(ReadSource(relative)), Encoding.UTF8, true)) return reader.ReadToEnd(); }
            try
            {
                TransferFile[] inventory;
                if (File.Exists(source))
                {
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked source archives are unsupported.");
                    archiveHash = HashFile(source); archiveSource = TransferZip.Open(source); inventory = archiveSource.Inventory;
                    report.Add("bundle.archive-integrity", "passed", "ZIP local/central headers, member paths, lengths and CRCs checked; exact archive SHA-256 retained.");
                }
                else
                {
                    inventory = TransferFiles.Inventory(source, validateXml: false);
                    report.Add("bundle.archive-integrity", "not-run", "Folder input; no source archive supplied.", applicability: "not-applicable-folder-input");
                }
                sourceHash = TransferFiles.Hash(inventory); report.SourceSha256 = sourceHash;
                if (inventory.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inventory.Length) throw new InvalidDataException("Case-colliding source paths are unsupported.");
                // Hash manifests are optional for plain exports, but an advertised manifest is never ignored.
                foreach (var manifest in inventory.Where(f => Path.GetFileName(f.Path).Equals(TransferManifest.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    string manifestPrefix = manifest.Path.Contains('/') ? manifest.Path.Substring(0, manifest.Path.LastIndexOf('/') + 1) : "";
                    var manifestInventory = inventory.Where(f => f.Path.StartsWith(manifestPrefix, StringComparison.Ordinal))
                        .Select(f => new TransferFile { Path = f.Path.Substring(manifestPrefix.Length), Bytes = f.Bytes, Sha256 = f.Sha256 }).ToArray();
                    TransferManifest.VerifyBytes(ReadSource(manifest.Path), manifestInventory);
                }
                if (inventory.Any(f => Path.GetFileName(f.Path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException("Unrecognized manifest.json: supply the documented transfer-manifest.json format or a separately reviewed plain export.");
                bool hasManifest = inventory.Any(f => Path.GetFileName(f.Path).Equals(TransferManifest.FileName, StringComparison.OrdinalIgnoreCase));
                report.Add("bundle.source-manifest", hasManifest ? "passed" : "not-run", "When present, every declared manifest member, byte count and SHA-256 must match exactly.",
                    applicability: hasManifest ? "required-for-selected-profile" : "not-applicable-no-source-manifest");
                var roots = inventory.Where(f => f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path.Split('/'))
                    .Where(parts => parts.Take(parts.Length - 1).Contains("TextLists", StringComparer.Ordinal))
                    .Select(parts => string.Join("/", parts.Take(Array.IndexOf(parts, "TextLists") + 1))).Distinct(StringComparer.Ordinal).ToArray();
                if (archiveSource == null && Path.GetFileName(source).Equals("TextLists", StringComparison.Ordinal)) roots = new[] { "" };
                if (roots.Length != 1) throw new InvalidDataException("Select one unambiguous TextLists category root; nested ZIP/folder wrappers are supported.");
                string prefix = roots[0].Length == 0 ? "" : roots[0] + "/";
                var xmlFiles = inventory.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (xmlFiles.Length == 0) throw new InvalidDataException("No text-list XML files found.");
                string summaryPath = roots[0].Contains('/') ? roots[0].Substring(0, roots[0].LastIndexOf('/') + 1) + "ExportSummary.txt" : "ExportSummary.txt";
                string? summaryText = inventory.Any(f => f.Path == summaryPath) ? ReadText(summaryPath) : null;
                if (archiveSource == null && prefix.Length == 0)
                {
                    string parentSummary = Path.Combine(Path.GetDirectoryName(source)!, "ExportSummary.txt");
                    if (File.Exists(parentSummary)) summaryText = File.ReadAllText(parentSummary);
                }
                if (summaryText != null)
                {
                    var statuses = System.Text.RegularExpressions.Regex.Matches(summaryText, @"(?m)^(OK|FAILED)\s+TextLists(?:\s|$)");
                    if (statuses.Count != 1 || statuses[0].Groups[1].Value != "OK") throw new InvalidDataException("TextLists export summary is failed, missing or ambiguous.");
                }
                report.Add("bundle.category-root", "passed", "One exact TextLists root; selected export summary checked when supplied.", roots[0]);
                string release = Path.Combine(work, "release"), bundle = Path.Combine(release, "bundle"); Directory.CreateDirectory(bundle);
                var entries = new List<TransferResource>(); var resources = new List<object>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var selected = new HashSet<string>(selectedNames ?? Array.Empty<string>(), StringComparer.Ordinal); var found = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in xmlFiles)
                {
                    byte[] original = ReadSource(file.Path);
                    // Selection uses internal XML identity, never a sanitized filename or suffix heuristic.
                    byte[] candidate = repairDeclaration ? NativeXmlValidation.RepairDeclaration(original) : original;
                    var validation = NativeXmlValidation.Analyze(candidate, targetVersion, textListMetadata: true);
                    string name = validation.ObjectName ?? throw new InvalidDataException("Cannot establish the native text-list identity: " + file.Path);
                    if (!names.Add(name)) throw new InvalidDataException("Duplicate internal text-list identity: " + name);
                    if (selectedNames != null && !selected.Contains(name)) continue;
                    found.Add(name); validation.RequirePassed();
                    string native = "objects/" + entries.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "/native";
                    string nativeRoot = TransferBundle.SafePath(bundle, native); Directory.CreateDirectory(nativeRoot);
                    File.WriteAllBytes(Path.Combine(nativeRoot, Path.GetFileName(file.Path)), candidate);
                    string originalPath = TransferBundle.SafePath(release, "originals/" + entries.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "/resource.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!); File.WriteAllBytes(originalPath, original);
                    var xml = TransferFiles.Xml(NativeXmlValidation.Read(candidate));
                    foreach (var node in xml.Descendants().Where(e => string.Equals((string?)e.Attribute("external"), "path", StringComparison.OrdinalIgnoreCase)))
                    {
                        string relative = node.Value.Replace('\\', '/');
                        if (node.HasElements || relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("XML companion resources require a separately scoped native export.");
                        TransferBundle.ValidateRelativePath(relative);
                        string sourceParent = file.Path.Contains('/') ? file.Path.Substring(0, file.Path.LastIndexOf('/') + 1) : "";
                        string target = TransferBundle.SafePath(nativeRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (!File.Exists(target)) File.WriteAllBytes(target, ReadSource(sourceParent + relative));
                    }
                    TransferFiles.ValidateXmlCompanions(nativeRoot);
                    entries.Add(new TransferResource { Path = "p:TextLists/n:" + Uri.EscapeDataString(name), ImportPath = "p:TextLists", SourceDirectory = native, NativeType = "Siemens.Engineering.Hmi.TextGraphicList.TextList" });
                    resources.Add(new { name, sourcePath = file.Path, sourceSha256 = file.Sha256, candidateSha256 = TextListDocument.Hash(candidate), declarationRepaired = !candidate.SequenceEqual(original), validation });
                }
                if (entries.Count == 0 || (selectedNames != null && !selected.SetEquals(found))) throw new InvalidDataException("Selected text-list identities were not all found.");
                report.Add("bundle.identities-and-resources", "passed", "Unique native identities, complete selected scope, metadata and required companions checked.");
                File.WriteAllText(Path.Combine(bundle, "export-index.json"), JsonSerializer.Serialize(new { format = "tia-hmi-export-v1", entries, failures = Array.Empty<object>() }, JsonDefaults.Options));
                TransferManifest.Write(bundle); TransferBundle.Read(bundle);
                string zipPath = Path.Combine(release, "bundle.zip");
                using (var output = File.Create(zipPath))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                    foreach (var file in TransferFiles.Inventory(bundle))
                    {
                        var entry = zip.CreateEntry(file.Path);
                        using (var input = File.OpenRead(TransferBundle.SafePath(bundle, file.Path)))
                        using (var target = entry.Open()) input.CopyTo(target);
                    }
                string verify = Path.Combine(work, "archive-check"); TransferZip.Extract(zipPath, verify);
                if (TransferFiles.Hash(TransferFiles.Inventory(bundle)) != TransferFiles.Hash(TransferFiles.Inventory(verify))) throw new InvalidDataException("Published ZIP and folder differ.");
                report.Add("bundle.published-archive", "passed", "Extracted ZIP CRCs, file set and SHA-256 inventory equal the candidate folder.");
                if ((archiveSource == null && sourceHash != TransferFiles.Hash(TransferFiles.Inventory(source, validateXml: false))) || (archiveHash.Length != 0 && archiveHash != HashFile(source))) throw new IOException("Source changed during preparation.");
                report.Add("bundle.immutable-source", "passed", "Original source inventory/archive hashes unchanged; original XML retained outside the import bundle.");
                report.RequirePassed();
                var receipt = new { validation = report, sourceInventorySha256 = sourceHash, sourceArchiveSha256 = archiveHash, bundleInventorySha256 = TransferFiles.Hash(TransferFiles.Inventory(bundle)), archiveSha256 = HashFile(zipPath), resources = entries.Count, resourceReports = resources,
                    sourceAccess = new { mode = archiveSource == null ? "source-folder" : "verified-zip-stream", sourceFilesVerified = inventory.Length,
                        archiveMembersVerified = archiveSource?.MemberCount, sourceArchiveMembersExtracted = 0 },
                    artifactDirectory = Path.Combine(destination, "bundle"), archivePath = Path.Combine(destination, "bundle.zip"), projectChanged = false, nativeImportPerformed = false, downloadPerformed = false };
                File.WriteAllText(Path.Combine(release, "validation.json"), JsonSerializer.Serialize(receipt, JsonDefaults.Options));
                Directory.Move(release, destination); published = true; return receipt;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException || ex is System.Xml.XmlException || ex is JsonException || ex is NotSupportedException)
            {
                report.Add("bundle.required-preflight", ex is NotSupportedException ? "unsupported" : "failed", ex.Message);
                string failurePath = destination + ".validation-failed.json";
                File.WriteAllText(failurePath, JsonSerializer.Serialize(new { validation = report, sourceInventorySha256 = sourceHash, sourceArchiveSha256 = archiveHash, candidatePublished = false }, JsonDefaults.Options));
                throw new InvalidDataException("Required TextLists preflight blocked publication. Retained report: " + failurePath, ex);
            }
            finally
            {
                archiveSource?.Dispose();
                // Only this operation's private, newly created tree is removed.
                // Source inventory already rejects links; ZIP extraction cannot create links.
                if (Directory.Exists(work)) Directory.Delete(work, true);
                if (!published && Directory.Exists(destination)) throw new IOException("An unexpected destination appeared; it was left untouched.");
            }
        }
        private static string HashFile(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
