using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    internal sealed class PlcSourceInput
    {
        public string Name { get; set; } = "";
        public string Language { get; set; } = "";
        public string Code { get; set; } = "";
        public bool AllowBlockReplacement { get; set; }
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c))) throw new ArgumentException("Use a bounded native source name without path characters.");
            if (Language != "SCL" && Language != "STL") throw new ArgumentException("Source language must be SCL or STL.");
            if (string.IsNullOrWhiteSpace(Code) || Code.Length > 4 * 1024 * 1024 || Code.Contains('\0')) throw new ArgumentException("Supply bounded, nonempty PLC source code.");
            if (!AllowBlockReplacement) throw new ArgumentException("Native source generation may replace blocks declared in the source. Set allowBlockReplacement=true only for that reviewed scope.");
        }
        public string FileName => Language == "SCL" ? "payload.scl" : "payload.awl";
        public void RequireExactCandidate(PlcSourceInput candidate)
        {
            Validate(); candidate.Validate();
            if (Name != candidate.Name || Language != candidate.Language || Code != candidate.Code || AllowBlockReplacement != candidate.AllowBlockReplacement)
                throw new InvalidOperationException("Extension cannot change the exact PLC source or replacement scope supplied to this tool.");
        }
    }
    internal static class PlcSourceFiles
    {
        internal static void Write(string directory, PlcSourceInput source)
        {
            source.Validate(); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "source.json"), JsonSerializer.Serialize(new { source.Name, source.Language, source.AllowBlockReplacement }, JsonDefaults.Options), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, source.FileName), source.Code, new UTF8Encoding(true));
        }
        internal static PlcSourceInput Read(string directory)
        {
            var source = JsonSerializer.Deserialize<PlcSourceInput>(File.ReadAllText(Path.Combine(directory, "source.json")), JsonDefaults.Options) ?? throw new ArgumentException("Missing source metadata.");
            source.Code = File.ReadAllText(Path.Combine(directory, source.FileName), new UTF8Encoding(false, true));
            source.Validate();
            var files = TransferFiles.Inventory(directory);
            if (files.Length != 2 || files.Any(f => f.Path != "source.json" && f.Path != source.FileName)) throw new ArgumentException("A source plan must contain only its metadata and native source file.");
            return source;
        }
    }
}
