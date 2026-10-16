using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SiemensTiaOpenness.Mcp;
using Xunit;

public sealed class DirectoryTransferTests
{
    [Fact] public void FilenameOnlyImportUsesActualConfigurationName()
    {
        using var temp = new Scratch(); temp.Write("Reviewed.hmi.yml", "Tags: []"); temp.Write("Reviewed.TextLibrary.hmi.yml", "Texts: []");
        var node = new NamedImport(); DirectoryTransferNative.Import(node, typeof(NamedImport).GetMethod("Import")!, temp.Path, true);
        Assert.Equal("Reviewed.hmi.yml", node.Name);
    }
    [Fact] public void FilenameRouteRejectsAmbiguityBeforeInvokingNative()
    {
        using var temp = new Scratch(); temp.Write("A.hmi.yml", "Tags: []"); temp.Write("B.hmi.yml", "Tags: []");
        var node = new NamedImport(); Assert.Throws<ArgumentException>(() => DirectoryTransferNative.Import(node, typeof(NamedImport).GetMethod("Import")!, temp.Path, true)); Assert.Null(node.Name);
        Assert.Equal(2, DirectoryTransferNative.Validate("native", temp.Path, false).Length);
    }
    [Fact] public void DeferredExportsFinishAndNamedExportsUseNativeExtension()
    {
        using var temp = new Scratch(); var node = new DeferredExport();
        DirectoryTransferNative.Export(node, typeof(DeferredExport).GetMethod("Export")!, temp.Path);
        Assert.True(node.Finished); Assert.True(File.Exists(System.IO.Path.Combine(temp.Path, "payload.hmi.yml")));
    }
    [Theory]
    [InlineData("Siemens.Engineering.HmiUnified.Scripts.HmiScriptModuleComposition", "Module.hmi.yml", "Module.hmi.js")]
    [InlineData("Siemens.Engineering.HmiUnified.Scripts.HmiScriptModuleComposition", "Module.yml", "Module.js")]
    [InlineData("Siemens.Engineering.HmiUnified.TextGraphicList.HmiTextListComposition", "Textlists.hmi.yml", "Textlists.TextLibrary.hmi.yml")]
    public void RequiredNativeCompanionsCannotBeOmitted(string type, string config, string companion)
    {
        using var temp = new Scratch(); temp.Write(config, "Items: []");
        Assert.Throws<ArgumentException>(() => DirectoryTransferNative.Validate(type, temp.Path, true));
        temp.Write(companion, companion.EndsWith(".yml") ? "Texts: []" : "export function f() {}\n");
        Assert.Equal(config, Assert.Single(DirectoryTransferNative.Validate(type, temp.Path, true)));
    }
    [Fact] public void InvalidYamlCompanionOnlyAndUnacknowledgedScopeAreRejected()
    {
        using var temp = new Scratch(); temp.Write("A.TextLibrary.hmi.yml", "Texts: []");
        Assert.Throws<ArgumentException>(() => DirectoryTransferNative.Validate("native", temp.Path, false));
        temp.Write("A.hmi.yml", "Tags: [unterminated"); Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => DirectoryTransferNative.Validate("native", temp.Path, false));
        var node = new NamedImport(); Assert.Throws<ArgumentException>(() => DirectoryTransferNative.Import(node, typeof(NamedImport).GetMethod("Import")!, temp.Path, false)); Assert.Null(node.Name);
    }
    [Fact] public void FalseErrorsAndUnknownResultStatesFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => DirectoryTransferNative.Complete(false));
        Assert.Throws<InvalidOperationException>(() => DirectoryTransferNative.Complete(new ImportResult { State = "Error" }));
        Assert.Throws<InvalidOperationException>(() => DirectoryTransferNative.Complete(new ImportResult { State = "Unexpected" }));
        DirectoryTransferNative.Complete(new ImportResult { State = "Success" }); DirectoryTransferNative.Complete(new ImportResult { State = "Warning" }); DirectoryTransferNative.Complete(null);
    }
    public sealed class NamedImport
    {
        public string? Name; public bool Import(DirectoryInfo directory, string name) { Name = name; return true; }
    }
    public sealed class DeferredExport
    {
        public bool Finished;
        public IEnumerable<FileInfo> Export(DirectoryInfo directory, string name)
        {
            string path = System.IO.Path.Combine(directory.FullName, name); File.WriteAllText(path, "Tags: []");
            yield return new FileInfo(path); Finished = true;
        }
    }
    public sealed class ImportResult { public string State { get; set; } = ""; }
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "directory-transfer-" + Guid.NewGuid().ToString("N"));
        public Scratch() { Directory.CreateDirectory(Path); }
        public void Write(string name, string content) => File.WriteAllText(System.IO.Path.Combine(Path, name), content);
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
