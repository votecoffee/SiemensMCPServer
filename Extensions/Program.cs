using System;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SiemensTiaOpenness.Extensions
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length != 1 || !args[0].StartsWith("--module=", StringComparison.Ordinal)) return 2;
            string module = args[0].Substring(9);
            try
            {
                FeatureOperations.SelfTest(module);
                string? line;
                while ((line = ExtensionWire.ReadLine(Console.In)) != null)
                {
                    var request = JsonSerializer.Deserialize<ExtensionRequest>(line, ExtensionWire.Options)!;
                    var response = new ExtensionResponse { Id = request.Id };
                    try
                    {
                        response.Result = request.Operation == "$hello" ? ExtensionWire.Json(new ExtensionHello {
                            Protocol = ExtensionWire.Protocol, Module = module, Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3), Operations = FeatureOperations.Describe(module)
                        }) : FeatureOperations.Execute(module, request.Operation, request.Input);
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
