using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Analysis;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // WinExe workers have inherited pipes, not a console. Encoding setters
            // call console APIs and can fail with ERROR_INVALID_HANDLE. Bind the
            // actual standard streams directly and keep each JSON frame UTF-8.
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false, true)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false, true)) { AutoFlush = true });
            if (args.Length == 2 && args[0].StartsWith("--project-texts=", StringComparison.Ordinal) && args[1].StartsWith("--output=", StringComparison.Ordinal))
            {
                try { Console.WriteLine(JsonSerializer.Serialize(ProjectTranslationFiles.Run(args[0].Substring(16), args[1].Substring(9)), ExtensionWire.Options)); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 1; }
            }
            if (args.Length == 2 && args[0].StartsWith("--analyze-project=", StringComparison.Ordinal) && args[1].StartsWith("--output=", StringComparison.Ordinal))
            {
                try { Console.WriteLine(JsonSerializer.Serialize(HmiAnalysisFiles.Run(args[0].Substring(18), args[1].Substring(9)), ExtensionWire.Options)); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 1; }
            }
            if (args.Length != 1 || !args[0].StartsWith("--module=", StringComparison.Ordinal)) return 2;
            string module = args[0].Substring(9);
            try
            {
                FeatureOperations.SelfTest(module);
                string recipePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");
                if (File.Exists(recipePath) && new FileInfo(recipePath).Length > 65536) throw new ArgumentException("Recipe catalog is too large.");
                var recipes = new FeatureRecipes(module, File.Exists(recipePath) ? File.ReadAllText(recipePath, Encoding.UTF8) : null);
                string? line; bool firstFrame = true;
                while ((line = ExtensionWire.ReadLine(Console.In)) != null)
                {
                    // .NET Framework's Process.StandardInput can emit a UTF-8 BOM
                    // when started from a UTF-8 console. Normalize only the first
                    // transport frame; later frames retain strict JSON parsing.
                    if (firstFrame && line.StartsWith("\uFEFF", StringComparison.Ordinal)) line = line.Substring(1);
                    firstFrame = false;
                    var request = JsonSerializer.Deserialize<ExtensionRequest>(line, ExtensionWire.Options)!;
                    var response = new ExtensionResponse { Id = request.Id };
                    try
                    {
                        if (request.Operation == "$hello") response.Result = ExtensionWire.Json(new ExtensionHello {
                            Protocol = ExtensionWire.Protocol, Module = module, Version = AppRelease.Canonical(AppRelease.Identity(Assembly.GetExecutingAssembly())), Operations = FeatureOperations.Describe(module).Concat(recipes.Operations).ToArray()
                        });
                        else response.Result = recipes.TryExecute(request.Operation, request.Input, out var preset) ? preset : FeatureOperations.Execute(module, request.Operation, request.Input);
                    }
                    catch (Exception ex) { response.Error = ex.GetBaseException().Message; }
                    Console.WriteLine(ExtensionWire.Serialize(response));
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}
