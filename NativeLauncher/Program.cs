using System;
using System.IO;
using System.Text.Json;

namespace SiemensMCPServer.NativeLauncher
{
    internal static class Program
    {
        // A separate Framework AppDomain supplies the immutable runtime's binding
        // redirects/base directory. It remains the same OS process and stdio pair;
        // the native host installs its own 8-process-capable Job/parent fence.
        public static int Main(string[] args) {
            try {
                if (args.Length > 1 || args.Length == 1 && args[0] != "--version") throw new InvalidDataException("Unsupported native launcher arguments.");
                using var selection = new Selection(AppDomain.CurrentDomain.BaseDirectory);
                string entry = Selection.Safe(selection.Runtime, "SiemensMCPServer.Mcp.exe");
                var setup = new AppDomainSetup { ApplicationBase = selection.Runtime, ConfigurationFile = entry + ".config" };
                var domain = AppDomain.CreateDomain("Siemens reviewed native runtime", null, setup);
                using var generation = args.Length == 0 ? new Generation(selection) : null;
                try {
                    string[] launch = args.Length == 1 ? args : new[] {
                        "--tia-version=" + selection.Data.TiaVersion,
                        "--settings-file=" + Selection.Safe(selection.Root, "settings/safeguards.json"),
                        "--artifact-root=" + Selection.Safe(selection.Root, "state/sessions"),
                        "--extensions-root=" + Selection.Safe(selection.Runtime, "extensions")
                    };
                    if (args.Length == 0 && selection.Data.AllowWrites) { Array.Resize(ref launch, launch.Length + 1); launch[launch.Length - 1] = "--allow-writes"; }
                    if (args.Length == 0) Environment.SetEnvironmentVariable("SIEMENS_MCP_MANAGEMENT_REGISTRY", Selection.Safe(selection.Root, "state/registry"));
                    int code = domain.ExecuteAssembly(entry, launch);
                    generation?.Complete(code); return code;
                } finally { AppDomain.Unload(domain); }
            } catch (Exception ex) {
                Console.Error.WriteLine("Siemens native launcher refused startup/execution: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
