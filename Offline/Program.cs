using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Offline;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
            if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(AppRelease.DisplayVersion); return 0; }
            OfflineOperations.ValidateArguments(args);
            // This binary has no vendor references or process-launch API route.
            // A one-process Job also rejects an accidental future child launch.
            OfflineLifetime.Install();
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
            builder.Services.AddHostedService<StdioEofShutdown>();
            builder.Services.AddMcpServer(options => {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "siemens-mcp-server-offline", Version = AppRelease.DisplayVersion };
                options.ServerInstructions = "Direct offline profile. Discover siemens_offline_catalog, then describe the operation. Supply serialized exported content only. No files, native TIA, attachment, edits, workers, service, controller or shared host. Static evidence never establishes native/runtime acceptance.";
            }).WithStreamServerTransport(new BoundedInputStream(Console.OpenStandardInput()), Console.OpenStandardOutput()).WithTools<OfflineTools>();
            using var host = builder.Build();
            // Bound drain even if a pure analysis fails to observe cancellation.
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Environment.Exit(124)));
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 1; }
    }
}
