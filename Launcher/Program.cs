using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using SiemensMCPServer.Offline;

namespace SiemensMCPServer.Launcher;

internal static class Program
{
    private sealed class Runtime : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;
        internal Runtime(string entry) : base("Siemens reviewed offline runtime", false) { resolver = new(entry); }
        protected override Assembly? Load(AssemblyName name) {
            string? path = resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name) {
            // This profile has only OS P/Invokes; no payload-supplied native libraries.
            return IntPtr.Zero;
        }
    }
    public static async Task<int> Main(string[] args)
    {
        string phase = "arguments";
        try {
            if (args.Length > 1 || (args.Length == 1 && args[0] != "--version")) throw new InvalidDataException("Unsupported launcher arguments.");
            // Same surviving process owns containment. The backend reuses this
            // in-process fence; no wrapper, child, helper or second admission.
            phase = "containment";
            // Match the existing backend's --version behavior: a short metadata
            // command does not open an MCP session or consume service admission.
            if (args.Length == 0) OfflineLifetime.Install();
            phase = "selection";
            using var selection = new Selection(AppContext.BaseDirectory);
            phase = "ownership";
            using var generation = args.Length == 0 ? new Generation(selection.Root, selection) : null;
            int result = 1;
            try {
                string entry = Selection.Safe(selection.Runtime, "SiemensMCPServer.Offline.dll");
                phase = "runtime";
                var assembly = new Runtime(entry).LoadFromAssemblyPath(entry);
                var main = assembly.EntryPoint ?? throw new InvalidDataException("Offline entry point is unavailable.");
                object returned = main.Invoke(null, new object[] { args }) ?? throw new InvalidDataException("Offline entry result is unavailable.");
                result = returned is Task<int> pending ? await pending : (int)returned;
                return result;
            } finally { generation?.Complete(result); }
        } catch (Exception ex) {
            string detail = phase == "containment" && ex is InvalidOperationException && ex.Message.StartsWith("Direct offline capacity is unavailable", StringComparison.Ordinal)
                ? "Offline capacity is unavailable (four sessions per user/logon session)." : "Verify the installed selection and retained ownership receipts.";
            Console.Error.WriteLine("Siemens offline launcher refused startup or execution (" + phase + ", " + ex.GetType().Name + "). " + detail);
            return 1;
        }
    }
}
