using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using SiemensTiaOpenness.Mcp;
using Xunit;

public sealed class NativeCompanionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "native-companions-" + Guid.NewGuid().ToString("N"));
    public NativeCompanionTests() { Directory.CreateDirectory(Path.Combine(root, "payload files")); File.WriteAllBytes(Path.Combine(root, "payload files", "image.emf"), new byte[] { 1, 0, 255, 10 }); }
    private void Xml(string relative) => File.WriteAllText(Path.Combine(root, "payload.xml"), new XDocument(new XElement("Document",
        new XElement("Hmi.Globalization.MultiLingualGraphic", new XElement("AttributeList", new XElement("Name", "Graphic"),
            new XElement("DefaultImageStream", new XAttribute("external", "path"), relative))))).ToString(), new UTF8Encoding(false));
    [Theory]
    [InlineData("payload files\\image.emf")][InlineData("payload files/image.emf")]
    public void AcceptsNativeWindowsPathsAndPreservesCompanionBytes(string relative)
    {
        Xml(relative); TransferFiles.ValidateXmlCompanions(root);
        Assert.Equal(new byte[] { 1, 0, 255, 10 }, File.ReadAllBytes(Path.Combine(root, "payload files", "image.emf")));
    }
    [Theory]
    [InlineData("../outside.emf")][InlineData("..\\outside.emf")][InlineData("C:\\outside.emf")]
    [InlineData("/outside.emf")][InlineData("\\\\server\\share\\image.emf")][InlineData("payload files/../image.emf")]
    [InlineData("payload files/image.emf:stream")][InlineData("")]
    public void RejectsPathsOutsideTheStagedFileSet(string relative)
    {
        Xml(relative); Assert.Throws<ArgumentException>(() => TransferFiles.ValidateXmlCompanions(root));
    }
    [Fact] public void RejectsMissingFilesAndDirectoryReferences()
    {
        Xml("payload files/missing.emf"); Assert.Throws<FileNotFoundException>(() => TransferFiles.ValidateXmlCompanions(root));
        Xml("payload files"); Assert.Throws<FileNotFoundException>(() => TransferFiles.ValidateXmlCompanions(root));
    }
    [Fact] public void NestedXmlUsesItsOwnDirectoryForRelativeReferences()
    {
        Xml("image.emf"); File.Move(Path.Combine(root, "payload.xml"), Path.Combine(root, "payload files", "metadata.xml"));
        TransferFiles.ValidateXmlCompanions(root);
    }
    public void Dispose() { Directory.Delete(root, true); }
}
