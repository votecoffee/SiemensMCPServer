using System;
using System.Linq;
using SiemensMCPServer;
using SiemensMCPServer.Analysis;
using SiemensMCPServer.Mcp;
using Xunit;

internal static class ControllerFixture
{
    public const string L5x = @"<RSLogix5000Content TargetType='Controller' SoftwareRevision='35.00'><Controller Name='DemoPLC'>
<DataTypes><DataType Name='Axis'><Members><Member Name='Position' DataType='DINT' Dimension='0'/><Member Name='Flags' DataType='DINT' Dimension='4'/>
<Member Name='Ready' DataType='BIT' Dimension='0' Target='__bits' BitNumber='0'/><Member Name='__bits' DataType='SINT' Hidden='true' Dimension='0'/></Members></DataType></DataTypes>
<Tags><Tag Name='Axes' TagType='Base' DataType='Axis' Dimensions='3'/><Tag Name='Matrix' TagType='Base' DataType='INT' Dimensions='2,4'/>
<Tag Name='Counter' TagType='Base' DataType='DINT' ExternalAccess='Read Only'/><Tag Name='CounterAlias' TagType='Alias' DataType='DINT' AliasFor='Counter'/>
<Tag Name='ReadyAlias' TagType='Alias' DataType='BOOL' AliasFor='Axes[1].Ready'/><Tag Name='AliasAgain' TagType='Alias' DataType='DINT' AliasFor='CounterAlias'/>
<Tag Name='External' TagType='Base' DataType='VendorUnknown'/></Tags>
<Programs><Program Name='Main'><Tags><Tag Name='Counter' TagType='Base' DataType='INT'/><Tag Name='LocalAlias' TagType='Alias' DataType='INT' AliasFor='Counter'/></Tags></Program></Programs>
</Controller></RSLogix5000Content>";
    public const string PlcOpen = @"<project xmlns='http://www.plcopen.org/xml/tc6_0201'><contentHeader name='VendorPLC'/><types><dataTypes>
<dataType name='Record'><baseType><struct><variable name='Value'><type><DINT/></type></variable></struct></baseType></dataType>
<dataType name='Records'><baseType><array><dimension lower='-2' upper='2'/><baseType><derived name='Record'/></baseType></array></baseType></dataType>
</dataTypes><pous/></types><instances><configurations><configuration name='Config'><resource name='Runtime'><globalVars>
<variable name='Axis'><type><derived name='Records'/></type></variable><variable name='Input'><type><BOOL/></type><documentation/></variable>
<variable name='Mapped' address='%IX0.0'><type><BOOL/></type></variable>
</globalVars></resource></configuration></configurations></instances></project>";
    public static ControllerDeclaration Import(string text = L5x, string format = "rockwell-l5x") => ControllerDeclarations.Import(new ControllerDocumentInput { Id = "plc", Format = format, Content = text });
}
public sealed class ControllerDeclarationTests
{
    [Fact] public void ImportsControllerProgramTagsTypesAliasesAccessAndProvenanceWithoutFlatteningArrays()
    {
        var model = ControllerFixture.Import(); Assert.Equal("DemoPLC", model.Name); Assert.Equal("35.00", model.SoftwareRevision); Assert.False(model.PartialExport);
        Assert.Equal(TextListDocument.Hash(ControllerFixture.L5x), model.SourceSha256); Assert.Equal(9, model.Symbols.Length);
        Assert.Equal(3, model.Symbols.Single(s => s.Name == "Axes").Dimensions[0].Upper + 1);
        Assert.Equal("Program:Main.Counter", model.Symbols.Single(s => s.Scope != "" && s.Name == "Counter").QualifiedName);
        Assert.Equal("BOOL", model.Types.Single().Members.Single(m => m.Name == "Ready").DataType);
        Assert.True(model.Types.Single().Members.Single(m => m.Name == "__bits").Hidden);
    }
    [Theory]
    [InlineData("Axes[1].Position", "resolved", "DINT")]
    [InlineData("axes[2].ready", "resolved", "BOOL")]
    [InlineData("Axes[1].Flags[3].31", "resolved", "BOOL")]
    [InlineData("Matrix[1,3]", "resolved", "INT")]
    [InlineData("Axes[3].Position", "arrayBounds", "")]
    [InlineData("Axes[-1].Position", "arrayBounds", "")]
    [InlineData("Axes[0].Flags[4]", "arrayBounds", "")]
    [InlineData("Matrix[1]", "arrayRankMismatch", "")]
    [InlineData("Axes.Position", "arrayIndexRequired", "")]
    [InlineData("Axes[0].Missing", "missingMember", "")]
    [InlineData("Axes[0].__bits", "hiddenMember", "")]
    [InlineData("Counter.32", "bitBounds", "")]
    [InlineData("Counter.-1", "unresolved", "")]
    [InlineData("Axes[Index].Position", "unresolved", "")]
    [InlineData("Axes[1+1].Position", "unresolved", "")]
    [InlineData("External.Member", "unknownType", "")]
    [InlineData("External", "unknownType", "")]
    [InlineData("Missing", "missingSymbol", "")]
    public void ResolvesExactNestedMembersAndLiteralIndicesWithExplicitFailures(string path, string status, string type)
    {
        var result = new ControllerSymbolResolver(ControllerFixture.Import()).Resolve(path);
        Assert.Equal(status, result.Status); Assert.Equal(type, result.DataType);
        Assert.False(result.PhysicalOffsetKnown);
    }
    [Fact] public void ProgramScopesAndAliasChainsRetainIdentityAndAccess()
    {
        var resolver = new ControllerSymbolResolver(ControllerFixture.Import());
        var alias = resolver.Resolve("AliasAgain"); Assert.Equal("resolved", alias.Status); Assert.Equal("Counter", alias.CanonicalSymbol);
        Assert.Equal(new[] { "AliasAgain", "CounterAlias" }, alias.AliasChain); Assert.Equal("Read Only", alias.ExternalAccess);
        Assert.Equal("INT", resolver.Resolve("Program:Main.LocalAlias").DataType);
        Assert.Equal("INT", resolver.Resolve("Counter", "Program:Main").DataType);
        Assert.Equal("DINT", resolver.Resolve("Counter").DataType);
        Assert.Equal("BOOL", resolver.Resolve("ReadyAlias").DataType);
    }
    [Fact] public void AliasCyclesMissingTargetsAndConflictingDeclaredTypesDoNotResolve()
    {
        var text = ControllerFixture.L5x.Replace("AliasFor='Counter'", "AliasFor='AliasAgain'");
        Assert.Equal("aliasCycle", new ControllerSymbolResolver(ControllerFixture.Import(text)).Resolve("AliasAgain").Status);
        text = ControllerFixture.L5x.Replace("AliasFor='Counter'", "AliasFor='Missing'");
        Assert.Equal("missingSymbol", new ControllerSymbolResolver(ControllerFixture.Import(text)).Resolve("CounterAlias").Status);
        text = ControllerFixture.L5x.Replace("Name='CounterAlias' TagType='Alias' DataType='DINT'", "Name='CounterAlias' TagType='Alias' DataType='BOOL'");
        Assert.Equal("aliasTypeMismatch", new ControllerSymbolResolver(ControllerFixture.Import(text)).Resolve("CounterAlias").Status);
    }
    [Fact] public void AliasSuffixesResolveAgainstTheBaseArrayAndMemberDeclarations()
    {
        var text = ControllerFixture.L5x.Replace("AliasFor='Counter'", "AliasFor='Axes'");
        var resolver = new ControllerSymbolResolver(ControllerFixture.Import(text));
        Assert.Equal("DINT", resolver.Resolve("CounterAlias[1].Flags[2]").DataType);
        Assert.Equal("Axes[1].Flags[2]", resolver.Resolve("CounterAlias[1].Flags[2]").CanonicalSymbol);
    }
    [Fact] public void PlcOpenDerivedStructArraysPreserveNonzeroBoundsScopesAndDeclaredAddresses()
    {
        var model = ControllerFixture.Import(ControllerFixture.PlcOpen, "plcopen-xml"); var resolver = new ControllerSymbolResolver(model);
        Assert.Equal("VendorPLC", model.Name); Assert.True(model.PartialExport);
        var result = resolver.Resolve("Config.Runtime.Axis[-2].Value"); Assert.Equal("resolved", result.Status); Assert.Equal("DINT", result.DataType);
        Assert.Equal("arrayBounds", resolver.Resolve("Config.Runtime.Axis[-3].Value").Status);
        Assert.Equal("BOOL", resolver.Resolve("%IX0.0").DataType);
        Assert.Equal("BOOL", resolver.Resolve("Input", "Config.Runtime").DataType);
        Assert.Equal("missingSymbol", resolver.Resolve("Input").Status);
    }
    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY external SYSTEM 'file:///missing'>]><RSLogix5000Content/>")]
    [InlineData("<RSLogix5000Content><Controller Name='P'><Tags><Tag Name='X' TagType='Alias'/></Tags></Controller></RSLogix5000Content>")]
    [InlineData("<RSLogix5000Content><Controller Name='P'><Tags><Tag Name='X' TagType='Base' DataType='DINT' Dimensions='-1'/></Tags></Controller></RSLogix5000Content>")]
    [InlineData("<RSLogix5000Content><Controller Name='P'><Tags><Tag Name='X' TagType='Base' DataType='DINT'/><Tag Name='x' TagType='Base' DataType='INT'/></Tags></Controller></RSLogix5000Content>")]
    public void RejectsUnsafeMalformedAndAmbiguousMetadata(string xml) => Assert.ThrowsAny<Exception>(() => ControllerFixture.Import(xml));
}
