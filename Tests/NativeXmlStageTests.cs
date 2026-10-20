using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class NativeXmlStageTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.local", "stage-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
    public NativeXmlStageTests() { Directory.CreateDirectory(root); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void UsesPrivateBoundedPhysicalPathAndCleansOnlyItsOwnDirectory()
    {
        byte[] xml = NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture());
        string marker = Path.Combine(root, "unrelated.txt"); File.WriteAllText(marker, "keep");
        NativeStagingReport report;
        string operation;
        using (var stage = NativeXmlStage.Create(roots: new[] { root }))
        {
            operation = stage.DirectoryPath; report = stage.Report;
            stage.Write(xml, "V15.1", true);
            Assert.True(File.Exists(stage.Report.NativePath));
            Assert.True(stage.Report.NativePath.Length <= 140);
            Assert.Equal(xml, stage.Read());
            Assert.Equal(TextListDocument.Hash(xml), stage.Report.PayloadSha256);
            Assert.False(stage.Report.SettingsChanged);
            Assert.False(stage.Report.NativeDispatched);
        }
        Assert.False(Directory.Exists(operation));
        Assert.True(report.CleanupSucceeded);
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public void AnotherProcessCanReadTheExactResolvedPayload()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
        byte[] xml = NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture());
        using var stage = NativeXmlStage.Create(roots: new[] { root }); stage.Write(xml);
        var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"[Convert]::ToBase64String([IO.File]::ReadAllBytes($env:SIEMENS_STAGE_TEST_FILE))\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.EnvironmentVariables["SIEMENS_STAGE_TEST_FILE"] = stage.Report.NativePath;
        using var process = Process.Start(info)!;
        string actual = process.StandardOutput.ReadToEnd().Trim();
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(Convert.ToBase64String(xml), actual);
    }

    [Fact]
    public void RefusesInvalidEncodingAndDetectsChangedStagingBeforeDispatch()
    {
        using var stage = NativeXmlStage.Create(roots: new[] { root });
        Assert.Throws<InvalidDataException>(() => stage.Write(NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture(), "<?xml version='1.0' encoding='utf8'?>")));
        Assert.False(stage.Report.NativeDispatched);
        Assert.False(File.Exists(stage.Report.NativePath));
        byte[] xml = NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture()); stage.Write(xml);
        File.AppendAllText(stage.Report.LogicalPath, "modified");
        Assert.Throws<IOException>(() => stage.Verify(xml));
    }

    [Fact]
    public void FailsBeforeNativeWorkWhenNoShortRootIsAvailable()
    {
        Assert.Throws<IOException>(() => NativeXmlStage.Create(roots: new[] { root }, maximumPath: 1));
        Assert.Empty(Directory.GetDirectories(root));
        Assert.Throws<ArgumentException>(() => NativeXmlStage.Create("../escape.xml", new[] { root }));
    }
}
