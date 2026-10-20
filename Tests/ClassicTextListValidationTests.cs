using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class ClassicTextListValidationTests
{
    private static ClassicDeltaValidation Request()
    {
        var before = new TextListDocument(NativeXmlValidationTests.NativeFixture());
        var changes = new[] { new ClassicTextListChange { Action = "addEntry", Entry = "8", TemplateEntry = "7", Translations = new[] {
            new ClassicTranslation { Culture = "en-US", Text = "New entry" }, new ClassicTranslation { Culture = "es-ES", Text = "" } } } };
        var edits = new[] { new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = "Reviewed label" } };
        var after = before.ChangeStructure(changes, new[] { "en-US", "es-ES" }).Edit(edits);
        return new ClassicDeltaValidation { BeforeXml = before.Serialize(), CandidateXml = after.Serialize(), ExpectedBeforeFingerprint = before.Fingerprint,
            ExpectedCandidateSha256 = TextListDocument.Hash(after.Serialize()), Changes = changes, Edits = edits, ActiveCultures = new[] { "en-US", "es-ES" },
            BlankOtherNewTranslations = true, AuthoredCulture = "en-US" };
    }
    private static JsonElement Check(ClassicDeltaValidation request) => FeatureOperations.Execute("comfort", "validate_text_list_delta", ExtensionWire.Json(request));

    [Fact]
    public void AdvertisesCallableNamedValidationProfiles()
    {
        Assert.Contains(FeatureOperations.Describe("comfort"), d => d.Name == "validate_text_list_delta" && d.Route == "local" && !d.Preview);
        var request = Request(); var result = Check(request);
        Assert.Equal("passed", result.GetProperty("validation").GetProperty("status").GetString());
        Assert.Equal("not-run", result.GetProperty("validation").GetProperty("acceptance").GetProperty("nativeImport").GetString());
        Assert.False(result.GetProperty("projectChanged").GetBoolean());
        Assert.False(result.GetProperty("downloadPerformed").GetBoolean());
        Assert.Equal(request.ExpectedCandidateSha256, result.GetProperty("candidateSha256").GetString());
    }

    [Theory]
    [InlineData("Español intacto", "Unrequested translation")]
    [InlineData("Fallback", "Unexpected default")]
    [InlineData("<From>8</From>", "<From>9</From>")]
    public void CompleteSemanticComparisonRejectsUnselectedLanguageDefaultAndKeyChanges(string old, string replacement)
    {
        var request = Request(); request.CandidateXml = request.CandidateXml.Replace(old, replacement); request.ExpectedCandidateSha256 = "";
        Assert.Equal("failed", Check(request).GetProperty("validation").GetProperty("status").GetString());
    }

    [Fact]
    public void RejectsStaleBaselineAndReviewedCandidateHash()
    {
        var request = Request(); request.ExpectedBeforeFingerprint = new string('0', 64);
        Assert.Equal("failed", Check(request).GetProperty("validation").GetProperty("status").GetString());
        request = Request(); request.ExpectedCandidateSha256 = new string('0', 64);
        Assert.Equal("failed", Check(request).GetProperty("validation").GetProperty("status").GetString());
    }

    [Fact]
    public void ExplicitBlankPolicyDoesNotInventTranslations()
    {
        var request = Request(); request.Changes[0].Translations[1].Text = "Invented";
        var before = new TextListDocument(request.BeforeXml);
        request.CandidateXml = before.ChangeStructure(request.Changes, request.ActiveCultures).Edit(request.Edits).Serialize();
        request.ExpectedCandidateSha256 = "";
        Assert.Contains(Check(request).GetProperty("validation").GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "delta.new-translations" && c.GetProperty("status").GetString() == "failed");
    }

    [Fact]
    public void SuppliedNativeExportComparisonDoesNotAssertImportCompileOrSave()
    {
        var request = Request(); request.ReadbackXml = request.CandidateXml;
        var result = Check(request);
        Assert.Equal("passed", result.GetProperty("validation").GetProperty("status").GetString());
        Assert.Equal(result.GetProperty("candidateFingerprint").GetString(), result.GetProperty("readbackFingerprint").GetString());
        Assert.Equal("not-run", result.GetProperty("validation").GetProperty("acceptance").GetProperty("compile").GetString());
        request.ReadbackXml = request.BeforeXml;
        Assert.Equal("failed", Check(request).GetProperty("validation").GetProperty("status").GetString());
    }

    [Fact]
    public void ByteProfileMarksInheritedAliasAsBlockingWithoutExemption()
    {
        string xml = Encoding.UTF8.GetString(NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture(), "<?xml version='1.0' encoding='utf8'?>"));
        var bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        var result = FeatureOperations.Execute("comfort", "validate_native_text_list", ExtensionWire.Json(new ClassicByteValidation { XmlBytesBase64 = bytes, BaselineBytesBase64 = bytes }));
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("canPublish").GetBoolean());
        Assert.Contains(result.GetProperty("checks").EnumerateArray(), c => c.GetProperty("classification").GetString() == "inherited-still-blocking");
    }
}
