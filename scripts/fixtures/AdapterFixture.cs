using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    internal static class FixtureEntryPoint
    {
        public static Task<int> Main(string[] args) => Program.RunAsync(args, (root, features) => new FixtureSession(root, features));
    }

    // Test executable only: real MCP handlers, scheduler, review workflow and
    // pure workers; IHmiSession replaces all Siemens/native dispatch below them.
    internal sealed class FixtureSession : IHmiSession, ITextListBackend
    {
        private readonly string state, artifacts;
        private readonly IFeatureEngine features;
        private TextListWorkflow workflow;
        private bool attached;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private string XmlPath => Path.Combine(state, "native-state.xml");
        private string ProjectPath => Path.Combine(state, "FixtureProject.ap15_1");
        private string SavedPath => Path.Combine(state, "saved-fingerprint");
        public FixtureSession(string artifacts, IFeatureEngine features)
        {
            state = Environment.GetEnvironmentVariable("SIEMENS_FIXTURE_STATE") ?? throw new InvalidOperationException("Explicit fixture state is required.");
            state = Path.GetFullPath(state); Directory.CreateDirectory(state);
            this.artifacts = artifacts; this.features = features;
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)))
                throw new InvalidOperationException("Fixture must not load Siemens engineering assemblies.");
            if (!File.Exists(XmlPath)) File.WriteAllText(XmlPath, "<Document><Hmi.TextGraphicList.TextList ID='0'><AttributeList><Name>Messages_TL</Name><ListRange>Decimal</ListRange></AttributeList><ObjectList><Hmi.TextGraphicList.TextListEntry ID='1' CompositionName='Entries'><AttributeList><DefaultEntry>false</DefaultEntry><EntryType>SingleValue</EntryType><From>7</From><To>7</To></AttributeList><ObjectList><MultilingualText ID='2' CompositionName='Text'><ObjectList><MultilingualTextItem ID='3' CompositionName='Items'><AttributeList><Culture>en-US</Culture><Text>Original</Text></AttributeList></MultilingualTextItem><MultilingualTextItem ID='4' CompositionName='Items'><AttributeList><Culture>es-ES</Culture><Text>Conservado</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.TextGraphicList.TextListEntry></ObjectList></Hmi.TextGraphicList.TextList></Document>", new UTF8Encoding(false));
            if (!File.Exists(SavedPath)) File.WriteAllText(SavedPath, Document().Fingerprint);
            workflow = NewWorkflow(); Journal("provider-created");
        }
        private TextListWorkflow NewWorkflow() => new TextListWorkflow(this, Path.Combine(artifacts, "fixture-previews"), features);
        private TextListDocument Document() => new TextListDocument(File.ReadAllText(XmlPath, Encoding.UTF8));
        private void Journal(string name)
        {
            if (thread != Thread.CurrentThread.ManagedThreadId || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Fixture native access escaped the owning STA.");
            File.AppendAllText(Path.Combine(state, "effects.jsonl"), JsonSerializer.Serialize(new { fixture = true, name, processId = System.Diagnostics.Process.GetCurrentProcess().Id, thread, fingerprint = Document().Fingerprint }) + "\n", new UTF8Encoding(false));
        }
        private void RequireAttached() { if (!attached) throw new InvalidOperationException("Select the exact fixture project first."); }
        private void Target(string hmiId, string listName)
        {
            RequireAttached();
            if (hmiId != "fixture-hmi" || listName != "Messages_TL") throw new InvalidOperationException("Unknown fixture target.");
        }
        public object Attach(int processId, string expectedProjectPath)
        {
            if (processId != 4242 || !string.Equals(expectedProjectPath, ProjectPath, StringComparison.Ordinal)) throw new ArgumentException("Exact fixture process/project required.");
            if (File.Exists(Path.Combine(state, "slow-attach"))) Thread.Sleep(500); // Synthetic long operation for progress delivery.
            attached = true; workflow = NewWorkflow(); Journal("attached"); return Status();
        }
        public object Status() => new { fixture = true, attached, projectPath = attached ? ProjectPath : null,
            projectModified = Document().Fingerprint != File.ReadAllText(SavedPath), processId = attached ? (int?)4242 : null,
            hmis = attached ? new[] { new { hmiId = "fixture-hmi", name = "FixtureHmi", family = "classic" } } : new object[0] };
        public object ListProcesses() => new[] { new { processId = 4242, projectPath = ProjectPath } };
        public object ApiCapabilities() => new { fixture = true, nativeAcceptance = false };
        public object ListTextLists(string hmiId) { Target(hmiId, "Messages_TL"); return new { hmiId, textLists = new[] { "Messages_TL" } }; }
        public object Read(string hmiId, string listName)
        {
            Target(hmiId, listName); var document = Document();
            return new { hmiId, listName, fingerprint = document.Fingerprint, values = document.Read(), nativeImportScope = document.NativeImportScope,
                structure = document.Structure(), activeProjectCultures = new[] { "en-US", "es-ES" } };
        }
        public string ReadTextList(string hmiId, string listName) { Target(hmiId, listName); return File.ReadAllText(XmlPath, Encoding.UTF8); }
        public void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml)
        {
            Target(hmiId, listName); TextListWorkflow.RequireFingerprint(Document(), expectedFingerprint);
            File.WriteAllText(XmlPath, candidateXml, new UTF8Encoding(false)); Journal("import-effect");
            if (File.Exists(Path.Combine(state, "lose-reply"))) Environment.Exit(91);
        }
        public object Preview(string hmiId, string listName, string expectedFingerprint, TextEdit[] edits)
        { Target(hmiId, listName); return workflow.Preview(hmiId, listName, expectedFingerprint, edits); }
        public object Apply(string planId, string candidateSha256) { RequireAttached(); return workflow.Apply(planId, candidateSha256); }
        public object Compile(string hmiId) { Target(hmiId, "Messages_TL"); Journal("compile"); return new { fixture = true, errors = 0, warnings = 0 }; }
        public object Save(string expectedProjectPath)
        {
            RequireAttached(); if (expectedProjectPath != ProjectPath) throw new ArgumentException("Wrong fixture project.");
            File.WriteAllText(SavedPath, Document().Fingerprint); Journal("save"); return new { saved = true, projectPath = ProjectPath };
        }
        public object Reopen(string expectedProjectPath)
        {
            RequireAttached(); if (expectedProjectPath != ProjectPath || Document().Fingerprint != File.ReadAllText(SavedPath)) throw new InvalidOperationException("Wrong project or unsaved fixture state.");
            InvalidatePreviews(); Journal("reopen"); return Status();
        }
        public void InvalidatePreviews() { workflow = NewWorkflow(); Journal("previews-invalidated"); }
        public void Dispose() { Journal("provider-disposed"); }
        public object PreviewExtension(string module, string operation, string targetId, string path, string fingerprint, JsonElement arguments) => throw new NotSupportedException("Not in the fixture workflow: PreviewExtension");
        public object ApplyExtension(string id, string hash) => throw new NotSupportedException("Not in the fixture workflow: ApplyExtension");
        public object ReadPlcExtension(string operation, string kind, JsonElement arguments) => throw new NotSupportedException("Not in the fixture workflow: ReadPlcExtension");
        public object ReadProjectExtension(string operation, string kind, JsonElement arguments) => throw new NotSupportedException("Not in the fixture workflow: ReadProjectExtension");
        public object ListScreens(string hmiId) => throw new NotSupportedException("Not in the fixture workflow: ListScreens");
        public object ListScreenItems(string hmiId, string screenPath) => throw new NotSupportedException("Not in the fixture workflow: ListScreenItems");
        public object ReadUnifiedObject(string hmiId, string screenPath, string itemPath) => throw new NotSupportedException("Not in the fixture workflow: ReadUnifiedObject");
        public object PreviewUnified(string hmiId, string screenPath, string itemPath, string expectedFingerprint, UnifiedEdit[] edits) => throw new NotSupportedException("Not in the fixture workflow: PreviewUnified");
        public object ApplyUnified(string planId, string candidateSha256) => throw new NotSupportedException("Not in the fixture workflow: ApplyUnified");
        public object ReadEngineeringObject(string hmiId, string path) => throw new NotSupportedException("Not in the fixture workflow: ReadEngineeringObject");
        public object ExportEngineeringObject(string hmiId, string path) => throw new NotSupportedException("Not in the fixture workflow: ExportEngineeringObject");
        public object PreviewEngineering(string action, string hmiId, string path, string fingerprint, EngineeringEdit[]? edits, string nativeType, CreationArgument[]? arguments, string sourceDirectory, bool overwrite) => throw new NotSupportedException("Not in the fixture workflow: PreviewEngineering");
        public object ApplyEngineeringPlan(string planId, string candidateSha256) => throw new NotSupportedException("Not in the fixture workflow: ApplyEngineeringPlan");
        public object ReadClassicXml(string hmiId, string path) => throw new NotSupportedException("Not in the fixture workflow: ReadClassicXml");
        public object PreviewClassicXml(string hmiId, string path, string fingerprint, ClassicXmlEdit[] edits) => throw new NotSupportedException("Not in the fixture workflow: PreviewClassicXml");
        public object PreviewClassicBehaviors(string hmiId, string path, string fingerprint, ClassicBehaviorChange[] changes) => throw new NotSupportedException("Not in the fixture workflow: PreviewClassicBehaviors");
        public object ApplyClassicXml(string planId, string candidateSha256) => throw new NotSupportedException("Not in the fixture workflow: ApplyClassicXml");
        public object ListPlcs() => throw new NotSupportedException("Not in the fixture workflow: ListPlcs");
        public object ReadPlcObject(string plcId, string path) => throw new NotSupportedException("Not in the fixture workflow: ReadPlcObject");
        public object ReadPlcResource(string plcId, string path) => throw new NotSupportedException("Not in the fixture workflow: ReadPlcResource");
        public object ExportPlcObjects(string plcId, string path) => throw new NotSupportedException("Not in the fixture workflow: ExportPlcObjects");
        public object PreviewPlc(string action, string plcId, string path, string fingerprint, EngineeringEdit[]? edits, string nativeType, CreationArgument[]? arguments, string sourceDirectory, bool overwrite) => throw new NotSupportedException("Not in the fixture workflow: PreviewPlc");
        public object PreviewPlcXmlImport(string plcId, string path, string fingerprint, string candidateXml, bool overwrite) => throw new NotSupportedException("Not in the fixture workflow: PreviewPlcXmlImport");
        public object PreviewPlcSource(string plcId, string fingerprint, string name, string language, string code, bool allowBlockReplacement) => throw new NotSupportedException("Not in the fixture workflow: PreviewPlcSource");
        public object CompilePlc(string plcId) => throw new NotSupportedException("Not in the fixture workflow: CompilePlc");
        public object PreviewClassicTextStructure(string hmiId, string listName, string expectedFingerprint, ClassicTextListChange[] changes) => throw new NotSupportedException("Not in the fixture workflow: PreviewClassicTextStructure");
        public object Archive(string expectedProjectPath) => throw new NotSupportedException("Not in the fixture workflow: Archive");
    }
}
