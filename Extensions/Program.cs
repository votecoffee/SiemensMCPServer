using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SiemensMCPServer.Extensions
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false, true); Console.OutputEncoding = new UTF8Encoding(false, true);
            if (args.Length != 1 || !args[0].StartsWith("--module=", StringComparison.Ordinal)) return 2;
            string module = args[0].Substring(9);
            try
            {
                FeatureOperations.SelfTest(module);
                string recipePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes.json");
                if (File.Exists(recipePath) && new FileInfo(recipePath).Length > 65536) throw new ArgumentException("Recipe catalog is too large.");
                var recipes = new FeatureRecipes(module, File.Exists(recipePath) ? File.ReadAllText(recipePath, Encoding.UTF8) : null);
                string? line;
                while ((line = ExtensionWire.ReadLine(Console.In)) != null)
                {
                    var request = JsonSerializer.Deserialize<ExtensionRequest>(line, ExtensionWire.Options)!;
                    var response = new ExtensionResponse { Id = request.Id };
                    try
                    {
                        if (request.Operation == "$hello") response.Result = ExtensionWire.Json(new ExtensionHello {
                            Protocol = ExtensionWire.Protocol, Module = module, Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3), Operations = FeatureOperations.Describe(module).Concat(recipes.Operations).ToArray()
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
