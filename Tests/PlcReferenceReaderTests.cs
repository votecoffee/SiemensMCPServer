using System;
using System.Linq;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class PlcReferenceReaderTests
{
    public class Display
    {
        public string Name { get; set; } = "Example";
        public string Address => "%MW0";
        public string TypeName => "Int";
        public string Device => "PLC";
        public string Path => "Program blocks/Example";
        public object UnderlyingObject => throw new Exception("Native handle was accessed");
    }
    public sealed class Source : Display { public Reference[] References { get; set; } = Array.Empty<Reference>(); public Source[] Children { get; set; } = Array.Empty<Source>(); }
    public sealed class Reference : Display { public Location[] Locations { get; set; } = Array.Empty<Location>(); }
    public sealed class Location
    {
        public string Name => "Network 1";
        public string ReferenceLocation => "Statement 2";
        public string ReferenceType => "Direct";
        public string Access => "Read";
        public string ReferencedAsName => "Counter";
        public string Address => "%MW0";
        public string TypeName => "Int";
        public object ReferencedAs => throw new Exception("Native handle was accessed");
    }
    [Fact] public void WalksNestedSourcesReferencesAndLocationsWithoutAccessingNativeHandles()
    {
        var root = new Source { Name = "Parent", Children = new[] { new Source { Name = "Member", References = new[] { new Reference { Locations = new[] { new Location(), new Location() } } } } } };
        var result = PlcReferenceReader.Read(new { Sources = new[] { root } }, 100);
        Assert.Equal(3, result.Rows.Length); Assert.False(result.Truncated);
        Assert.Null(result.Rows[0].Reference); Assert.Equal("Parent", Assert.Single(result.Rows[1].SourceAncestry));
        Assert.Equal("Read", result.Rows[1].Location!["Access"]); Assert.Equal("Member", result.Rows[1].Source["Name"]);
        Assert.DoesNotContain("UnderlyingObject", result.Rows[1].Reference!.Keys);
    }
    [Fact] public void RowLimitAndCyclesCannotProduceUnboundedReplies()
    {
        var result = PlcReferenceReader.Read(new { Sources = Enumerable.Range(0, 4).Select(_ => new Source()).ToArray() }, 2);
        Assert.Equal(2, result.Rows.Length); Assert.True(result.Truncated);
        var cycle = new Source(); cycle.Children = new[] { cycle };
        result = PlcReferenceReader.Read(new { Sources = new[] { cycle } }, 1000);
        Assert.True(result.Truncated); Assert.Equal(32, result.Rows.Length);
    }
    [Fact] public void FieldsAndTotalTextHaveIndependentBoundsAndExplicitTruncation()
    {
        var result = PlcReferenceReader.Read(new { Sources = new[] { new Source { Name = new string('x', 6000) } } }, 10);
        Assert.True(result.FieldsTruncated); Assert.Equal(4096, result.Rows[0].Source["Name"].Length);
        result = PlcReferenceReader.Read(new { Sources = Enumerable.Range(0, 1000).Select(_ => new Source { Name = new string('x', 4000) }).ToArray() }, 1000);
        Assert.True(result.Truncated); Assert.True(result.Rows.Length < 1000);
    }
    [Fact] public void MissingNativeMetadataFailsInsteadOfReportingNoReferences()
    {
        Assert.Throws<NotSupportedException>(() => PlcReferenceReader.Read(new { }, 10));
        Assert.Throws<NotSupportedException>(() => PlcReferenceReader.Read(new { Sources = new[] { new { Name = "Incomplete" } } }, 10));
    }
    [Fact] public void LongAncestorNamesRetainTheSameTextAsTheirSourceRows()
    {
        string name = new string('x', 500);
        var result = PlcReferenceReader.Read(new { Sources = new[] { new Source { Name = name, Children = new[] { new Source() } } } }, 10);
        Assert.Equal(name, Assert.Single(result.Rows[1].SourceAncestry)); Assert.False(result.FieldsTruncated);
    }
}
