// Harmless process fixture only. No MCP, vendor API, service or controller.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using SiemensMCPServer.Offline;

try {
    if (args.Length == 1 && args[0] == "child") return 17;
    OfflineLifetime.Install();
    Console.WriteLine(JsonSerializer.Serialize(new { identity = OfflineLifetime.Identity, mode = args[0] }));
    if (args[0] == "wait") Thread.Sleep(Timeout.Infinite);
    if (args[0] == "child-refusal") {
        try {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "child") { UseShellExecute = false, CreateNoWindow = true });
            child!.WaitForExit(); Console.WriteLine("unexpected-child-created"); return 2;
        } catch (Win32Exception) { Console.WriteLine("child-blocked"); return 0; }
    }
    Console.OpenStandardInput().CopyTo(Console.OpenStandardOutput());
    return 0;
} catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
