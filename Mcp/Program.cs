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
                if (args.Length == 1 && args[0] == "--version") {
                    Console.WriteLine(AppRelease.Identity(typeof(Program).Assembly));
                    return 0; // no SDK host, native resource owner, worker or TIA API is initialized
                }
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
                int metadataMemory = Math.Min(initial.MetadataJobMemoryMiB, initial.MaxOwnedJobMemoryMiB);
                // Only the separately compiled IHmiSession fixture supplies a
                // factory. Production exposes no namespace/fixture switch.
                string? testNamespace = sessionFactory == null ? null : "Local\\SiemensMCPServer-AdapterFixture-v2-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "-";
                OwnedWorkerJob.Install(1, metadataMemory);
                NativeHostLifetime.Install(initial.MaxMetadataFrontends, initial.StdioDrainTimeoutSeconds, testNamespace == null ? null : testNamespace + "metadata-");
                NativeWorkAdmission.Configure(initial.MaxMcpFrontends, initial.MaxProcessTreeSize, initial.MaxOwnedJobMemoryMiB, metadataMemory, testNamespace == null ? null : testNamespace + "work-");
                ResourceOwnership.Initialize(initial.MaxOwnedResources);
                if (!explicitArtifactRoot && initial.McpArtifactRoot.Length != 0) root = initial.McpArtifactRoot;
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                // Container ownership drains the STA queue and disposes workers before host exit.
                OpennessRuntime? runtimeInstance = null;
                builder.Services.AddSingleton(_ => runtimeInstance = new OpennessRuntime(version, root, writes, extensionsRoot, analysisParallelism ?? initial.AnalysisParallelism, sessionFactory, safeguards));
                builder.Services.AddHostedService<StdioEofShutdown>();
                builder.Services.AddSingleton(new NativeGuidance(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-guidance")));
                builder.Services.AddMcpServer(options => {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = sessionFactory == null ? "siemens-mcp-server" : "siemens-mcp-server-fixture", Version = AppRelease.Identity(typeof(Program).Assembly) };
                    options.ServerInstructions = "Use explicit TIA process, project path, and HMI IDs. Read a text list, preview edits, and inspect the diff before applying authorized changes. Apply never saves; compile and save are separate tools. Classic and capability-checked Unified operations; use tia_get_api_capabilities and the target family. Do not retry an uncertain write; inspect current state first.";
                }).WithRequestFilters(filters => filters.AddListToolsFilter(next => async (request, token) => {
                    var result = await next(request, token).ConfigureAwait(false);
                    // These tool definitions are fixed for this owning server lifetime.
                    // Caller-private hints never cache engineering results or approvals.
                    result.TimeToLive = TimeSpan.FromSeconds(5);
                    result.CacheScope = ModelContextProtocol.Protocol.CacheScope.Private;
                    return result;
                })).WithStreamServerTransport(new NativeInputStream(Console.OpenStandardInput(), () => {
                    runtimeInstance?.BeginStop(); NativeHostLifetime.InputEnded();
                }), Console.OpenStandardOutput()).WithTools<McpTools>().WithTools<NativeGuidanceTools>();
                using (var host = builder.Build()) {
                    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                    lifetime.ApplicationStopping.Register(() => { runtimeInstance?.BeginStop(); NativeHostLifetime.BeginDrain("host-or-stdio-completion"); });
                    NativeHostLifetime.BindStop(lifetime.StopApplication);
                    await host.RunAsync().ConfigureAwait(false);
                }
                return NativeHostLifetime.ExitCode;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
