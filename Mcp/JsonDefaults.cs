using System.Text.Json;

namespace SiemensTiaOpenness.Mcp
{
    internal static class JsonDefaults
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    }
}
