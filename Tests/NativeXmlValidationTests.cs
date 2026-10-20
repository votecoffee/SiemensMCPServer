using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class NativeXmlValidationTests
{
    internal static string NativeFixture()
    {
        var doc = XDocument.Parse(TextListTests.Fixture());
        foreach (var text in doc.Descendants("MultilingualTextItem").Elements("AttributeList").Elements("Text"))
        {
            string value = text.Value;
            text.ReplaceNodes(new XElement("body", new XElement("p", value)));
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    internal static byte[] Utf8(string xml, string declaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>", bool bom = false)
        => (bom ? new byte[] { 0xef, 0xbb, 0xbf } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(declaration + "\r\n" + xml)).ToArray();

    [Theory]
    [InlineData("utf8", "'", false)][InlineData("UTF8", "\"", false)][InlineData("UtF8", "'", true)]
    public void RejectsNativeAliasEvenWhenStringParserAcceptsIt(string spelling, string quote, bool bom)
    {
        var bytes = Utf8(NativeFixture(), "<?xml version='1.0' encoding = " + quote + spelling + quote + "?>", bom);
        Assert.NotNull(XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\ufeff')).Root);
        var result = NativeXmlValidation.Analyze(bytes, "V15.1", true);
        Assert.False(result.CanPublish);
        Assert.Contains(result.Checks, c => c.Id == "xml.encoding.native-alias" && c.Status == "failed");
    }

    [Theory]
    [InlineData("utf-8", false)][InlineData("UTF-8", false)][InlineData("UtF-8", true)]
    public void CanonicalUtf8WithQuotesCaseAndBomPasses(string spelling, bool bom)
    {
        var result = NativeXmlValidation.Analyze(Utf8(NativeFixture(), "<?xml version='1.0' encoding='" + spelling + "'?>", bom), "V15.1", true);
        Assert.True(result.CanPublish, result.Summary + string.Join(";", result.Checks.Where(c => c.Status != "passed").Select(c => c.Message)));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void PreservesLegitimateNativeUtf16Bytes(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("<?xml version='1.0' encoding='utf-16'?>" + NativeFixture())).ToArray();
        var result = NativeXmlValidation.Analyze(bytes, "V15.1", true);
        Assert.True(result.CanPublish);
        Assert.Equal(bigEndian ? "utf-16be" : "utf-16le", result.SourceEncoding);
        Assert.Equal(bytes, NativeXmlValidation.RepairDeclaration(bytes));
    }

    [Theory]
    [InlineData("'", false)][InlineData("\"", true)]
    public void RepairChangesOnlyDeclarationBytesAndKeepsBodyAndSource(string quote, bool bom)
    {
        byte[] source = Utf8(NativeFixture(), "<?xml version='1.0' encoding=" + quote + "UTF8" + quote + "?>", bom);
        byte[] frozen = source.ToArray(), repaired = NativeXmlValidation.RepairDeclaration(source);
        Assert.Equal(frozen, source);
        Assert.NotEqual(TextListDocument.Hash(source), TextListDocument.Hash(repaired));
        string before = Encoding.UTF8.GetString(source), after = Encoding.UTF8.GetString(repaired);
        Assert.Equal(before.Substring(before.IndexOf("?>", StringComparison.Ordinal) + 2), after.Substring(after.IndexOf("?>", StringComparison.Ordinal) + 2));
        Assert.True(NativeXmlValidation.Analyze(repaired, "V15.1", true).CanPublish);
        Assert.Equal(bom, repaired.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
    }

    [Theory]
    [InlineData("<Document>")]
    [InlineData("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///secret'>]><Document>&x;</Document>")]
    public void RejectsTruncationAndExternalEntities(string xml)
        => Assert.False(NativeXmlValidation.Analyze(Utf8(xml), "V15.1", true).CanPublish);

    [Fact]
    public void RejectsMalformedUtf8WithoutReplacementDecoding()
    {
        byte[] bytes = Utf8(NativeFixture()); bytes[bytes.Length - 4] = 0xff;
        Assert.False(NativeXmlValidation.Analyze(bytes).CanPublish);
        Assert.Throws<InvalidDataException>(() => NativeXmlValidation.Read(bytes));
    }

    [Fact]
    public void RejectsDeclarationByteMismatchAndUnsupportedTarget()
    {
        Assert.False(NativeXmlValidation.Analyze(Utf8(NativeFixture(), "<?xml version='1.0' encoding='utf-16'?>"), "V15.1", true).CanPublish);
        var other = NativeXmlValidation.Analyze(Utf8(NativeFixture().Replace("V15.1", "V19")), "V19", true);
        Assert.Equal("unsupported", other.Status);
        Assert.False(other.CanPublish);
    }

    [Fact]
    public void RequiredNativeMetadataBlocksIdentityIdsAndPlainTextFailures()
    {
        var duplicate = NativeFixture().Replace("ID=\"1A\"", "ID=\"0\"");
        Assert.Contains(NativeXmlValidation.Analyze(Utf8(duplicate), "V15.1", true).Checks, c => c.Id == "metadata.unique-ids" && c.Status == "failed");
        Assert.Contains(NativeXmlValidation.Analyze(Utf8(TextListTests.Fixture()), "V15.1", true).Checks, c => c.Id == "metadata.native-text" && c.Status == "failed");
        Assert.False(NativeXmlValidation.Analyze(Utf8(NativeFixture().Replace("<Engineering version=\"V15.1\" />", "")), "V15.1", true).CanPublish);
    }

    [Fact]
    public void TransferInventoryAutomaticallyChecksDeclarationBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "payload.xml"), Utf8(NativeFixture(), "<?xml version='1.0' encoding='utf8'?>"));
            Assert.Throws<InvalidDataException>(() => TransferFiles.Inventory(root));
            Assert.Single(TransferFiles.Inventory(root, validateXml: false));
        }
        finally { Directory.Delete(root, true); }
    }
}
