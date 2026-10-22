using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

namespace SiemensMCPServer.Mcp
{
    // Observe the transport's public completion task without reading stdin twice.
    // GenericHost must stop when its MCP client goes away, rather than keeping
    // an otherwise idle process alive until a console signal arrives.
    internal sealed class StdioEofShutdown : BackgroundService
    {
        private readonly ITransport transport;
        private readonly IHostApplicationLifetime lifetime;
        public StdioEofShutdown(ITransport transport, IHostApplicationLifetime lifetime)
        { this.transport = transport; this.lifetime = lifetime; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (stoppingToken.Register(() => stopped.TrySetResult(true))) {
                Task completion = transport.MessageReader.Completion;
                if (await Task.WhenAny(completion, stopped.Task).ConfigureAwait(false) == completion) {
                    try { await completion.ConfigureAwait(false); } catch { /* Host cleanup still owns draining. */ }
                    lifetime.StopApplication();
                }
            }
        }
    }
}
