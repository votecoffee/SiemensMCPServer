using System;
using System.Linq;
using SiemensMCPServer.Analysis;
using Xunit;

internal static class HmiReferenceFixture
{
    public static string Table(string address = "Axes[1].Position", string type = "DInt", string connection = "Rockwell", string name = @"HMI\Axis\Position", string extra = "")
        => "<Document><Engineering version='V15.1'/><Hmi.Tag.TagTable ID='0'><AttributeList><Name>Tags</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID='1' CompositionName='Tags'><AttributeList><Name>" + name
            + "</Name><AddressAccessMode>Absolute</AddressAccessMode><LogicalAddress>" + address + "</LogicalAddress><Length>1</Length>" + extra + "</AttributeList><LinkList><Connection TargetID='@OpenLink'><Name>"
            + connection + "</Name></Connection><DataType TargetID='@OpenLink'><Name>" + type + "</Name></DataType><HmiDataType TargetID='@OpenLink'><Name>" + type
            + "</Name></HmiDataType></LinkList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>";
    public static string Screen(string reference = @"HMI\Axis\Position", string extra = "", string nativeType = "Hmi.Screen.Screen")
        => "<Document><" + nativeType + " ID='0'><AttributeList><Name>Overview</Name></AttributeList><ObjectList><Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><ObjectName>Start</ObjectName></AttributeList><ObjectList>"
            + "<Hmi.Event.Event ID='2' CompositionName='Events'><AttributeList><Name>Press</Name></AttributeList><ObjectList><Hmi.Event.FunctionListEventHandler ID='3'><ObjectList><Hmi.Event.FunctionListEntry ID='4'><AttributeList><Name>SetTag</Name></AttributeList><LinkList><Tag TargetID='@OpenLink'><Name>"
            + reference + "</Name></Tag></LinkList></Hmi.Event.FunctionListEntry></ObjectList></Hmi.Event.FunctionListEventHandler></ObjectList></Hmi.Event.Event>"
            + extra + "</ObjectList></Hmi.Screen.Button></ObjectList></" + nativeType + "></Document>";
    public static HmiAnalysisInput Input(string? table = null, string? screen = null) => new HmiAnalysisInput {
        Resources = new[] { new HmiResourceInput { HmiId = "hmi", Path = "tags", Xml = table ?? Table() },
            new HmiResourceInput { HmiId = "hmi", Path = "screens/overview", Xml = screen ?? Screen() } },
        ControllerDocuments = new[] { new ControllerDocumentInput { Id = "plc", Format = "rockwell-l5x", Content = ControllerFixture.L5x } },
        ConnectionMappings = new[] { new HmiConnectionMapping { HmiId = "hmi", Connection = "Rockwell", ControllerId = "plc", ExpectedControllerName = "DemoPLC" } },
        ExportReportedComplete = true };
}
public sealed class HmiReferenceAnalysisTests
{
    [Fact] public void FollowsNativeQualifiedTagsFromScreensControlsEventsToControllerMembers()
    {
        var report = HmiReferenceAnalyzer.Analyze(HmiReferenceFixture.Input()); var reference = report.References.Single(r => r.LinkType == "Tag");
        Assert.Equal("valid", reference.Status); Assert.Equal("Axes[1].Position", reference.ControllerAddress); Assert.Equal("DINT", reference.Resolution!.DataType);
        Assert.Equal("Start", reference.ControlName); Assert.Contains(reference.Context, s => s == "Hmi.Event.Event:Press");
        Assert.Contains(@"HMI:HMI\Axis\Position", reference.Chain); Assert.False(report.NativeBindingVerified);
        Assert.Single(report.Bindings); Assert.Equal("Absolute", report.Tags.Single().AddressMode);
    }
    [Theory] [InlineData("Hmi.Screen.Screen")] [InlineData("Hmi.Screen.ScreenPopup")] [InlineData("Hmi.Screen.ScreenTemplate")] [InlineData("Hmi.Screen.ScreenSlidein")]
    public void AnalyzesScreenResourceKindsAndAnimationContexts(string type)
    {
        string dynamic = "<Hmi.Dynamic.TagConnectionDynamic ID='9' CompositionName='Animations'><AttributeList><Name>Visible</Name><Indirect>false</Indirect></AttributeList><LinkList><Tag TargetID='@OpenLink'><Name>HMI\\Axis\\Position</Name></Tag></LinkList></Hmi.Dynamic.TagConnectionDynamic>";
        var report = HmiReferenceAnalyzer.Analyze(HmiReferenceFixture.Input(screen: HmiReferenceFixture.Screen(extra: dynamic, nativeType: type)));
        Assert.Equal(2, report.References.Count(r => r.LinkType == "Tag" && r.Status == "valid")); Assert.All(report.References, r => Assert.NotEmpty(r.XPath));
    }
    [Theory]
    [InlineData("Axes[3].Position", "DInt", "arrayBounds")]
    [InlineData("Axes[0].Absent", "DInt", "missingMember")]
    [InlineData("Axes[0].Position", "Int", "typeMismatch")]
    [InlineData("ReadyAlias", "Bool", "valid")]
    [InlineData("Missing", "DInt", "missingSymbol")]
    public void ValidatesControllerMembersTypesArraysAndAliases(string address, string type, string status)
    {
        var report = HmiReferenceAnalyzer.Analyze(HmiReferenceFixture.Input(HmiReferenceFixture.Table(address, type)));
        Assert.Equal(status, report.Bindings.Single().Status); Assert.Equal(status, report.References.Single(r => r.LinkType == "Tag").Status);
    }
    [Fact] public void ArrayReferencesValidateBothHmiAndControllerDimensions()
    {
        var input = HmiReferenceFixture.Input(HmiReferenceFixture.Table("Axes[1].Flags", "Array [0..3] of DInt").Replace("<Length>1</Length>", "<Length>4</Length>"),
            HmiReferenceFixture.Screen(@"HMI\Axis\Position[3]"));
        var report = HmiReferenceAnalyzer.Analyze(input); var row = report.References.Single(r => r.LinkType == "Tag");
        Assert.Equal("valid", row.Status); Assert.Equal("Axes[1].Flags[3]", row.ControllerAddress); Assert.Empty(row.Resolution!.Dimensions);
        input.Resources[1].Xml = HmiReferenceFixture.Screen(@"HMI\Axis\Position[4]");
        Assert.Equal("hmiArrayBounds", HmiReferenceAnalyzer.Analyze(input).References.Single(r => r.LinkType == "Tag").Status);
        input.Resources[0].Xml = HmiReferenceFixture.Table("Axes[1].Flags", "Array [1..4] of DInt").Replace("<Length>1</Length>", "<Length>4</Length>");
        Assert.Equal("arrayBoundsMismatch", HmiReferenceAnalyzer.Analyze(input).Bindings.Single().Status);
    }
    [Fact] public void ExplicitConnectionMappingChecksIdentityAndDoesNotAssertNativePartner()
    {
        var input = HmiReferenceFixture.Input(); input.ConnectionMappings = Array.Empty<HmiConnectionMapping>();
        Assert.Equal("unmappedConnection", HmiReferenceAnalyzer.Analyze(input).Bindings.Single().Status);
        input = HmiReferenceFixture.Input(); input.ConnectionMappings[0].ControllerId = "other";
        Assert.Equal("missingController", HmiReferenceAnalyzer.Analyze(input).Bindings.Single().Status);
        input = HmiReferenceFixture.Input(); input.ConnectionMappings[0].ExpectedControllerName = "OtherPLC";
        Assert.Equal("controllerIdentityMismatch", HmiReferenceAnalyzer.Analyze(input).Bindings.Single().Status);
        input = HmiReferenceFixture.Input(); Assert.False(HmiReferenceAnalyzer.Analyze(input).Bindings.Single().NativePartnerVerified);
    }
    [Fact] public void HmiIdentityAndQualifiedNamesPreventCrossTargetOrSuffixGuessing()
    {
        var input = HmiReferenceFixture.Input(); input.Resources[1].HmiId = "other";
        Assert.Contains("missing", HmiReferenceAnalyzer.Analyze(input).References.Single(r => r.LinkType == "Tag").Issues.Single());
        input = HmiReferenceFixture.Input(screen: HmiReferenceFixture.Screen("Position")); Assert.Equal("valid", HmiReferenceAnalyzer.Analyze(input).References.Single(r => r.LinkType == "Tag").Status);
        input.Resources = input.Resources.Concat(new[] { new HmiResourceInput { HmiId = "hmi", Path = "other/tags", Xml = HmiReferenceFixture.Table(name: @"Other\Position") } }).ToArray();
        Assert.Contains("ambiguous", HmiReferenceAnalyzer.Analyze(input).References.Single(r => r.LinkType == "Tag").Issues.Single());
    }
    [Fact] public void DynamicScriptAndIncompleteExportRegionsRemainVisible()
    {
        string dynamic = "<Hmi.Dynamic.TagConnectionDynamic ID='9'><AttributeList><Indirect>true</Indirect><Expression>GetTag(name)</Expression></AttributeList><LinkList><Tag TargetID='@OpenLink'><Name>HMI\\Axis\\Position</Name></Tag></LinkList></Hmi.Dynamic.TagConnectionDynamic>";
        var input = HmiReferenceFixture.Input(screen: HmiReferenceFixture.Screen(extra: dynamic)); input.ExportReportedComplete = false; input.ExportFailures = new[] { "unexported object" };
        var report = HmiReferenceAnalyzer.Analyze(input); Assert.Single(report.Diagnostics); Assert.Contains(report.References, r => r.Status == "indirect");
        Assert.False(report.ExportReportedComplete); Assert.Single(report.ExportFailures);
        input.Resources[0].Xml = HmiReferenceFixture.Table(extra: "<UseMultiplexing>true</UseMultiplexing>");
        Assert.Equal("multiplexed", HmiReferenceAnalyzer.Analyze(input).Bindings.Single().Status);
    }
    [Fact] public void ConnectionAndControllerReferencesOnTagDeclarationsJoinTheBinding()
    {
        var input = HmiReferenceFixture.Input(); var report = HmiReferenceAnalyzer.Analyze(input);
        Assert.Equal("valid", report.References.Single(r => r.LinkType == "Connection").Status);
        string symbolic = input.Resources[0].Xml.Replace("<LogicalAddress>Axes[1].Position</LogicalAddress>", "")
            .Replace("</LinkList>", "<ControllerTag TargetID='@OpenLink'><Name>Axes[1].Position</Name></ControllerTag></LinkList>");
        input.Resources[0].Xml = symbolic; Assert.Equal("valid", HmiReferenceAnalyzer.Analyze(input).References.Single(r => r.LinkType == "ControllerTag").Status);
    }
    [Fact] public void ImpactsFollowControllerParentMembersAndAliasesWithoutPrefixFalsePositives()
    {
        var input = HmiReferenceFixture.Input(HmiReferenceFixture.Table("AliasAgain")); var report = HmiReferenceAnalyzer.Analyze(input);
        var impact = HmiReferenceImpact.Report(input, report, report.AnalysisSha256, new[] { new HmiProposedChange { Kind = "controllerSymbol", ControllerId = "plc", Name = "Counter" } });
        Assert.Contains(impact.Items, i => i.Reference.LinkType == "Tag"); Assert.All(impact.Items, i => Assert.Contains("dependency", i.Reason));
        Assert.Empty(HmiReferenceImpact.Report(input, report, report.AnalysisSha256, new[] { new HmiProposedChange { Kind = "controllerSymbol", ControllerId = "plc", Name = "Count" } }).Items);
    }
    [Fact] public void BindingAndConnectionProposalsValidateTargetsWithoutMutatingInputs()
    {
        var input = HmiReferenceFixture.Input(); var report = HmiReferenceAnalyzer.Analyze(input); string before = input.Resources[0].Xml;
        var impact = HmiReferenceImpact.Report(input, report, report.AnalysisSha256, new[] { new HmiProposedChange { Kind = "binding", HmiId = "hmi", Name = @"HMI\Axis\Position", NewSymbol = "Axes[9].Position" } });
        Assert.NotEmpty(impact.Items); Assert.All(impact.Items, item => Assert.Equal("arrayBounds", item.ProposedBinding!.Status));
        Assert.Equal(before, input.Resources[0].Xml);
        impact = HmiReferenceImpact.Report(input, report, report.AnalysisSha256, new[] { new HmiProposedChange { Kind = "connection", Name = "Rockwell", NewControllerId = "absent" } });
        Assert.All(impact.Items, item => Assert.Equal("missingController", item.ProposedBinding!.Status));
    }
    [Fact] public void ImpactRequiresTheCurrentAnalysisFingerprintAndReportsEmptyMatches()
    {
        var input = HmiReferenceFixture.Input(); var report = HmiReferenceAnalyzer.Analyze(input);
        Assert.Throws<InvalidOperationException>(() => HmiReferenceImpact.Report(input, report, "stale", new[] { new HmiProposedChange { Kind = "tag", Name = "Missing" } }));
        var impact = HmiReferenceImpact.Report(input, report, report.AnalysisSha256, new[] { new HmiProposedChange { Kind = "tag", Name = "Missing" } }); Assert.Empty(impact.Items); Assert.NotEmpty(impact.Issues);
        input.ControllerDocuments[0].Content += "\n"; Assert.NotEqual(report.AnalysisSha256, HmiReferenceAnalyzer.Analyze(input).AnalysisSha256);
    }
    [Fact] public void NativeIdLinksAndLiteralNamesRemainResourceScoped()
    {
        var input = HmiReferenceFixture.Input(); input.Resources[1].Xml = input.Resources[1].Xml.Replace("TargetID='@OpenLink'", "TargetID='#1'");
        var report = HmiReferenceAnalyzer.Analyze(input); Assert.NotEmpty(report.References.Single(r => r.LinkType == "Tag").Issues);
        input = HmiReferenceFixture.Input(); input.Resources = input.Resources.Concat(new[] { input.Resources[0] }).ToArray();
        Assert.Throws<ArgumentException>(() => HmiReferenceAnalyzer.Analyze(input));
    }
}
