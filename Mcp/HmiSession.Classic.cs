using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession
    {
        public object ReadClassicXml(string hmiId, string path) { CheckProject(); return classicXmlWorkflow!.Read(hmiId, path); }
        public object PreviewClassicXml(string hmiId, string path, string fingerprint, ClassicXmlEdit[] edits)
        { CheckProject(); return classicXmlWorkflow!.Preview(hmiId, path, fingerprint, edits); }
        public object ApplyClassicXml(string planId, string candidateSha256) { CheckProject(); return classicXmlWorkflow!.Apply(planId, candidateSha256); }
        public object PreviewClassicBehaviors(string hmiId, string path, string fingerprint, ClassicBehaviorChange[] changes)
        { CheckProject(); return classicXmlWorkflow!.PreviewBehaviors(hmiId, path, fingerprint, changes); }
        public ClassicResource ReadClassicResource(string hmiId, string path)
        {
            FindClassicHmi(hmiId);
            object node = ResolveEngineering(hmiId, path);
            if (!node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.", StringComparison.Ordinal)
                || TransferNative.ExportMethod(node)?.GetParameters()[0].ParameterType != typeof(FileInfo))
                throw new NotSupportedException("Select one native classic HMI XML resource, such as a screen, tag table, script, text list or graphic list.");
            string directory = Path.Combine(artifactRoot, "comfort-xml-exports", Guid.NewGuid().ToString("N"));
            TransferNative.Export(node, directory);
            string xml = Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly).Single();
            new ClassicXmlDocument(File.ReadAllText(xml, Encoding.UTF8));
            return new ClassicResource { HmiId = hmiId, Path = path, Directory = directory, XmlFile = Path.GetFileName(xml),
                Editable = !node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal) };
        }
        public void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint)
        {
            using (var exclusive = portal!.ExclusiveAccess("Codex Comfort XML edit"))
            {
                CheckProject();
                var current = ReadClassicResource(before.HmiId, before.Path);
                if (!current.Editable || current.Fingerprint != expectedFingerprint) throw new InvalidOperationException("Classic resource changed or is protected; read it again.");
                var baseline = new ClassicXmlDocument(File.ReadAllText(Path.Combine(current.Directory, current.XmlFile), Encoding.UTF8));
                var candidate = new ClassicXmlDocument(File.ReadAllText(Path.Combine(candidateDirectory, before.XmlFile), Encoding.UTF8));
                candidate.ValidateNativeLineGeometry();
                if (candidate.Name != baseline.Name || candidate.NativeType != baseline.NativeType) throw new InvalidOperationException("Candidate resource identity differs.");
                string expected = TransferFiles.SemanticHash(candidateDirectory);
                object node = ResolveEngineering(before.HmiId, before.Path);
                object target = TransferNative.ImportMethod(node) != null ? node : ResolveEngineering(before.HmiId, ParentPath(before.Path));
                TransferNative.ValidateImport(target, candidateDirectory, true);
                using (var transaction = exclusive.Transaction(project!, "Edit Comfort resource XML"))
                {
                    TransferNative.Import(target, candidateDirectory, true, evidenceRoot: Path.GetDirectoryName(candidateDirectory));
                    var after = ReadClassicResource(before.HmiId, before.Path);
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(candidateDirectory)!, "native-readback.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { artifactDirectory = after.Directory, fingerprint = after.Fingerprint, expectedFingerprint = expected,
                            matched = after.Fingerprint == expected, scope = "Export captured inside the native transaction; apply.json records the final outcome." }, JsonDefaults.Options), new UTF8Encoding(false));
                    if (after.Fingerprint != expected) throw new InvalidOperationException("Native Comfort readback differs from the preview; transaction not committed. Inspect native-readback.json in the plan artifacts.");
                    if (exclusive.IsCancellationRequested || !transaction.CanCommit) throw new InvalidOperationException("Comfort transaction cancelled or cannot commit.");
                    transaction.CommitOnDispose();
                }
            }
        }
    }
}
