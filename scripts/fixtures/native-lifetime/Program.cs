// Harmless net48 fixture. No MCP, vendor API, feature worker or service.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using SiemensMCPServer.Mcp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try {
            if (args[0] == "child") { Console.WriteLine("child-ready"); Console.OpenStandardInput().CopyTo(Console.OpenStandardOutput()); return 0; }
            OwnedWorkerJob.Install(3, 256);
            NativeHostLifetime.Install(2, 1);
            var stop = new ManualResetEventSlim();
            NativeHostLifetime.BindStop(() => stop.Set());
            Console.WriteLine(JsonSerializer.Serialize(new { identity = NativeHostLifetime.Identity, mode = args[0] }));
            if (args[0] == "parent-wait") { stop.Wait(); return NativeHostLifetime.ExitCode; }
            if (args[0] == "force-parent") { Thread.Sleep(Timeout.Infinite); return 2; }
            if (args[0] == "force-eof") { new NativeInputStream(Console.OpenStandardInput(), NativeHostLifetime.InputEnded).CopyTo(Console.OpenStandardOutput()); Thread.Sleep(Timeout.Infinite); return 2; }
            if (args[0] == "tree") {
                using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"child") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true })) {
                    Console.WriteLine(JsonSerializer.Serialize(new { child = child.Id, ready = child.StandardOutput.ReadLine() }));
                    Thread.Sleep(Timeout.Infinite);
                }
            }
            new NativeInputStream(Console.OpenStandardInput(), NativeHostLifetime.InputEnded).CopyTo(Console.OpenStandardOutput());
            return NativeHostLifetime.ExitCode;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
