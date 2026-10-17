using System;
using System.IO;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    internal static class ExtensionWire
    {
        public const int Protocol = 1;
        public const int MaxFrame = 32 * 1024 * 1024;
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Options);
        public static T Parse<T>(JsonElement value) => JsonSerializer.Deserialize<T>(value.GetRawText(), Options) ?? throw new ArgumentException("Missing extension value.");
        public static string? ReadLine(TextReader reader)
        {
            var line = new StringBuilder();
            int c;
            while ((c = reader.Read()) != -1 && c != '\n')
            {
                if (line.Length >= MaxFrame) throw new InvalidDataException("Extension frame exceeds 32 MiB.");
                line.Append((char)c);
            }
            return c == -1 && line.Length == 0 ? null : line.ToString();
        }
        public static string Serialize(object value)
        {
            string json = JsonSerializer.Serialize(value, Options);
            if (json.Length > MaxFrame) throw new ArgumentException("Extension frame exceeds 32 MiB.");
            return json;
        }
    }
    internal sealed class ExtensionOperation
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Route { get; set; } = "local";
        public bool Preview { get; set; }
        public JsonElement InputSchema { get; set; }
    }
    internal sealed class ExtensionHello
    {
        public int Protocol { get; set; }
        public string Module { get; set; } = "";
        public string Version { get; set; } = "";
        public ExtensionOperation[] Operations { get; set; } = Array.Empty<ExtensionOperation>();
    }
    internal sealed class ExtensionRequest
    {
        public long Id { get; set; }
        public string Operation { get; set; } = "";
        public JsonElement Input { get; set; }
    }
    internal sealed class ExtensionResponse
    {
        public long Id { get; set; }
        public JsonElement Result { get; set; } = ExtensionWire.Json(new { });
        public string? Error { get; set; }
    }
    internal interface IFeatureEngine
    {
        JsonElement Execute(string module, string operation, object input);
    }
    // Used by pure unit fixtures only. The production host always injects worker processes.
    internal sealed class LocalFeatureEngine : IFeatureEngine
    {
        public JsonElement Execute(string module, string operation, object input) => FeatureOperations.Execute(module, operation, ExtensionWire.Json(input));
    }
    internal sealed class ClassicInspection
    {
        public string Name { get; set; } = "";
        public string NativeType { get; set; } = "";
        public JsonElement ScalarCatalog { get; set; }
        public JsonElement BehaviorCatalog { get; set; }
    }
}
