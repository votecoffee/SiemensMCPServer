using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SiemensMCPServer.Mcp
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                string? version = Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION");
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiemensMCPServer", "Mcp", "Sessions");
                bool writes = false;
                string? extensionsRoot = null;
                foreach (string arg in args)
                {
                    if (arg == "--allow-writes") writes = true;
                    else if (arg.StartsWith("--tia-version=", StringComparison.Ordinal)) version = arg.Substring(14);
                    else if (arg.StartsWith("--artifact-root=", StringComparison.Ordinal)) root = arg.Substring(16);
                    else if (arg.StartsWith("--extensions-root=", StringComparison.Ordinal)) extensionsRoot = arg.Substring(18);
                    else throw new ArgumentException("Unknown option: " + arg);
                }
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                builder.Services.AddSingleton(new OpennessRuntime(version, root, writes, extensionsRoot));
                builder.Services.AddMcpServer(options => {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "siemens-mcp-server", Version = "0.9.0" };
                    options.ServerInstructions = "Use explicit TIA process, project path, and HMI IDs. Read a text list, preview edits, and inspect the diff before applying authorized changes. Apply never saves; compile and save are separate tools. Classic and capability-checked Unified operations; use tia_get_api_capabilities and the target family. Do not retry an uncertain write; inspect current state first.";
                }).WithStdioServerTransport().WithTools<McpTools>();
                using (var host = builder.Build()) await host.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
