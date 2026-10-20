using System;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class TextListEvidenceTests
{
    private const string Screen = "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Main</Name></AttributeList><ObjectList>"
        + "<Hmi.Screen.SymbolicIOField ID='1' CompositionName='ScreenItems'><AttributeList><ObjectName>Status</ObjectName></AttributeList><LinkList><TextList TargetID='@OpenLink'><Name>Messages_TL_1</Name></TextList></LinkList></Hmi.Screen.SymbolicIOField>"
        + "</ObjectList></Hmi.Screen.Screen></Document>";
    private static JsonElement Binding(TextListBindingEvidence input) => FeatureOperations.Execute("comfort", "validate_text_list_bindings", ExtensionWire.Json(input));
    private static JsonElement Workflow(TextListWorkflowEvidence input) => FeatureOperations.Execute("comfort", "validate_text_list_workflow", ExtensionWire.Json(input));
    [Fact] public void BindingUsesActualNativeLinkRoleAndPreservesSuffixAndOwnerEvidence()
    {
        var input = new TextListBindingEvidence { Xml = Screen, KnownTextLists = new[] { "Messages_TL", "Messages_TL_1" }, UnchangedScreenAfterXml = Screen.Replace("ID='0'", "ID='99'") };
        var result = Binding(input); Assert.True(result.GetProperty("validation").GetProperty("canPublish").GetBoolean());
        var row = Assert.Single(result.GetProperty("references").EnumerateArray());
        Assert.Equal("Messages_TL_1", row.GetProperty("name").GetString()); Assert.Equal("Status", row.GetProperty("controlName").GetString());
        input.ExpectedBindings = new[] { new TextListBindingExpectation { XPath = row.GetProperty("xPath").GetString()!, ExpectedName = "Messages_TL" } };
        Assert.False(Binding(input).GetProperty("validation").GetProperty("canPublish").GetBoolean());
        input.ExpectedBindings = Array.Empty<TextListBindingExpectation>(); input.KnownTextLists = new[] { "Messages_TL" };
        Assert.False(Binding(input).GetProperty("validation").GetProperty("canPublish").GetBoolean());
    }
    [Fact] public void ScreenPreservationCoversUnrelatedControlFields()
    {
        var result = Binding(new TextListBindingEvidence { Xml = Screen, KnownTextLists = new[] { "Messages_TL_1" }, UnchangedScreenAfterXml = Screen.Replace("Status", "Changed control") });
        Assert.Contains(result.GetProperty("validation").GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "bindings.screen-preserved" && c.GetProperty("status").GetString() == "failed");
    }
    private static TextListWorkflowEvidence Request(bool native = true)
    {
        var before = new TextListDocument(NativeXmlValidationTests.NativeFixture());
        var edits = new[] { new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = "Reviewed" } };
        var candidate = before.Edit(edits); string xml = candidate.Serialize();
        var input = new TextListWorkflowEvidence { ProjectPath = @"C:\Example\Demo.ap15_1", ProcessId = 1234, HmiId = "hmi", Lists = new[] {
            new TextListWorkflowItem { Delta = new ClassicDeltaValidation { BeforeXml = before.Serialize(), CandidateXml = xml, ExpectedBeforeFingerprint = before.Fingerprint,
                ExpectedCandidateSha256 = TextListDocument.Hash(xml), Edits = edits, ReadbackXml = native ? xml : null } } } };
        if (!native) return input;
        input.Lists[0].ApplyReceipt = ExtensionWire.Json(new { state = "applied", hmiId = input.HmiId, listName = before.Name, projectIdentity = new { input.ProjectPath, input.ProcessId },
            beforeFingerprint = before.Fingerprint, candidateSha256 = TextListDocument.Hash(xml), verifiedFingerprint = candidate.Fingerprint, nativeImportReturned = true,
            nativeReadback = "matched", readbackSnapshotSha256 = TextListDocument.Hash(NativeXmlValidation.SnapshotBytes(xml)) });
        input.CompileReceipt = ExtensionWire.Json(new { input.ProjectPath, input.ProcessId, targetId = input.HmiId, state = "Warning", errors = 0, warnings = 1, messagesTruncated = false,
            messages = new[] { new { state = "Warning", description = "Example warning", path = "Demo/Panel" } } });
        input.SaveReceipt = ExtensionWire.Json(new { input.ProjectPath, input.ProcessId, projectSaved = true }); return input;
    }
    [Fact] public void WorkflowKeepsOfflineChecksSeparateAndLeavesMissingStagesNotRun()
    {
        var result = Workflow(Request(false));
        Assert.True(result.GetProperty("validation").GetProperty("canPublish").GetBoolean()); Assert.False(result.GetProperty("suppliedWorkflowComplete").GetBoolean());
        Assert.Equal("not-run", result.GetProperty("suppliedNativeStages").GetProperty("compile").GetString());
        Assert.Equal("not-run", result.GetProperty("suppliedNativeStages").GetProperty("save").GetString());
        Assert.False(result.GetProperty("nativeOperationPerformed").GetBoolean());
    }
    [Fact] public void WorkflowRequiresIdentityAndExactReadbackAndClassifiesWarningsOnlyWithBaseline()
    {
        var input = Request(); var result = Workflow(input); Assert.True(result.GetProperty("suppliedWorkflowComplete").GetBoolean());
        Assert.Equal("unclassified-no-comparable-baseline", result.GetProperty("compileDiagnostics")[0].GetProperty("classification").GetString());
        input.BaselineCompileReceipt = input.CompileReceipt;
        Assert.Equal("inherited", Workflow(input).GetProperty("compileDiagnostics")[0].GetProperty("classification").GetString());
        input.BaselineCompileReceipt = ExtensionWire.Json(new { input.ProjectPath, input.ProcessId, targetId = input.HmiId, state = "Success", errors = 0, warnings = 0, messages = Array.Empty<object>() });
        Assert.Equal("new", Workflow(input).GetProperty("compileDiagnostics")[0].GetProperty("classification").GetString());
        input.ProcessId++; Assert.False(Workflow(input).GetProperty("validation").GetProperty("canPublish").GetBoolean());
        input = Request(); input.Lists[0].Delta.ReadbackXml = input.Lists[0].Delta.ReadbackXml!.Replace("Reviewed", "Wrong native value");
        Assert.False(Workflow(input).GetProperty("validation").GetProperty("canPublish").GetBoolean());
    }
    [Fact] public void UncertainApplyOrFailedCompileCannotBecomeCompleteReceipt()
    {
        var input = Request(); input.Lists[0].ApplyReceipt = ExtensionWire.Json(new { state = "native-import-returned-readback-unverified", nativeImportReturned = true });
        Assert.False(Workflow(input).GetProperty("suppliedWorkflowComplete").GetBoolean());
        input = Request(); input.CompileReceipt = ExtensionWire.Json(new { input.ProjectPath, input.ProcessId, targetId = input.HmiId, state = "Error", errors = 1, warnings = 0, messages = Array.Empty<object>() });
        Assert.False(Workflow(input).GetProperty("suppliedWorkflowComplete").GetBoolean());
    }
    [Fact] public void NullDeltaFieldsProduceReviewFailureWithoutNativeEffect()
    {
        var input = Request(false).Lists[0].Delta; input.ExpectedCandidateSha256 = null!;
        Assert.False(ClassicTextListValidation.Delta(input).GetProperty("validation").GetProperty("canPublish").GetBoolean());
        input = Request(false).Lists[0].Delta; input.Changes = new ClassicTextListChange[] { null! }; input.BlankOtherNewTranslations = true;
        Assert.False(ClassicTextListValidation.Delta(input).GetProperty("validation").GetProperty("canPublish").GetBoolean());
    }
}
