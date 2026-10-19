using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ExtensionTests
{
    [Theory]
    [InlineData("edit_text_list")]
    [InlineData("change_text_list")]
    public void OlderComfortWorkersCannotSilentlyNormalizeTextValues(string operation)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        string generation = manager.Generation("comfort");
        var descriptor = fixture.Workers.Single().Hello.Operations.Single(o => o.Name == operation);
        descriptor.Route = "internal"; descriptor.SnapshotKind = "";
        var failure = Assert.Throws<InvalidOperationException>(() => manager.Execute("comfort", operation, new { }));
        Assert.Contains("worker 0.14.1+", failure.Message);
        Assert.Equal(generation, manager.Generation("comfort"));
        Assert.False(fixture.Workers.Single().Disposed);
    }

    [Theory]
    [InlineData("comfort", "edit_text_list", true, "literalTextV1", true)]
    [InlineData("comfort", "change_text_list", true, "literalTextV1", true)]
    [InlineData("comfort", "edit_text_list", false, "literalTextV1", false)]
    [InlineData("unified", "edit_text_list", true, "literalTextV1", false)]
    [InlineData("comfort", "edit_xml", true, "literalTextV1", false)]
    [InlineData("comfort", "edit_text_list", true, "", false)]
    public void LiteralTextRouteRequiresTheExactInternalContract(string module, string name, bool preview, string kind, bool valid)
    {
        var operation = new ExtensionOperation { Name = name, Route = "internalText", Preview = preview, SnapshotKind = kind,
            InputSchema = ExtensionWire.Json(new { type = "object" }) };
        Assert.Equal(valid, operation.IsCompatible(module));
    }

    [Fact] public void RenamedRootPreservesLegacySelectionUntilNewRootExists()
    {
        using var fixture = new Fixture();
        string current = Path.Combine(fixture.Root, "SiemensMCPServer", "Extensions");
        string legacy = Path.Combine(fixture.Root, "SiemensTiaOpenness", "Extensions");
        Assert.Equal(current, ExtensionPaths.DefaultRoot(fixture.Root));
        Directory.CreateDirectory(legacy);
        Assert.Equal(current, ExtensionPaths.DefaultRoot(fixture.Root));
        File.WriteAllText(Path.Combine(legacy, "active.json"), "{}");
        Assert.Equal(legacy, ExtensionPaths.DefaultRoot(fixture.Root));
        Directory.CreateDirectory(current);
        Assert.Equal(current, ExtensionPaths.DefaultRoot(fixture.Root));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(legacy, "active.json")));
    }
    [Fact] public void LegacyWorkerNameRetainsPayloadAndReloadGuards()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Bundled, "comfort");
        const string legacy = "SiemensTiaOpenness.ExtensionWorker.exe";
        File.Move(Path.Combine(directory, "SiemensMCPServer.ExtensionWorker.exe"), Path.Combine(directory, legacy));
        fixture.Manifest(directory, "comfort", "0.7.0", legacy);
        using var manager = fixture.Manager();
        string generation = manager.Generation("comfort");
        File.AppendAllText(Path.Combine(directory, legacy), "tamper");
        Assert.Throws<InvalidOperationException>(() => manager.Reload("comfort", () => throw new Exception("must not invalidate")));
        Assert.Equal(generation, manager.Generation("comfort"));
        Assert.False(fixture.Workers.Single().Disposed);
    }
    [Fact] public void NewRecipeAddsOperationWithoutChangingHostToolSchema()
    {
        var recipes = new FeatureRecipes("comfort", "[{\"name\":\"grid_preset\",\"description\":\"Set editor grid\",\"xPath\":\"//GridColor\",\"newValue\":\"1, 2, 3\"}]");
        Assert.Equal("classicXml", recipes.Operations.Single().Route);
        string xml = "<Document><Hmi.Screen.Screen><AttributeList><Name>S</Name><GridColor>0, 0, 0</GridColor></AttributeList></Hmi.Screen.Screen></Document>";
        Assert.True(recipes.TryExecute("grid_preset", ExtensionWire.Json(new { snapshot = xml, arguments = new { expectedValue = "0, 0, 0" } }), out var result));
        Assert.Contains("<GridColor>1, 2, 3</GridColor>", result.GetProperty("candidateXml").GetString());
        Assert.Throws<InvalidOperationException>(() => recipes.TryExecute("grid_preset", ExtensionWire.Json(new { snapshot = xml, arguments = new { expectedValue = "stale" } }), out _));
        Assert.Throws<ArgumentException>(() => new FeatureRecipes("unified", "[{\"name\":\"preset\",\"xPath\":\"//Name\",\"newValue\":\"x\"}]"));
    }
    [Fact] public void GenerationBoundPlansCannotReplayOrSurviveReload()
    {
        var plans = new ExtensionPlanStore();
        plans.Add("one", new ExtensionPlanBinding { Module = "comfort", Generation = "first", Hash = "hash", Route = "classicXml" });
        Assert.Throws<InvalidOperationException>(() => plans.Take("one", "wrong", _ => "first"));
        Assert.Throws<InvalidOperationException>(() => plans.Take("one", "hash", _ => "second"));
        Assert.Equal("classicXml", plans.Take("one", "hash", _ => "first").Route);
        Assert.Throws<InvalidOperationException>(() => plans.Take("one", "hash", _ => "first"));
        plans.Add("two", new ExtensionPlanBinding { Generation = "first", Hash = "hash" }); plans.Clear();
        Assert.Throws<InvalidOperationException>(() => plans.Take("two", "hash", _ => "first"));
    }
    [Fact] public void ReloadSwapsWorkersAndKeepsPreviousOnInvalidUpdate()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        string first = manager.Generation("comfort"); var old = fixture.Workers.Single();
        int invalidations = 0;
        fixture.Bundle("comfort", "0.7.1"); manager.Reload("comfort", () => invalidations++);
        string second = manager.Generation("comfort"); Assert.NotEqual(first, second); Assert.True(old.Disposed); Assert.Equal(1, invalidations);
        File.AppendAllText(Path.Combine(fixture.Bundled, "comfort", "SiemensMCPServer.ExtensionWorker.exe"), "tamper");
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
    [Fact] public void NewBundledModulesAreDiscoveredAndReloadedWithTheExistingFacade()
    {
        using var fixture = new Fixture(); fixture.Bundle("project", "0.8.0");
        File.WriteAllText(Path.Combine(fixture.Bundled, "active.json"), "{\"project\":\"project\"}");
        using var manager = fixture.Manager();
        var listed = ExtensionWire.Json(manager.List());
        Assert.Equal(4, listed.GetProperty("modules").GetArrayLength());
        string previous = manager.Generation("project");
        var reloaded = ExtensionWire.Json(manager.Reload("", () => { }));
        Assert.Contains("project", reloaded.GetProperty("reloaded").EnumerateArray().Select(e => e.GetString()));
        Assert.NotEqual(previous, manager.Generation("project"));
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
        File.WriteAllText(Path.Combine(fixture.External, "releases", "comfort", "0.7.2", "SiemensMCPServer.ExtensionWorker.exe"), "new source bits");
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
            File.WriteAllText(Path.Combine(dir, "SiemensMCPServer.ExtensionWorker.exe"), "fixture " + version); Manifest(dir, module, version);
        }
        public void Manifest(string dir, string module, string version, string entryPoint = "SiemensMCPServer.ExtensionWorker.exe") => File.WriteAllText(Path.Combine(dir, "extension.json"), JsonSerializer.Serialize(new ExtensionManifest {
            Format = "tia-extension-v1", Protocol = 1, Module = module, Version = version, EntryPoint = entryPoint,
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
        public bool Healthy => !Disposed;
        public FakeWorker(ExtensionBundle bundle) { Directory = bundle.Directory; Hello = new ExtensionHello { Protocol = 1, Module = bundle.Manifest.Module, Version = bundle.Manifest.Version, Operations = FeatureOperations.Describe(bundle.Manifest.Module) }; }
        public JsonElement Call(string operation, object input) => FeatureOperations.Execute(Hello.Module, operation, ExtensionWire.Json(input));
        public void Dispose() { Disposed = true; }
    }
}
