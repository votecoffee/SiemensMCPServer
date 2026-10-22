using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class SafeguardTests
{
    private sealed class Scratch : IDisposable
    {
        internal string Root = Path.Combine(Path.GetTempPath(), "siemens-policy-tests-" + Guid.NewGuid().ToString("N"));
        internal Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact] public void StoresRoundTripPermissionsAndRetainPreviousPolicy()
    {
        using var temp = new Scratch(); var store = new SafeguardStore(Path.Combine(temp.Root, "safeguards.json"));
        var first = store.Load(); Assert.Equal("absent", first.Hash); Assert.True(first.Settings.DesktopWritesEnabled);
        first.Settings.McpWritesEnabled = false; first.Settings.RequiredProjectPath = Path.Combine(temp.Root, "Project.ap15_1");
        var saved = store.Save(first.Settings, first.Hash);
        Assert.False(new SafeguardStore(store.FilePath).Current().McpWritesEnabled);
        saved.Settings.AllowDelete = false; store.Save(saved.Settings, saved.Hash);
        Assert.False(store.Current().AllowDelete);
        Assert.True(new SafeguardStore(store.FilePath + ".previous").Current().AllowDelete);
        Assert.DoesNotContain("identityChecks", File.ReadAllText(store.FilePath));
    }
    [Fact] public void ConcurrentEditorsCannotOverwriteASavedPolicy()
    {
        using var temp = new Scratch(); var a = new SafeguardStore(Path.Combine(temp.Root, "safeguards.json")); var b = new SafeguardStore(a.FilePath);
        var first = a.Load(); var stale = b.Load(); first.Settings.AllowSave = false; a.Save(first.Settings, first.Hash);
        stale.Settings.AllowSave = true;
        Assert.Throws<IOException>(() => b.Save(stale.Settings, stale.Hash)); Assert.False(a.Current().AllowSave);
    }
    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("{\"allowSave\":false,\"allowSave\":true}")]
    [InlineData("{\"allowSave\":false,\"AllowSave\":true}")][InlineData("{\"alllowSave\":false}")]
    [InlineData("{\"schemaVersion\":2}")][InlineData("{\"previewLifetimeMinutes\":0}")]
    [InlineData("{\"nativeQueueCapacity\":1001}")][InlineData("{\"workerTimeoutSeconds\":0}")]
    [InlineData("{\"analysisParallelism\":9}")][InlineData("{\"requiredProjectPath\":\"relative\"}")]
    [InlineData("{\"maxMcpFrontends\":0}")][InlineData("{\"maxMcpFrontends\":17}")]
    public void InvalidPolicyNeverFallsBackOrOverwritesEvidence(string json)
    {
        using var temp = new Scratch(); string path = Path.Combine(temp.Root, "safeguards.json"); File.WriteAllText(path, json);
        var store = new SafeguardStore(path); Assert.ThrowsAny<Exception>(() => store.Current());
        Assert.ThrowsAny<Exception>(() => store.Save(new SafeguardSettings(), "absent")); Assert.Equal(json, File.ReadAllText(path));
    }
    [Fact] public void AnObservedPolicyCannotDisappearIntoPermissiveDefaults()
    {
        using var temp = new Scratch(); var store = new SafeguardStore(Path.Combine(temp.Root, "safeguards.json"));
        var snapshot = store.Load(); snapshot.Settings.McpWritesEnabled = false; store.Save(snapshot.Settings, snapshot.Hash);
        File.Delete(store.FilePath); Assert.Throws<InvalidDataException>(() => store.Current());
    }
    [Fact] public void DesktopAndMcpWritePermissionsAreIndependentCeilings()
    {
        var settings = new SafeguardSettings { McpWritesEnabled = false };
        settings.Require(GuardedOperation.Save); Assert.Throws<InvalidOperationException>(() => settings.Require(GuardedOperation.Save, mcp: true));
        settings.McpWritesEnabled = true; settings.DesktopWritesEnabled = false;
        settings.Require(GuardedOperation.Save, mcp: true); Assert.Throws<InvalidOperationException>(() => settings.Require(GuardedOperation.Save));
        settings.Require(GuardedOperation.ExtensionReload); settings.AllowExtensionReload = false;
        Assert.Throws<InvalidOperationException>(() => settings.Require(GuardedOperation.ExtensionReload, mcp: true));
    }
    [Theory]
    [InlineData("AllowApply", GuardedOperation.Apply)][InlineData("AllowCompile", GuardedOperation.Compile)]
    [InlineData("AllowSave", GuardedOperation.Save)][InlineData("AllowArchive", GuardedOperation.Archive)]
    [InlineData("AllowReopen", GuardedOperation.Reopen)][InlineData("AllowDelete", GuardedOperation.Delete)]
    [InlineData("AllowOverwrite", GuardedOperation.Overwrite)][InlineData("AllowSourceReplacement", GuardedOperation.SourceReplacement)]
    [InlineData("AllowProjectTextImport", GuardedOperation.ProjectTextImport)][InlineData("AllowExtensionReload", GuardedOperation.ExtensionReload)]
    public void EachPermissionBlocksItsOperation(string property, object operationValue)
    {
        var operation = (GuardedOperation)operationValue;
        CheckPermission(property, operation);
    }
    private void CheckPermission(string property, GuardedOperation operation)
    {
        var settings = new SafeguardSettings(); typeof(SafeguardSettings).GetProperty(property)!.SetValue(settings, false);
        Assert.Throws<InvalidOperationException>(() => settings.Require(operation));
        Assert.Throws<InvalidOperationException>(() => settings.Require(operation, mcp: true));
    }
    [Fact] public void PinnedProjectAppliesToSubsequentReadsAndWrites()
    {
        using var temp = new Scratch(); var settings = new SafeguardSettings { RequiredProjectPath = Path.Combine(temp.Root, "One.ap15_1") };
        settings.RequireProject(settings.RequiredProjectPath);
        Assert.Throws<InvalidOperationException>(() => settings.RequireProject(Path.Combine(temp.Root, "Other.ap15_1")));
    }
    [Fact] public void PropertyGridExposesEveryEditableSettingAndKeepsInvariantsReadonly()
    {
        var properties = TypeDescriptor.GetProperties(typeof(SafeguardSettings)).Cast<PropertyDescriptor>().Where(p => p.IsBrowsable).ToArray();
        Assert.Contains(properties, p => p.Name == "McpWritesEnabled" && !p.IsReadOnly);
        Assert.Contains(properties, p => p.Name == "MaxMcpFrontends" && !p.IsReadOnly);
        Assert.Contains(properties, p => p.Name == "IdentityChecks" && p.IsReadOnly);
        foreach (var property in properties) Assert.False(string.IsNullOrEmpty(property.Category));
        var writable = typeof(SafeguardSettings).GetProperties().Where(p => p.CanWrite && p.Name != "SchemaVersion").Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(writable, properties.Where(p => !p.IsReadOnly).Select(p => p.Name).OrderBy(n => n));
    }
    [Fact] public void PreviewRelaxationNeverExtendsAnExistingDeadlineAndTighteningAppliesImmediately()
    {
        var settings = new SafeguardSettings { PreviewLifetimeMinutes = 5, MaxPendingPreviews = 1 };
        var now = DateTime.UtcNow; var policy = new PreviewPolicy(() => settings, () => now);
        var created = now; var deadline = policy.Expires;
        settings.PreviewLifetimeMinutes = 60; now = created.AddMinutes(6); Assert.True(policy.Expired(created, deadline));
        settings.PreviewLifetimeMinutes = 2; now = created.AddMinutes(3); Assert.True(policy.Expired(created, deadline));
        Assert.Throws<InvalidOperationException>(() => policy.RequireCapacity(1));
    }
    [Fact] public void RealTextWorkflowRejectsExpiredPreviewBeforeBackendEffect()
    {
        using var temp = new Scratch(); var backend = new TextBackend(); var settings = new SafeguardSettings { PreviewLifetimeMinutes = 1 };
        var now = DateTime.UtcNow;
        var workflow = new TextListWorkflow(backend, temp.Root, settings: () => settings, utcNow: () => now);
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "Messages_TL", new TextListDocument(backend.Xml).Fingerprint,
            new[] { new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = "Changed" } }));
        now = now.AddMinutes(2); settings.PreviewLifetimeMinutes = 120;
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal(0, backend.Imports);
    }
    [Fact] public void RevokingApplyAfterTextPreviewPreventsTheImport()
    {
        using var temp = new Scratch(); var backend = new TextBackend(); var settings = new SafeguardSettings();
        var workflow = new TextListWorkflow(backend, temp.Root, settings: () => settings);
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "Messages_TL", new TextListDocument(backend.Xml).Fingerprint,
            new[] { new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = "Changed" } }));
        settings.AllowApply = false;
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal(0, backend.Imports);
    }
    private sealed class TextBackend : ITextListBackend
    {
        internal string Xml = TextListTests.Fixture(); internal int Imports;
        public string ReadTextList(string hmiId, string listName) => Xml;
        public void ImportTextList(string hmiId, string listName, string fingerprint, string candidateXml) { Imports++; Xml = candidateXml; }
    }
}
