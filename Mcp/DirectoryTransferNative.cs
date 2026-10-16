using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using YamlDotNet.Core;

namespace SiemensTiaOpenness.Mcp
{
    // Directory overloads are used by Unified. Keep filenames and companions native.
    internal static class DirectoryTransferNative
    {
        internal static void Export(object node, MethodInfo method, string directory)
        {
            object? result = method.Invoke(node, method.GetParameters().Length == 1
                ? new object[] { new DirectoryInfo(directory) }
                : new object[] { new DirectoryInfo(directory), "payload.hmi.yml" });
            Complete(result);
        }
        internal static object? Import(object node, MethodInfo method, string directory, bool overwrite)
        {
            if (!overwrite) throw new ArgumentException("Unified directory imports require overwrite=true to acknowledge native replacement scope.");
            bool named = method.GetParameters().Length == 2;
            var configurations = Validate(node.GetType().FullName!, directory, named);
            object? result = method.Invoke(node, named ? new object[] { new DirectoryInfo(directory), configurations.Single() } : new object[] { new DirectoryInfo(directory) });
            Complete(result); return result;
        }
        internal static string[] Validate(string nativeType, string directory, bool requiresFilename)
        {
            var inventory = TransferFiles.Inventory(directory);
            var yaml = inventory.Where(f => f.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).ToArray();
            var configurations = yaml.Where(f => !f.Path.Contains('/') && !f.Path.EndsWith(".TextLibrary.hmi.yml", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToArray();
            if (configurations.Length == 0) throw new ArgumentException("Unified import requires a top-level native YML configuration and its companions.");
            if (requiresFilename && configurations.Length != 1) throw new ArgumentException("This native API requires a filename. Select exactly one configuration and its companions.");
            foreach (var file in yaml)
            {
                using (var reader = File.OpenText(Path.Combine(directory, file.Path)))
                {
                    var parser = new Parser(reader); int events = 0;
                    while (parser.MoveNext()) if (++events > 1000000) throw new ArgumentException("YML event count exceeds the transfer limit.");
                }
            }
            foreach (string configuration in configurations)
            {
                bool hmiSuffix = configuration.EndsWith(".hmi.yml", StringComparison.OrdinalIgnoreCase);
                string stem = configuration.Substring(0, configuration.Length - (hmiSuffix ? ".hmi.yml".Length : ".yml".Length));
                if (nativeType.StartsWith("Siemens.Engineering.HmiUnified.Scripts.", StringComparison.Ordinal))
                    RequireFile(inventory, stem + (hmiSuffix ? ".hmi.js" : ".js"), "script JavaScript");
                if (nativeType.StartsWith("Siemens.Engineering.HmiUnified.TextGraphicList.", StringComparison.Ordinal)
                    && nativeType.IndexOf("TextList", StringComparison.OrdinalIgnoreCase) >= 0)
                    RequireFile(inventory, stem + ".TextLibrary.hmi.yml", "text-list multilingual library");
            }
            return configurations;
        }
        private static void RequireFile(TransferFile[] files, string name, string description)
        {
            if (!files.Any(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Missing native " + description + " companion: " + name);
        }
        internal static void Complete(object? result)
        {
            if (result is bool accepted && !accepted) throw new InvalidOperationException("Native transfer returned false; no transaction should be committed.");
            // Newer Unified APIs may return an ImportResult instead of bool.
            var state = result?.GetType().GetProperty("State");
            if (state != null)
            {
                string value = Convert.ToString(state.GetValue(result, null), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                if (value != "Success" && value != "Warning") throw new InvalidOperationException("Native transfer result was not successful: " + value);
            }
            // Some native exports return a deferred file sequence. Finish it before inventory/readback.
            if (result is IEnumerable sequence && !(result is string)) foreach (object? ignored in sequence) { }
        }
    }
}
