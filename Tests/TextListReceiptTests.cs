using System;
using System.IO;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class TextListReceiptTests
{
    private sealed class Backend : ITextListBackend
    {
        public string Xml = NativeXmlValidationTests.NativeFixture();
        public bool WrongReadback;
        public int Imports;
        public string ReadTextList(string hmiId, string listName) => Xml;
        public void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml)
        { Imports++; Xml = WrongReadback ? candidateXml.Replace("Reviewed", "Unexpected") : candidateXml; }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void RetainsScopedReadbackOrPrecisePostImportUncertaintyWithoutReplay(bool wrong)
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-receipt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new Backend { WrongReadback = wrong };
            var workflow = new TextListWorkflow(backend, root, identity: () => new { projectPath = @"C:\Project\Fixture.ap15_1", processId = 1234 });
            var preview = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "Messages_TL", new TextListDocument(backend.Xml).Fingerprint,
                new[] { new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = "Reviewed" } }), JsonDefaults.Options);
            Assert.True(preview.GetProperty("validation").GetProperty("validation").GetProperty("canPublish").GetBoolean());
            string id = preview.GetProperty("planId").GetString()!, hash = preview.GetProperty("candidateSha256").GetString()!;
            if (wrong) Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
            else
            {
                var result = JsonSerializer.SerializeToElement(workflow.Apply(id, hash), JsonDefaults.Options);
                Assert.Equal("matched", result.GetProperty("nativeReadback").GetString());
                Assert.Equal(@"C:\Project\Fixture.ap15_1", result.GetProperty("projectIdentity").GetProperty("projectPath").GetString());
                Assert.Equal(TextListDocument.Hash(File.ReadAllBytes(result.GetProperty("readbackFile").GetString()!)), result.GetProperty("readbackSnapshotSha256").GetString());
            }
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(preview.GetProperty("artifactDirectory").GetString()!, "apply.json")));
            Assert.Equal(wrong ? "native-import-returned-readback-unverified" : "applied", receipt.RootElement.GetProperty("state").GetString());
            Assert.True(receipt.RootElement.GetProperty("nativeImportReturned").GetBoolean());
            Assert.False(receipt.RootElement.GetProperty("projectSaved").GetBoolean());
            Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
            Assert.Equal(1, backend.Imports);
            var status = JsonSerializer.SerializeToElement(workflow.Status(), JsonDefaults.Options);
            Assert.Empty(status.GetProperty("pendingPlans").EnumerateArray());
            Assert.Equal(id, status.GetProperty("lastAttempt").GetProperty("planId").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
