using System;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer.Analysis;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Offline;
using Xunit;

public sealed class HmiScriptAnalysisTests
{
    internal static HmiResourceInput Script(string name, string code, string hmi = "panel") => new HmiResourceInput { HmiId = hmi, Path = "VBScripts/" + name + ".xml",
        Xml = new XDocument(new XElement("Document", new XElement("Hmi.RuntimeScripting.VBScript", new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("Name", name), new XElement("Code", code))))).ToString() };
    internal static HmiResourceInput Tags(string script, int count = 1, string mode = "Continuous", string cycle = "500 ms", string eventName = "ChangeValue")
    {
        var tags = Enumerable.Range(0, count).Select(i => {
            var call = new XElement("Hmi.Event.FunctionListEntry", new XAttribute("ID", i * 3 + 3), new XElement("LinkList", new XElement("VBScript", new XAttribute("TargetID", "@OpenLink"), new XElement("Name", script))));
            var handler = new XElement("Hmi.Event.Event", new XAttribute("ID", i * 3 + 2), new XElement("AttributeList", new XElement("Name", eventName)), new XElement("ObjectList", call));
            return new XElement("Hmi.Tag.Tag", new XAttribute("ID", i * 3 + 1), new XAttribute("CompositionName", "Tags"),
                new XElement("AttributeList", new XElement("Name", "Field" + i), new XElement("AcquisitionMode", mode)),
                new XElement("LinkList", new XElement("AcquisitionCycle", new XAttribute("TargetID", "@OpenLink"), new XElement("Name", cycle))), new XElement("ObjectList", handler));
        });
        return new HmiResourceInput { HmiId = "panel", Path = "TagTables/Process.xml", Xml = new XDocument(new XElement("Document", new XElement("Hmi.Tag.TagTable", new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("Name", "Process")), new XElement("ObjectList", tags)))).ToString() };
    }
    private static JsonElement Analyze(string view, params HmiResourceInput[] resources) => OfflineOperations.Execute("analyze_script_triggers", JsonSerializer.Serialize(new { resources, view }, ExtensionWire.Options));

    [Fact] public void CountsConfiguredHandlersAndAcquisitionWithoutInferringRuntimeRatesOrOverflow()
    {
        var script = Script("DisplayRefresh", "Sub DisplayRefresh()\nFor i = 0 To 2\nSmartTags(\"DisplayText\") = \"\"\nNext\nEnd Sub");
        var result = Analyze("registrations", script, Tags("DisplayRefresh", 12));
        Assert.Equal(12, result.GetProperty("summary").GetProperty("configuredHandlers").GetInt32());
        Assert.False(result.GetProperty("summary").GetProperty("measuredInvocationRateAvailable").GetBoolean());
        Assert.False(result.GetProperty("summary").GetProperty("queueOverflowEstablished").GetBoolean());
        var row = result.GetProperty("page").GetProperty("items")[0];
        Assert.Equal("tag-change-edge", row.GetProperty("eventKind").GetString());
        Assert.Contains("500 ms", row.GetProperty("acquisitionFields").EnumerateObject().Select(p => p.Value.GetString()));
        var issues = Analyze("diagnostics", script, Tags("DisplayRefresh", 12)).GetProperty("page").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToArray();
        Assert.Contains("repeated-display-work-candidate", issues);
        Assert.Contains("periodic-reconciliation-not-demonstrated", issues);
        Assert.Contains("trigger-read-not-demonstrated", issues);
    }
    [Fact] public void LexerIgnoresCommentsAndStringsAndPreservesUnicodeAndReadWriteDistinction()
    {
        string code = "Rem Call Ghost()\n' SmartTags(\"Fake\") = 0\nx = \"Call Ghost SmartTags(\"\"Fake\"\")\"\nIf SmartTags(\"État\") = 0 Then SmartTags(\"Display\") = \"\"\nHMIRuntime.Tags(\"Connected\").Read\nHMIRuntime.Tags(\"Other\").Write 0";
        var resource = Script("Decode", code);
        var rows = Analyze("dependencies", resource).GetProperty("page").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal("read", rows.Single(r => r.GetProperty("tag").GetString() == "État").GetProperty("access").GetString());
        Assert.Equal("write", rows.Single(r => r.GetProperty("tag").GetString() == "Display").GetProperty("access").GetString());
        Assert.DoesNotContain(rows, r => r.GetProperty("tag").GetString() == "Fake");
        Assert.Equal(resource.Xml, Script("Decode", code).Xml);
        var scripts = Analyze("scripts", resource).GetProperty("page").GetProperty("items")[0];
        Assert.Equal(2, scripts.GetProperty("clearWriteSites").GetArrayLength()); // comparison is never a zero-write site
    }
    [Fact] public void FollowsTransitiveCallsAndCyclesAndLeavesUnresolvedDynamicDependenciesVisible()
    {
        var entry = Script("Entry", "Sub Entry()\nCall Worker()\nEnd Sub");
        var worker = Script("Worker", "Sub Worker()\nx = SmartTags(\"Field0\")\nCall Entry()\nEnd Sub");
        var issues = Analyze("diagnostics", entry, worker, Tags("Entry")).GetProperty("page").GetProperty("items").EnumerateArray().ToArray();
        Assert.DoesNotContain(issues, r => r.GetProperty("code").GetString() == "trigger-read-not-demonstrated");
        var calls = Analyze("calls", entry, worker).GetProperty("page").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, r => Assert.Equal("resolved-exported-script-candidate", r.GetProperty("status").GetString()));
        var dynamic = Script("Entry", "x = SmartTags(prefix & index)\nCall Missing()\n");
        Assert.True(Analyze("scripts", dynamic).GetProperty("page").GetProperty("items")[0].GetProperty("opaque").GetBoolean());
        Assert.Equal("not-exported", Analyze("calls", dynamic).GetProperty("page").GetProperty("items")[0].GetProperty("status").GetString());
    }
    [Fact] public void ScopeAmbiguityAndMissingCategoriesRemainUnknown()
    {
        var a = Script("Same", "x = SmartTags(\"Field0\")");
        var b = Script("Same", "x = 0"); b.Path = "VBScripts/Other.xml";
        var result = Analyze("registrations", a, b, Tags("Same"));
        Assert.Equal("ambiguous", result.GetProperty("page").GetProperty("items")[0].GetProperty("resolution").GetString());
        Assert.Equal("unknown", result.GetProperty("categoryCoverage").EnumerateArray().Single(r => r.GetProperty("category").GetString() == "ScheduledTasks").GetProperty("callerReportedStatus").GetString());
        Assert.Equal("unknown", result.GetProperty("summary").GetProperty("schedulerStatus").GetString());
        var other = Script("Same", "x = 0", "other-panel");
        Assert.Equal("resolved-exported-script", Analyze("registrations", a, other, Tags("Same")).GetProperty("page").GetProperty("items")[0].GetProperty("resolution").GetString());
    }
    [Fact] public void ExplicitPeriodicConfigurationIsOnlyAConfigurationCandidate()
    {
        var script = Script("Decode", "SmartTags(\"Output\") = \"\"");
        var timer = Tags("Decode", eventName: "Cyclic"); timer.Path = "ScheduledTasks/Task.xml";
        var result = Analyze("registrations", script, timer);
        Assert.Equal("periodic-configuration-candidate", result.GetProperty("page").GetProperty("items")[0].GetProperty("eventKind").GetString());
        Assert.Equal("unknown", result.GetProperty("summary").GetProperty("deployedRuntimeStatus").GetString());
        var args = ExtensionWire.Json(new { resources = new[] { script }, schedulerEvidence = new[] { new { id = "capture", kind = "runtime-observation", content = "unverified event 20015" } } });
        var observed = HmiScriptAnalysis.Execute(args);
        Assert.False(observed.GetProperty("summary").GetProperty("queueOverflowEstablished").GetBoolean());
        Assert.Contains("unverified", observed.GetProperty("schedulerEvidence")[0].GetProperty("verification").GetString());
    }
    [Fact] public void StableHashPagesAndXmlSafetyPreserveSuppliedSources()
    {
        var script = Script("Decode", "x = CLng(SmartTags(\"Field0\"))\nSmartTags(\"Out\") = 0");
        var first = Analyze("scripts", script, Tags("Decode", 5));
        var second = Analyze("dependencies", Tags("Decode", 5), script);
        Assert.Equal(first.GetProperty("summary").GetProperty("analysisSha256").GetString(), second.GetProperty("summary").GetProperty("analysisSha256").GetString());
        var changed = Script("Decode", "x = 1");
        Assert.NotEqual(first.GetProperty("summary").GetProperty("analysisSha256").GetString(), Analyze("scripts", changed, Tags("Decode", 5)).GetProperty("summary").GetProperty("analysisSha256").GetString());
        var paged = HmiScriptAnalysis.Execute(ExtensionWire.Json(new { resources = new[] { script, Tags("Decode", 5) }, maxItems = 2, offset = 2 }));
        Assert.Equal(2, paged.GetProperty("page").GetProperty("items").GetArrayLength());
        Assert.True(paged.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Throws<ArgumentException>(() => HmiScriptAnalysis.Execute(ExtensionWire.Json(new { resources = new[] { script }, maxItems = 201 })));
        var unsafeXml = new HmiResourceInput { Path = "bad", Xml = "<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///missing'>]><Document><Hmi.Tag.TagTable>&x;</Hmi.Tag.TagTable></Document>" };
        Assert.Throws<System.Xml.XmlException>(() => Analyze("scripts", unsafeXml));
    }
    [Fact] public void NamedFunctionEntriesAreCandidatesAndExpressionsAreNotLiteralClearSites()
    {
        var script = Script("Decode", "If obj.Visible = False Then x = 1\nobj.Visible = False\nSmartTags(\"Result\") = 0 + 1\nSmartTags(\"Text\") = \"\" & suffix\n");
        var tags = Tags("Decode");
        var document = XDocument.Parse(tags.Xml);
        var entry = document.Descendants("Hmi.Event.FunctionListEntry").Single();
        entry.Element("LinkList")!.Remove();
        entry.Add(new XElement("AttributeList", new XElement("Name", "Decode"))); tags.Xml = document.ToString();
        var result = Analyze("registrations", script, tags);
        Assert.Equal(0, result.GetProperty("summary").GetProperty("explicitScriptLinks").GetInt32());
        Assert.Equal(1, result.GetProperty("summary").GetProperty("exportedNameCandidates").GetInt32());
        Assert.Equal("exported-name-candidate", result.GetProperty("page").GetProperty("items")[0].GetProperty("resolution").GetString());
        var sites = Analyze("scripts", script).GetProperty("page").GetProperty("items")[0].GetProperty("clearWriteSites");
        Assert.Single(sites.EnumerateArray());
        Assert.Equal(2, sites[0].GetProperty("line").GetInt32());
    }
    [Fact] public void StructuredCodeRemainsUnavailableAndCompactDeepXmlCannotAmplifyLocatorsWithoutBound()
    {
        var resource = Script("Opaque", "");
        var xml = XDocument.Parse(resource.Xml);
        xml.Descendants("Code").Single().Add(new XElement("Unknown", "uninterpreted")); resource.Xml = xml.ToString();
        var row = Analyze("scripts", resource).GetProperty("page").GetProperty("items")[0];
        Assert.False(row.GetProperty("codeAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("codeSha256").ValueKind);
        var inner = new XElement("ObjectList", Enumerable.Range(0, 10000).Select(_ => new XElement("Leaf")));
        for (int i = 0; i < 55; i++) inner = new XElement(new string('n', 50), inner);
        resource = new HmiResourceInput { Path = "Screens/Deep.xml", Xml = new XElement("Document", new XElement("Hmi.Screen.Screen", inner)).ToString() };
        Assert.Contains("XPath locator", Assert.Throws<ArgumentException>(() => Analyze("scripts", resource)).Message);
    }
}
