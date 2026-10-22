using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    internal static class Program
    {
        public static Task<int> Main(string[] args) => RunAsync(args);

        internal static async Task<int> RunAsync(string[] args, Func<string, IFeatureEngine, IHmiSession>? sessionFactory = null)
        {
            try
            {
                string? version = Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION");
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiemensMCPServer", "Mcp", "Sessions");
                bool writes = false;
                int? analysisParallelism = null;
                string? settingsFile = null;
                bool explicitArtifactRoot = false;
                string? extensionsRoot = null;
                foreach (string arg in args)
                {
                    if (arg == "--allow-writes") writes = true;
                    else if (arg.StartsWith("--tia-version=", StringComparison.Ordinal)) version = arg.Substring(14);
                    else if (arg.StartsWith("--artifact-root=", StringComparison.Ordinal)) { root = arg.Substring(16); explicitArtifactRoot = true; }
                    else if (arg.StartsWith("--extensions-root=", StringComparison.Ordinal)) extensionsRoot = arg.Substring(18);
                    else if (arg.StartsWith("--analysis-parallelism=", StringComparison.Ordinal)) {
                        if (!int.TryParse(arg.Substring(23), out int parallelism) || parallelism < 1 || parallelism > 8) throw new ArgumentException("Analysis parallelism must be 1..8.");
                        analysisParallelism = parallelism;
                    }
                    else if (arg.StartsWith("--settings-file=", StringComparison.Ordinal)) settingsFile = arg.Substring(16);
                    else throw new ArgumentException("Unknown option: " + arg);
                }
                var safeguards = new SafeguardStore(settingsFile);
                var initial = safeguards.Current();
                OwnedWorkerJob.Install(initial.MaxProcessTreeSize);
                ResourceOwnership.Initialize(initial.MaxOwnedResources);
                if (!explicitArtifactRoot && initial.McpArtifactRoot.Length != 0) root = initial.McpArtifactRoot;
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                // Container ownership drains the STA queue and disposes workers before host exit.
                builder.Services.AddSingleton(_ => new OpennessRuntime(version, root, writes, extensionsRoot, analysisParallelism ?? initial.AnalysisParallelism, sessionFactory, safeguards));
                builder.Services.AddHostedService<StdioEofShutdown>();
                builder.Services.AddMcpServer(options => {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = sessionFactory == null ? "siemens-mcp-server" : "siemens-mcp-server-fixture", Version = typeof(Program).Assembly.GetName().Version!.ToString(3) };
                    options.ServerInstructions = "Use explicit TIA process, project path, and HMI IDs. Read a text list, preview edits, and inspect the diff before applying authorized changes. Apply never saves; compile and save are separate tools. Classic and capability-checked Unified operations; use tia_get_api_capabilities and the target family. Do not retry an uncertain write; inspect current state first.";
                }).WithRequestFilters(filters => filters.AddListToolsFilter(next => async (request, token) => {
                    var result = await next(request, token).ConfigureAwait(false);
                    // These tool definitions are fixed for this owning server lifetime.
                    // Caller-private hints never cache engineering results or approvals.
                    result.TimeToLive = TimeSpan.FromSeconds(5);
                    result.CacheScope = ModelContextProtocol.Protocol.CacheScope.Private;
                    return result;
                })).WithStdioServerTransport().WithTools<McpTools>();
                using (var host = builder.Build()) await host.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
