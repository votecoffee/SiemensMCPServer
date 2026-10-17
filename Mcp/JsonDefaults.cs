using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    internal static class JsonDefaults
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    }
}
