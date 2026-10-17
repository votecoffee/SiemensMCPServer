using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class UnifiedTests
{
    private static UnifiedObjectState State() => new UnifiedObjectState {
        HmiId = "devices/HMI/Unified", ScreenPath = "groups/Area/screens/Main", ItemPath = "items/Button_1",
        NativeType = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton",
        Scalars = new[] { new UnifiedScalar { Property = "Left", Type = "System.Int32", Value = "12", Editable = true },
            new UnifiedScalar { Property = "Width", Type = "System.UInt32", Value = "80", Editable = true } },
        Texts = new[] { new UnifiedText { Property = "Text", Culture = "en-US", NativeText = "<body><p><span style=\"font-weight:bold\">Old &amp; ready</span></p></body>", Text = "Old & ready", Editable = true },
            new UnifiedText { Property = "Text", Culture = "es-ES", NativeText = "<body><p>Español intacto</p></body>", Text = "Español intacto", Editable = true } }
    };
    private static UnifiedEdit Caption() => new UnifiedEdit { Property = "Text", Culture = "en-US", ExpectedValue = "Old & ready", NewValue = "New “ready” & café" };

    [Fact]
    public void CaptionEditingPreservesStylingOtherLanguagesAndOriginal()
    {
        var before = State(); string hash = before.Fingerprint;
        var after = before.Edit(new[] { Caption() });
        Assert.Equal(hash, before.Fingerprint);
        Assert.Equal("Español intacto", after.Texts[1].Text);
        Assert.Equal(before.Texts[1].NativeText, after.Texts[1].NativeText);
        var expected = XElement.Parse(before.Texts[0].NativeText);
        expected.Descendants("span").Single().Value = Caption().NewValue;
        Assert.True(XNode.DeepEquals(expected, XElement.Parse(after.Texts[0].NativeText)));
    }

    [Fact]
    public void LayoutEditsUseNativeTypesAndPreserveCaptions()
    {
        var before = State();
        var after = before.Edit(new[] { new UnifiedEdit { Property = "Left", Culture = "", ExpectedValue = "12", NewValue = "-10" } });
        Assert.Equal("-10", after.Scalars[0].Value);
        Assert.Equal(before.Texts[0].NativeText, after.Texts[0].NativeText);
        Assert.Throws<FormatException>(() => before.Edit(new[] { new UnifiedEdit { Property = "Width", Culture = "", ExpectedValue = "80", NewValue = "-1" } }));
    }

    [Fact]
    public void DynamicAndUnknownPropertiesCannotBeEdited()
    {
        var state = State(); state.DynamizedProperties = new[] { "Text" };
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Caption() }));
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { new UnifiedEdit { Property = "Name", Culture = "", ExpectedValue = "Button_1", NewValue = "Renamed" } }));
        state.DynamizedProperties = new[] { "Text.SomeBinding" };
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Caption() }));
    }

    [Fact]
    public void StaleTextMissingLanguageAndDuplicateEditsAreRejected()
    {
        var stale = Caption(); stale.ExpectedValue = "outdated";
        Assert.Throws<InvalidOperationException>(() => State().Edit(new[] { stale }));
        var missing = Caption(); missing.Culture = "de-DE";
        Assert.Throws<InvalidOperationException>(() => State().Edit(new[] { missing }));
        Assert.Throws<ArgumentException>(() => State().Edit(new[] { Caption(), Caption() }));
    }

    [Theory]
    [InlineData("<body><p>A<span>B</span></p></body>")]
    [InlineData("<body><p>A<br/>B</p></body>")]
    [InlineData("<body><p><fieldinfos>dynamic</fieldinfos></p></body>")]
    [InlineData("<!DOCTYPE body [<!ENTITY x SYSTEM 'file:///missing'>]><body><p>&x;</p></body>")]
    public void ComplexAndDynamicRichTextIsReadOnly(string text)
    {
        Assert.False(UnifiedTextCodec.TryRead(text, out _));
        Assert.Throws<InvalidOperationException>(() => UnifiedTextCodec.Replace(text, "new"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unformatted")]
    [InlineData("<body><p /></body>")]
    public void EmptyAndPlainValuesProduceFormattedAssignments(string before)
    {
        string result = UnifiedTextCodec.Replace(before, "<literal> & café");
        Assert.True(UnifiedTextCodec.TryRead(result, out var text));
        Assert.Equal("<literal> & café", text);
        Assert.Equal("body", XElement.Parse(result).Name.LocalName);
    }

    [Fact]
    public void FingerprintIncludesOtherLanguagesBindingsAndTargetIdentity()
    {
        string hash = State().Fingerprint;
        var state = State(); state.Texts[1].NativeText = "<body><p>changed</p></body>";
        Assert.NotEqual(hash, state.Fingerprint);
        state = State(); state.DynamizedProperties = new[] { "Left" }; Assert.NotEqual(hash, state.Fingerprint);
        state = State(); state.HmiId += "/different"; Assert.NotEqual(hash, state.Fingerprint);
    }

    [Fact]
    public void ScalarBoundsAndColorRoundTripAreStrict()
    {
        Assert.Equal("#FF0012AB", UnifiedScalarCodec.Format(UnifiedScalarCodec.Parse("#ff0012ab", "System.Drawing.Color")));
        Assert.Equal("true", UnifiedScalarCodec.Format(UnifiedScalarCodec.Parse("true", "System.Boolean")));
        Assert.Throws<ArgumentException>(() => UnifiedScalarCodec.Parse("1", "System.Boolean"));
        Assert.Throws<OverflowException>(() => UnifiedScalarCodec.Parse("4294967296", "System.UInt32"));
        Assert.Throws<NotSupportedException>(() => UnifiedScalarCodec.Parse("anything", "System.Object"));
    }

    [Fact]
    public void PreviewIsNonMutatingAndApplyIsBoundAndSingleUse()
    {
        using var temp = new Scratch(); var backend = new Backend();
        var workflow = new UnifiedEditWorkflow(backend, temp.Path);
        var preview = Preview(workflow, backend);
        Assert.Equal(0, backend.Writes);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), "wrong"));
        workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString());
        Assert.Equal(1, backend.Writes);
        Assert.Equal(Caption().NewValue, backend.Current.Texts[0].Text);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
    }
    [Fact]
    public void ExtensionCannotBypassBrokerFieldAndIdentityPolicy()
    {
        var before = State(); var candidate = State(); candidate.HmiId = "other";
        Assert.Throws<InvalidOperationException>(() => before.RequireAllowedCandidate(candidate));
        candidate = State(); candidate.Scalars[0].Editable = false;
        Assert.Throws<InvalidOperationException>(() => before.RequireAllowedCandidate(candidate));
        before.Scalars[0].Editable = false; candidate = State(); candidate.Scalars[0].Value = "999";
        Assert.Throws<InvalidOperationException>(() => before.RequireAllowedCandidate(candidate));
        before = State(); candidate = before.Edit(new[] { Caption() }); before.RequireAllowedCandidate(candidate);
    }

    [Fact]
    public void StaleNativeSnapshotAndTamperedCandidateCannotWrite()
    {
        using var temp = new Scratch(); var backend = new Backend();
        var workflow = new UnifiedEditWorkflow(backend, temp.Path); var preview = Preview(workflow, backend);
        backend.Current.Scalars[0].Value = "999";
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
        Assert.Equal(0, backend.Writes);
        backend.Current = State(); preview = Preview(workflow, backend);
        File.AppendAllText(System.IO.Path.Combine(preview.GetProperty("artifactDirectory").GetString(), "candidate.json"), "tampered");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
        Assert.Equal(0, backend.Writes);
    }

    [Fact]
    public void FailedNativeWriteIsNotReplayed()
    {
        using var temp = new Scratch(); var backend = new Backend { Fail = true };
        var workflow = new UnifiedEditWorkflow(backend, temp.Path); var preview = Preview(workflow, backend);
        string id = preview.GetProperty("planId").GetString(), hash = preview.GetProperty("candidateSha256").GetString();
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
        Assert.Equal(0, backend.Writes);
    }

    private static JsonElement Preview(UnifiedEditWorkflow workflow, Backend backend) => JsonSerializer.SerializeToElement(
        workflow.Preview(backend.Current.HmiId, backend.Current.ScreenPath, backend.Current.ItemPath, backend.Current.Fingerprint, new[] { Caption() }));
    private sealed class Backend : IUnifiedBackend
    {
        public UnifiedObjectState Current = State(); public int Writes; public bool Fail;
        public UnifiedObjectState ReadUnified(string hmiId, string screenPath, string itemPath) => Clone(Current);
        public void WriteUnified(UnifiedObjectState before, UnifiedObjectState candidate)
        {
            if (before.Fingerprint != Current.Fingerprint || Fail) throw new InvalidOperationException("Native conflict/failure.");
            Writes++; Current = Clone(candidate);
        }
        private static UnifiedObjectState Clone(UnifiedObjectState state) => JsonSerializer.Deserialize<UnifiedObjectState>(JsonSerializer.Serialize(state));
    }
    private sealed class Scratch : IDisposable
    {
        public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-unified-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
