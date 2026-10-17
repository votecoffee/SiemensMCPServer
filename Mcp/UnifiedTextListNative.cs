using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    internal static class UnifiedTextListNative
    {
        private const string ConfigName = "Textlists.hmi.yml";
        private const string LibraryName = "Textlists.TextLibrary.hmi.yml";

        public static bool Supports(Type? softwareType)
        {
            Type? composition = softwareType?.GetProperty("HmiTextLists")?.PropertyType;
            return composition != null && typeof(IEnumerable).IsAssignableFrom(composition) &&
                composition.GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) }) != null &&
                composition.GetMethod("Import", new[] { typeof(DirectoryInfo), typeof(string) }) != null;
        }
        private static object Composition(object software)
        {
            if (!Supports(software.GetType())) throw new NotSupportedException("This installed Unified API lacks the documented HmiTextLists paired-YAML export/import methods. V19 does not expose them. Use a matching newer API build; classic XML cannot be used for Unified.");
            return software.GetType().GetProperty("HmiTextLists")!.GetValue(software, null)!;
        }
        public static string[] Names(object software)
        {
            return ((IEnumerable)Composition(software)).Cast<object>().Select(n => (string)(n.GetType().GetProperty("Name")!.GetValue(n, null)!)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public static string Export(object software, string listName, string artifactRoot)
        {
            object composition = Composition(software);
            var names = Names(software);
            if (!names.Contains(listName, StringComparer.Ordinal)) throw new InvalidOperationException("Unified text list does not exist.");
            string directory = Path.Combine(artifactRoot, "unified-yaml-exports", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                object? result = composition.GetType().GetMethod("Export", new[] { typeof(DirectoryInfo), typeof(string) })!.Invoke(composition, new object[] { new DirectoryInfo(directory), ConfigName });
                if (result is IEnumerable files) foreach (object ignored in files) { }
                var actual = Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var expected = new[] { ConfigName, LibraryName }.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("Unified export did not produce exactly the required configuration and multilingual library files.");
                if (!Names(software).SequenceEqual(names, StringComparer.Ordinal)) throw new InvalidOperationException("Unified list identities changed during export.");
                var bundle = new UnifiedTextListBundle { ListName = listName, Configuration = File.ReadAllText(Path.Combine(directory, ConfigName), Encoding.UTF8),
                    TextLibrary = File.ReadAllText(Path.Combine(directory, LibraryName), Encoding.UTF8), NativeListNames = names };
                string snapshot = JsonSerializer.Serialize(bundle, JsonDefaults.Options);
                new UnifiedTextListDocument(snapshot); // Validate native schema and language alignment before exposing it.
                return snapshot;
            }
            finally { Cleanup(directory); }
        }

        public static void Import(object software, UnifiedTextListDocument candidate, string artifactRoot)
        {
            object composition = Composition(software);
            string directory = Path.Combine(artifactRoot, "unified-yaml-imports", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, ConfigName), candidate.Bundle.Configuration, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, LibraryName), candidate.Bundle.TextLibrary, new UTF8Encoding(false));
                object? result = composition.GetType().GetMethod("Import", new[] { typeof(DirectoryInfo), typeof(string) })!.Invoke(composition, new object[] { new DirectoryInfo(directory), ConfigName });
                if (result is bool success && !success) throw new InvalidOperationException("Native Unified text-list import reported failure.");
            }
            finally { Cleanup(directory); }
        }
        private static void Cleanup(string directory)
        {
            // Only our two known files are removed. Unexpected export files remain for diagnostics.
            foreach (string name in new[] { ConfigName, LibraryName })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
}
