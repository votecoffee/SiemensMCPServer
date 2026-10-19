using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Mcp;
using Xunit;

public class BrokerPublicationTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void CatalogIsDeterministicInstallationMetadata()
    {
        var publication = new BrokerPublication();
        Assert.Equal(Json(publication.Catalog()).GetRawText(), Json(publication.Catalog()).GetRawText());
        var catalog = Json(publication.Catalog());
        Assert.Equal(1, catalog.GetProperty("schema_version").GetInt32());
        Assert.Equal(4, catalog.GetProperty("capabilities").GetArrayLength());
        foreach (var item in catalog.GetProperty("capabilities").EnumerateArray()) {
            Assert.Contains("workflow", item.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
            Assert.Equal("tia_read_workflow", item.GetProperty("discovery").GetProperty("tool").GetString());
        }
    }

    [Theory]
    [InlineData("edit-hmi-text")]
    [InlineData("edit-siemens-plc")]
    [InlineData("inspect-libraries")]
    [InlineData("inspect-project-texts")]
    public void EmbeddedGuidesHavePinnedRevisionsAndAllReferencesReadBack(string id)
    {
        var publication = new BrokerPublication();
        var hint = Json(publication.Catalog()).GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "workflow." + id);
        string revision = hint.GetProperty("discovery").GetProperty("arguments").GetProperty("expectedRevision").GetString()!;
        var guide = Json(publication.Read(id, revision));
        Assert.Equal(id, guide.GetProperty("id").GetString());
        var instructions = guide.GetProperty("instructions");
        Assert.Equal(BrokerPublication.Hash(instructions.GetProperty("text").GetString()!), instructions.GetProperty("sha256").GetString());
        Assert.NotEmpty(guide.GetProperty("tools").EnumerateArray());
        Assert.InRange(guide.GetProperty("references").GetArrayLength(), 1, 32);
        foreach (var reference in guide.GetProperty("references").EnumerateArray()) {
            string referenceId = reference.GetProperty("id").GetString()!, hash = reference.GetProperty("sha256").GetString()!;
            var read = Json(publication.ReadReference(id, revision, referenceId, hash));
            Assert.Equal(hash, BrokerPublication.Hash(read.GetProperty("content").GetString()!));
            Assert.Throws<ArgumentException>(() => publication.ReadReference(id, revision, referenceId, new string('0', 64)));
        }
        Assert.Throws<ArgumentException>(() => publication.Read(id, "stale"));
        Assert.Throws<ArgumentException>(() => publication.ReadReference(id, revision, "../private.txt", ""));
    }

    [Fact]
    public void ReferencedContentChangesInvalidateWorkflowRevision()
    {
        var docs = new Dictionary<string, string>();
        foreach (string id in new[] { "edit-hmi-text", "edit-siemens-plc", "inspect-libraries", "inspect-project-texts" })
            docs["skills/" + id + "/SKILL.md"] = "---\nname: " + id + "\ndescription: Example\n---\nUse [guide](../../guide.md).";
        docs["guide.md"] = "See [nested](nested.md)."; docs["nested.md"] = "Original";
        var before = new BrokerPublication(docs, "1.0.0");
        string revision = Json(before.Catalog()).GetProperty("capabilities")[0].GetProperty("discovery").GetProperty("arguments").GetProperty("expectedRevision").GetString()!;
        docs["nested.md"] = "Changed";
        var after = new BrokerPublication(docs, "1.0.0");
        Assert.Throws<ArgumentException>(() => after.Read("edit-hmi-text", revision));
        Assert.NotNull(before.Read("edit-hmi-text", revision));
        Assert.Throws<ArgumentException>(() => after.Read("../../other", revision));
    }
}
