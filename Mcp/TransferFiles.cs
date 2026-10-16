using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SiemensTiaOpenness.Mcp
{
    internal sealed class TransferFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Bytes { get; set; }
    }
    internal static class TransferFiles
    {
        internal static TransferFile[] Inventory(string directory)
        {
            directory = System.IO.Path.GetFullPath(directory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            var files = new List<TransferFile>(); long total = 0;
            Visit(directory, directory, files, ref total, 0);
            if (files.Count == 0) throw new ArgumentException("No files to transfer.");
            return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        }
        private static void Visit(string root, string directory, List<TransferFile> result, ref long total, int depth)
        {
            if (depth > 32 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked directories or excessive nesting are not supported.");
            foreach (string file in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked files are not supported.");
                long size = new FileInfo(file).Length; total += size;
                if (size > 64 * 1024 * 1024 || total > 512 * 1024 * 1024 || result.Count >= 10000) throw new ArgumentException("Transfer exceeds file/count/size limits.");
                if (System.IO.Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)) Xml(File.ReadAllText(file));
                result.Add(new TransferFile { Path = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'), Bytes = size, Sha256 = TextListDocument.Hash(File.ReadAllBytes(file)) });
            }
            foreach (string child in Directory.GetDirectories(directory).OrderBy(f => f, StringComparer.Ordinal)) Visit(root, child, result, ref total, depth + 1);
        }
        internal static void Copy(string source, string destination)
        {
            var files = Inventory(source);
            Directory.CreateDirectory(destination);
            foreach (var file in files)
            {
                string target = System.IO.Path.Combine(destination, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(System.IO.Path.Combine(source, file.Path), target, false);
            }
            if (Hash(files) != Hash(Inventory(destination))) throw new InvalidOperationException("Source files changed while staging the preview.");
        }
        internal static string Hash(TransferFile[] files) => TextListDocument.Hash(JsonSerializer.Serialize(files, JsonDefaults.Options));
        internal static XDocument Xml(string text)
        {
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 })) return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        internal static string XmlObjectName(string text)
        {
            var doc = Xml(text);
            return doc.Root?.Elements().Select(e => e.Element("AttributeList")?.Element("Name")?.Value).SingleOrDefault(n => n != null)
                ?? throw new ArgumentException("Expected one native resource with an AttributeList/Name.");
        }
        // Exclude volatile export headers/IDs; retain authored values, open links, sidecars, and file names.
        internal static string SemanticHash(string directory)
        {
            return TextListDocument.Hash(string.Join("\n", Inventory(directory).Select(f => {
                string file = System.IO.Path.Combine(directory, f.Path);
                if (!f.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return f.Path + ":" + f.Sha256;
                var doc = Xml(File.ReadAllText(file));
                if (doc.Root?.Name.LocalName == "Document")
                    foreach (var header in doc.Root.Elements().Where(e => e.Name.LocalName == "DocumentInfo").ToArray()) header.Remove();
                foreach (var element in doc.Descendants())
                {
                    element.Attributes().Where(a => a.Name.LocalName == "ID").Remove();
                    // Formatting whitespace only, never strip whitespace from authored scalar elements.
                    if (element.HasElements) element.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
                }
                return f.Path + ":" + doc.ToString(SaveOptions.DisableFormatting);
            })));
        }
    }
}
