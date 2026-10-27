using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SiemensMCPServer.Mcp;
using Xunit;

public class NativeLifecycleDiagnosticsTests
{
    [Fact]
    public void OneHarmlessFilesystemControlBoundsEventsBytesAndKeepsTerminalUnknown()
    {
        string root = Path.Combine(Path.GetTempPath(), "SiemensStageControl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // retained diagnostic fixture; no production directory or actor
        string events = Path.Combine(root, "events.jsonl");
        var recorder = new NativeStageRecorder(events, new { pid = 123, creationFileTime = "fixture" }, 3, 4096);
        recorder.Record("backend-entered"); recorder.Record("stop-requested", new { code = 125 });
        recorder.Record("main-return-intent", new { code = 125 }); recorder.Record("over-event-bound");
        Assert.True(SpinWait.SpinUntil(() => recorder.Flush(250), TimeSpan.FromSeconds(5)));
        var rows = File.ReadAllLines(events); Assert.Equal(3, rows.Length);
        foreach (var row in rows) { using var value = JsonDocument.Parse(row); Assert.Equal("UNKNOWN", value.RootElement.GetProperty("observedTerminal").GetString()); }
        Assert.DoesNotContain(rows, row => row.Contains("over-event-bound"));
        string bounded = Path.Combine(root, "bytes.jsonl");
        var bytes = new NativeStageRecorder(bounded, new { pid = 123 }, 64, 1024);
        for (int i = 0; i < 64; i++) bytes.Record("bounded", new { detail = new string('x', 400) });
        Assert.True(SpinWait.SpinUntil(() => bytes.Flush(250), TimeSpan.FromSeconds(5)));
        Assert.InRange(new FileInfo(bounded).Length, 1, 1024); Assert.True(bytes.Unavailable);
        Assert.Throws<ArgumentException>(() => new NativeStageRecorder(events, new { }, 129));
    }
}
