// Harmless net48 fixture. No MCP, vendor API, feature worker or service.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using SiemensMCPServer.Mcp;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job,int kind,IntPtr info,uint length,IntPtr returned);
    private static object Job()
    {
        var type=typeof(OwnedWorkerJob); var layout=type.GetNestedType("Extended",BindingFlags.NonPublic)!;
        int size=Marshal.SizeOf(layout); IntPtr buffer=Marshal.AllocHGlobal(size);
        try {
            IntPtr handle=(IntPtr)type.GetField("job",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            if(!QueryInformationJobObject(handle,9,buffer,(uint)size,IntPtr.Zero))throw new InvalidOperationException("Cannot query fixture Job");
            object value=Marshal.PtrToStructure(buffer,layout)!;object basic=layout.GetField("Basic")!.GetValue(value)!;
            using(var process=Process.GetCurrentProcess())return new {processLimit=basic.GetType().GetField("ActiveProcessLimit")!.GetValue(basic),memoryBytes=((UIntPtr)layout.GetField("JobMemoryLimit")!.GetValue(value)!).ToUInt64(),privateBytes=process.PrivateMemorySize64,workingSetBytes=process.WorkingSet64};
        } finally {Marshal.FreeHGlobal(buffer);}
    }
    private static int Main(string[] args)
    {
        try {
            if (args[0] == "child") { Console.WriteLine("child-ready"); Console.OpenStandardInput().CopyTo(Console.OpenStandardOutput()); return 0; }
            bool workspace = args[0] == "workspace";
            OwnedWorkerJob.Install(workspace ? 1 : 3, workspace ? 128 : 256);
            NativeHostLifetime.Install(workspace ? 6 : 2, 1, workspace ? "Local\\SiemensMCPServer-HarmlessMetadata-v3-" : "Local\\SiemensMCPServer-HarmlessNativeLifetime-v2-");
            if (workspace) NativeWorkAdmission.Configure(1, 3, 256, 128, "Local\\SiemensMCPServer-HarmlessWork-v3-");
            var stop = new ManualResetEventSlim();
            NativeHostLifetime.BindStop(() => stop.Set());
            Console.WriteLine(JsonSerializer.Serialize(new { identity = NativeHostLifetime.Identity, mode = args[0] }));
            if (workspace) {
                Process? child = null;
                string? line;
                while ((line = Console.ReadLine()) != null) {
                    try {
                        if (line == "metadata") { }
                        else if (line == "work") NativeWorkAdmission.EnsureActive();
                        else if (line == "release") NativeWorkAdmission.ReleaseAfterDrain();
                        else if (line == "child") {
                            child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"child") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true });
                            if (child!.StandardOutput.ReadLine() != "child-ready") throw new InvalidOperationException("Child readiness failed");
                        }
                        else if (line == "close-child") { child!.StandardInput.Close(); if (!child.WaitForExit(2000)) throw new InvalidOperationException("Child exit unconfirmed"); child.Dispose(); child=null; }
                        else throw new ArgumentException("Unsupported fixture command");
                        Console.WriteLine(JsonSerializer.Serialize(new { execution = NativeWorkAdmission.Snapshot(), childPid=child?.Id,job=Job() }));
                    } catch(Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { error=ex.Message,execution=NativeWorkAdmission.Snapshot(),job=Job() })); }
                }
                NativeHostLifetime.InputEnded(); return NativeHostLifetime.ExitCode;
            }
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
