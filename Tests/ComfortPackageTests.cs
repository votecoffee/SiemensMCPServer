using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ComfortPackageTests
{
    private const string Screen = "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>PrototypeScreen</Name><Number>1</Number><Width>800</Width></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID='1' CompositionName='Layers'><AttributeList><Name>Layer_1</Name></AttributeList><ObjectList><Hmi.Screen.IOField ID='2' CompositionName='ScreenItems'><AttributeList><Name>PrototypeField</Name><Left>10</Left><Top>20</Top></AttributeList><ObjectList><Hmi.Screen.Property ID='3' CompositionName='Properties'><AttributeList><Name>ProcessValue</Name></AttributeList><LinkList><Owner TargetID='2'/><Tag TargetID='@OpenLink'><Name>\"PrototypeTag\"</Name></Tag></LinkList></Hmi.Screen.Property></ObjectList><ObjectList><MultilingualText CompositionName='Text'><ObjectList><MultilingualTextItem ID='4'><AttributeList><Culture>de-DE</Culture><Text>Unchanged</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Screen.IOField></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>";
    private const string Table = "<Document><Engineering version='V15.1'/><Hmi.Tag.TagTable ID='0'><AttributeList><Name>PrototypeTable</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID='1' CompositionName='Tags'><AttributeList><Name>PrototypeTag</Name><LogicalAddress></LogicalAddress></AttributeList><LinkList><Connection TargetID='@OpenLink'><Name>PLC_Connection</Name></Connection><ControllerTag TargetID='@OpenLink'><Name>OldSymbol</Name></ControllerTag><DataType TargetID='@OpenLink'><Name>Int</Name></DataType><HmiDataType TargetID='@OpenLink'><Name>Int</Name></HmiDataType></LinkList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>";
    private static ClassicXmlEdit Edit(string path, string before, string after) => new ClassicXmlEdit { XPath = path, ExpectedValue = before, NewValue = after };
    private static ComfortPackageSpec Spec() => new ComfortPackageSpec {
        Resources = new[] {
            new ComfortResourceSpec { Kind = "screen", Name = "New Screen Ü", TemplateXml = Screen, KeepTemplateMembers = false,
                Edits = new[] { Edit("//Hmi.Screen.Screen/AttributeList/Number", "1", "23") },
                Members = new[] { new ComfortMemberSpec { Prototype = "PrototypeField", Name = "Status", Edits = new[] { Edit("//Left", "10", "100"), Edit("//Tag/Name", "\"PrototypeTag\"", "\"NewTag\"") } } } },
            new ComfortResourceSpec { Kind = "tagTable", Name = "New Table", TemplateXml = Table, KeepTemplateMembers = false,
                Members = new[] { new ComfortMemberSpec { Prototype = "PrototypeTag", Name = "NewTag", Edits = new[] { Edit("//ControllerTag/Name", "OldSymbol", "Counter") } } } }
        }, PlcSymbols = new[] { new ComfortPlcSymbol { Connection = "PLC_Connection", Symbol = "Counter", DataType = "Int" } }
    };
    [Fact] public void BuildsDependencyOrderedImportBundleWithNativeMetadataAndInternalReferences()
    {
        var result = ComfortPackage.Build(Spec());
        Assert.True(result.Validation.Valid); Assert.False(result.Validation.NativeBindingVerified); Assert.False(result.NativeImportAccepted);
        Assert.Equal(1, result.Validation.PlcBindings); Assert.Equal(1, result.Validation.ScreenTagReferences);
        var screen = XDocument.Parse(result.Files.Single(f => f.Content.Contains("<Hmi.Screen.Screen ")).Content);
        Assert.Equal("V15.1", (string?)screen.Root!.Element("Engineering")!.Attribute("version"));
        Assert.Contains("Unchanged", screen.ToString()); Assert.Contains("<Number>23</Number>", screen.ToString()); Assert.Contains("<Left>100</Left>", screen.ToString());
        var control = screen.Descendants("Hmi.Screen.IOField").Single();
        Assert.Equal("Status", control.Element("AttributeList")!.Element("Name")!.Value);
        Assert.Equal((string?)control.Attribute("ID"), (string?)control.Descendants("Owner").Single().Attribute("TargetID"));
        var ids = screen.Descendants().Attributes("ID").Select(a => a.Value).ToArray(); Assert.Equal(ids.Length, ids.Distinct().Count());
        using var scratch = new Scratch();
        foreach (var file in result.Files)
        {
            Assert.Equal(TextListDocument.Hash(file.Content), file.Sha256);
            string path = Path.Combine(scratch.Path, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, file.Content, new UTF8Encoding(false));
        }
        var entries = TransferBundle.Read(scratch.Path);
        Assert.Equal("Siemens.Engineering.Hmi.Tag.TagTable", entries[0].NativeType);
        Assert.Equal("p:ScreenFolder/p:Screens/n:New%20Screen%20%C3%9C", entries[1].Path);
        Assert.Equal(Screen, Spec().Resources[0].TemplateXml);
    }
    [Fact] public void ValidatesActualLinksAgainstManifestRatherThanAcceptingAnyDeclaration()
    {
        var input = new ComfortBindingInput { ResourceXml = new[] { Screen, Table }, PlcSymbols = new[] { new ComfortPlcSymbol { Connection = "PLC_Connection", Symbol = "OldSymbol", DataType = "Int" } } };
        Assert.True(ComfortPackage.ValidateBindings(input).Valid);
        input.PlcSymbols[0].Connection = "DifferentPLC"; Assert.Contains(ComfortPackage.ValidateBindings(input).Errors, e => e.Contains("absent"));
        input.PlcSymbols[0].Connection = "PLC_Connection"; input.PlcSymbols[0].DataType = "Bool"; Assert.Contains(ComfortPackage.ValidateBindings(input).Errors, e => e.Contains("data type"));
        input.PlcSymbols = Array.Empty<ComfortPlcSymbol>(); Assert.False(ComfortPackage.ValidateBindings(input).Valid);
    }
    [Fact] public void MissingAmbiguousAndIncompleteReferencesAreExplicitFailures()
    {
        var input = new ComfortBindingInput { ResourceXml = new[] { Screen, Table, Table.Replace("PrototypeTable", "AnotherTable") }, PlcSymbols = Array.Empty<ComfortPlcSymbol>() };
        Assert.Contains(ComfortPackage.ValidateBindings(input).Errors, e => e.Contains("Ambiguous"));
        input.ResourceXml = new[] { Screen }; Assert.Contains(ComfortPackage.ValidateBindings(input).Errors, e => e.Contains("absent from the package"));
        input.ResourceXml = new[] { Table.Replace("<Connection TargetID='@OpenLink'><Name>PLC_Connection</Name></Connection>", "") };
        Assert.Contains(ComfortPackage.ValidateBindings(input).Errors, e => e.Contains("Incomplete"));
    }
    [Fact] public void RejectsStaleEditsDuplicateNamesAndMixedVersionsBeforeProducingPackage()
    {
        var spec = Spec(); spec.Resources[0].Members[0].Edits[0].ExpectedValue = "stale";
        Assert.Throws<InvalidOperationException>(() => ComfortPackage.Build(spec));
        spec = Spec(); spec.Resources[0].KeepTemplateMembers = true; spec.Resources[0].Members[0].Name = "PrototypeField";
        Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
        spec = Spec(); spec.Resources[1].TemplateXml = Table.Replace("V15.1", "V19"); Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
        spec = Spec(); spec.Resources[0].Members[0].Edits = new[] { Edit("//@ID", "2", "9") }; Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
    }
    [Theory]
    [InlineData("UId='opaque'")]
    [InlineData("ID='0'")]
    public void UnsupportedOrDuplicateIdentitiesCannotBeCloned(string attribute)
    {
        var spec = Spec(); spec.Resources[0].TemplateXml = Screen.Replace("ID='4'", attribute);
        Assert.ThrowsAny<Exception>(() => ComfortPackage.Build(spec));
    }
    [Fact] public void UnresolvedCrossPrototypeReferencesAndExternalFilesAreNotSilentlyDropped()
    {
        var spec = Spec(); spec.Resources[0].TemplateXml = Screen.Replace("Owner TargetID='2'", "Owner TargetID='1'");
        // Layer 1 remains, so this reference is still valid.
        Assert.True(ComfortPackage.Build(spec).Validation.Valid);
        spec.Resources[0].TemplateXml = Screen.Replace("Owner TargetID='2'", "Owner TargetID='4'");
        Assert.True(ComfortPackage.Build(spec).Validation.Valid); // Internal prototype ID is remapped.
        spec.Resources[0].TemplateXml = Screen.Replace("Owner TargetID='2'", "Owner TargetID='missing'"); Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
        spec.Resources[0].TemplateXml = Screen.Replace("<Width>800</Width>", "<File external='path'>image.png</File>"); Assert.Throws<NotSupportedException>(() => ComfortPackage.Build(spec));
    }
    [Fact] public void LocalExtensionCatalogWorksWithoutNativeHandlesAndRejectsUnknownInputFields()
    {
        var op = FeatureOperations.Describe("comfort").Single(o => o.Name == "build_screen_package"); Assert.Equal("local", op.Route); Assert.False(op.Preview);
        var result = FeatureOperations.Execute("comfort", op.Name, ExtensionWire.Json(Spec()));
        Assert.Equal("tia-comfort-package-v1", result.GetProperty("format").GetString());
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", op.Name, ExtensionWire.Json(new { resources = Array.Empty<object>(), plcSymbols = Array.Empty<object>(), typo = true })));
    }
    [Fact] public void ReplacingPrototypesCannotLeaveReferencesToRemovedOtherControls()
    {
        var spec = Spec();
        spec.Resources[0].TemplateXml = Screen.Replace("Owner TargetID='2'", "Owner TargetID='99'").Replace("</Hmi.Screen.ScreenLayer>", "</Hmi.Screen.ScreenLayer><Hmi.Screen.Button ID='99' CompositionName='ScreenItems'><AttributeList><Name>Other</Name></AttributeList></Hmi.Screen.Button>");
        Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
    }
    [Fact] public void RejectsDuplicateScreenNumbersAndPackageCloneExpansion()
    {
        var spec = Spec();
        spec.Resources = spec.Resources.Concat(new[] { new ComfortResourceSpec { Kind = "screen", TemplateXml = Screen, Name = "Other Screen", KeepTemplateMembers = false,
            Edits = new[] { Edit("//Hmi.Screen.Screen/AttributeList/Number", "1", "23") } } }).ToArray();
        Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
        spec = Spec(); spec.Resources[0].TemplateXml = Screen.Replace("Unchanged", new string('x', 1024 * 1024));
        spec.Resources[0].Members = Enumerable.Range(0, 8).Select(i => new ComfortMemberSpec { Prototype = "PrototypeField", Name = "Field" + i }).ToArray();
        Assert.Throws<ArgumentException>(() => ComfortPackage.Build(spec));
    }
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "comfort-package-" + Guid.NewGuid().ToString("N"));
        public Scratch() { Directory.CreateDirectory(Path); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
