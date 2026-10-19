using System;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    internal static class LibraryInspectionOperations
    {
        public static object Schema(bool browse)
        {
            return new { type = "object", additionalProperties = false, required = new[] { "expectedProjectPath", "capture", "options" }, properties = new {
                expectedProjectPath = new { type = "string", description = "Exact absolute project path from tia_get_status." },
                capture = new { type = "object", additionalProperties = false, required = browse ? new[] { "libraryId", "path" } : Array.Empty<string>(), properties = new {
                    libraryId = new { type = "string", description = "Empty to list; project or an exact global ID returned by list_libraries to browse." },
                    path = new { type = "string", description = "Empty for library root, otherwise an exact path returned by browse_library." },
                    offset = new { type = "integer", minimum = 0, maximum = 10000 }, maxRows = new { type = "integer", minimum = 1, maximum = 200 } } },
                options = new { type = "object", additionalProperties = false } } };
        }
        public static JsonElement Execute(string operation, JsonElement input)
        {
            var request = ExtensionWire.Parse<LibraryInspectionInput>(input.GetProperty("arguments")); request.Validate();
            if (operation != "list_libraries" && operation != "browse_library") throw new ArgumentException("Unknown library inspection operation.");
            if (operation == "list_libraries" ? request.Capture.LibraryId != "" : request.Capture.LibraryId == "")
                throw new ArgumentException("list_libraries requires an empty library ID; browse_library requires a returned library ID.");
            if (request.Options.EnumerateObject().Any()) throw new ArgumentException("Library inspection options must be empty.");
            string? phase = input.GetProperty("phase").GetString();
            if (phase == "validate") return ExtensionWire.Json(new { validated = true });
            if (phase != "snapshot") throw new ArgumentException("Unknown library inspection phase.");
            var snapshot = ExtensionWire.Parse<LibrarySnapshot>(input.GetProperty("snapshot"));
            if (snapshot.Rows.Length > request.Capture.MaxRows) throw new ArgumentException("Library snapshot exceeds the requested row limit.");
            return ExtensionWire.Json(snapshot);
        }
    }
}
