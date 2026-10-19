// Test-only protocol worker. The integration test compiles it into a temporary
// trusted bundle; it is never included in application/plugin packages.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class ConcurrencyWorker
{
    static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--module=concurrency") return 2;
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        var json = new JavaScriptSerializer();
        string root = Environment.GetEnvironmentVariable("SIEMENS_CONCURRENCY_TEST_ROOT");
        if (String.IsNullOrEmpty(root)) return 3;
        string line;
        while ((line = Console.ReadLine()) != null) {
            var request = json.Deserialize<Dictionary<string, object>>(line.TrimStart('\uFEFF'));
            string operation = (string)request["operation"];
            object result = null; string error = null;
            try {
                if (operation == "$hello") result = new {
                    protocol = 1, module = "concurrency", version = "0.0.1",
                    operations = new[] { new { name = "hold", description = "Test gate", route = "local", preview = false, snapshotKind = "",
                        inputSchema = new { type = "object", additionalProperties = false } } }
                };
                else if (operation == "hold") {
                    string marker = Path.Combine(root, "running-" + Process.GetCurrentProcess().Id);
                    File.WriteAllText(marker, "running");
                    try {
                        var timer = Stopwatch.StartNew();
                        while (!File.Exists(Path.Combine(root, "release"))) {
                            if (timer.ElapsedMilliseconds > 25000) throw new TimeoutException("Test gate timed out");
                            Thread.Sleep(10);
                        }
                        result = new { workerProcessId = Process.GetCurrentProcess().Id, completed = true };
                    } finally { File.Delete(marker); }
                } else throw new ArgumentException("Unknown test operation");
            } catch (Exception ex) { error = ex.Message; }
            Console.WriteLine(json.Serialize(new { id = request["id"], result = result, error = error }));
        }
        return 0;
    }
}
