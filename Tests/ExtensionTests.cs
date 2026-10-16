using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensTiaOpenness.Extensions;
using SiemensTiaOpenness.Mcp;
using Xunit;

public sealed class ExtensionTests
{
    [Fact] public void ReloadSwapsWorkersAndKeepsPreviousOnInvalidUpdate()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        string first = manager.Generation("comfort"); var old = fixture.Workers.Single();
        int invalidations = 0;
        fixture.Bundle("comfort", "0.7.1"); manager.Reload("comfort", () => invalidations++);
        string second = manager.Generation("comfort"); Assert.NotEqual(first, second); Assert.True(old.Disposed); Assert.Equal(1, invalidations);
        File.AppendAllText(Path.Combine(fixture.Bundled, "comfort", "SiemensTiaOpenness.ExtensionWorker.exe"), "tamper");
        Assert.Throws<InvalidOperationException>(() => manager.Reload("comfort", () => invalidations++));
        Assert.Equal(second, manager.Generation("comfort")); Assert.False(fixture.Workers.Last().Disposed); Assert.Equal(1, invalidations);
    }
    [Fact] public void ReloadAllIsAtomicWhenOneReplacementFails()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var generations = new[] { "comfort", "unified", "plc" }.ToDictionary(m => m, manager.Generation);
        var original = fixture.Workers.ToArray();
        fixture.FailModule = "unified";
        Assert.Throws<IOException>(() => manager.Reload("", () => throw new Exception("must not invalidate")));
        Assert.All(original, w => Assert.False(w.Disposed));
        Assert.True(fixture.Workers.Last().Disposed);
        Assert.All(generations, g => Assert.Equal(g.Value, manager.Generation(g.Key)));
    }
    [Theory]
    [InlineData("../outside")][InlineData("C:/outside")][InlineData("comfort\\other")][InlineData("/outside")]
    public void RegistryCannotPointOutsideExtensionRoot(string relative)
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.External);
        File.WriteAllText(Path.Combine(fixture.External, "active.json"), JsonSerializer.Serialize(new { comfort = relative }));
        using var manager = fixture.Manager(); Assert.Throws<ArgumentException>(() => manager.Generation("comfort"));
    }
    [Fact] public void ExternalVersionIsStagedWithoutLockingItsSource()
    {
        using var fixture = new Fixture(); fixture.Bundle("comfort", "0.7.2", Path.Combine(fixture.External, "releases", "comfort", "0.7.2"));
        File.WriteAllText(Path.Combine(fixture.External, "active.json"), "{\"comfort\":\"releases/comfort/0.7.2\"}");
        using var manager = fixture.Manager(); manager.Generation("comfort");
        var worker = fixture.Workers.Single(); Assert.Equal("0.7.2", worker.Hello.Version); Assert.StartsWith(fixture.Cache, worker.Directory);
        Assert.NotEqual(Path.Combine(fixture.External, "releases", "comfort", "0.7.2"), worker.Directory);
        File.WriteAllText(Path.Combine(fixture.External, "releases", "comfort", "0.7.2", "SiemensTiaOpenness.ExtensionWorker.exe"), "new source bits");
        Assert.False(worker.Disposed);
    }
    [Fact] public void RejectsUnlistedFilesAndSiemensAssemblies()
    {
        using var fixture = new Fixture(); string directory = Path.Combine(fixture.Bundled, "comfort");
        File.WriteAllText(Path.Combine(directory, "extra.txt"), "extra");
        Assert.Throws<ArgumentException>(() => ExtensionBundle.Read(directory, "comfort")); File.Delete(Path.Combine(directory, "extra.txt"));
        File.WriteAllText(Path.Combine(directory, "Siemens.Engineering.dll"), "forbidden"); fixture.Manifest(directory, "comfort", "0.7.0");
        Assert.Throws<ArgumentException>(() => ExtensionBundle.Read(directory, "comfort"));
    }
    [Fact] public void WorkerTransformsUseActualXmlAndRetainOtherFields()
    {
        string xml = "<Document><Hmi.Screen.Screen><AttributeList><Name>S</Name><Width>800</Width><Height>480</Height></AttributeList></Hmi.Screen.Screen></Document>";
        var result = FeatureOperations.Execute("comfort", "edit_xml", ExtensionWire.Json(new { snapshot = xml, arguments = new { edits = new[] { new ClassicXmlEdit { XPath = "//Width", ExpectedValue = "800", NewValue = "900" } } } }));
        Assert.Contains("<Width>900</Width>", result.GetProperty("candidateXml").GetString()); Assert.Contains("<Height>480</Height>", result.GetProperty("candidateXml").GetString());
        Assert.Throws<ArgumentException>(() => FeatureOperations.Execute("comfort", "unknown", ExtensionWire.Json(new { })));
    }
    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "tia-extensions-" + Guid.NewGuid().ToString("N"));
        public string Bundled => Path.Combine(Root, "bundled"); public string External => Path.Combine(Root, "external"); public string Cache => Path.Combine(Root, "cache");
        public string FailModule = ""; public List<FakeWorker> Workers = new List<FakeWorker>();
        public Fixture() { foreach (string module in new[] { "comfort", "unified", "plc" }) Bundle(module, "0.7.0"); }
        public void Bundle(string module, string version, string? target = null)
        {
            string dir = target ?? Path.Combine(Bundled, module); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SiemensTiaOpenness.ExtensionWorker.exe"), "fixture " + version); Manifest(dir, module, version);
        }
        public void Manifest(string dir, string module, string version) => File.WriteAllText(Path.Combine(dir, "extension.json"), JsonSerializer.Serialize(new ExtensionManifest {
            Format = "tia-extension-v1", Protocol = 1, Module = module, Version = version, EntryPoint = "SiemensTiaOpenness.ExtensionWorker.exe",
            Files = TransferFiles.Inventory(dir).Where(f => f.Path != "extension.json").ToArray()
        }, JsonDefaults.Options));
        public ExtensionManager Manager() => new ExtensionManager(Bundled, External, Cache, b => {
            if (b.Manifest.Module == FailModule) throw new IOException("replacement failed");
            var worker = new FakeWorker(b); Workers.Add(worker); return worker;
        });
        public void Dispose() { Directory.Delete(Root, true); }
    }
    private sealed class FakeWorker : IExtensionWorker
    {
        public string Directory; public bool Disposed; public ExtensionHello Hello { get; }
        public int ProcessId => 123;
        public FakeWorker(ExtensionBundle bundle) { Directory = bundle.Directory; Hello = new ExtensionHello { Protocol = 1, Module = bundle.Manifest.Module, Version = bundle.Manifest.Version, Operations = FeatureOperations.Describe(bundle.Manifest.Module) }; }
        public JsonElement Call(string operation, object input) => FeatureOperations.Execute(Hello.Module, operation, ExtensionWire.Json(input));
        public void Dispose() { Disposed = true; }
    }
}
