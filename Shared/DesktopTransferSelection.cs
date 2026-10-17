using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer
{
    // Pure preflight rules shared with offline tests. Native methods are invoked only after review.
    internal static class DesktopTransferSelection
    {
        internal static void ValidatePath(string family, string path)
        {
            if (family != "plc" && family != "unified") throw new ArgumentException("Select a Unified HMI or PLC target.");
            if (path == "") return;
            var parts = path.Split('/');
            if (parts.Length > 128 || parts.Any(p => p.Length < 3 || !(p.StartsWith("p:", StringComparison.Ordinal)
                || p.StartsWith("n:", StringComparison.Ordinal) || p.StartsWith("i:", StringComparison.Ordinal) || p.StartsWith("l:", StringComparison.Ordinal))))
                throw new ArgumentException("Invalid engineering resource path.");
            if (family == "plc" && (!parts[0].StartsWith("p:", StringComparison.Ordinal) || !PlcPolicy.AllowsRootNavigator(Uri.UnescapeDataString(parts[0].Substring(2)))))
                throw new ArgumentException("Select offline PLC blocks, data types, tag tables or external sources.");
        }

        internal static TransferResource[] ReadBundle(string directory, string family, string selectedPath, bool overwrite)
        {
            ValidatePath(family, selectedPath);
            var resources = TransferBundle.Read(directory);
            using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "export-index.json"))))
            {
                if (!document.RootElement.TryGetProperty("family", out var value) || value.GetString() != family)
                    throw new ArgumentException("The export bundle belongs to a different target family.");
            }
            foreach (var resource in resources)
            {
                ValidatePath(family, resource.Path);
                ValidatePath(family, resource.ImportPath);
                if (selectedPath.Length != 0 && resource.ImportPath != selectedPath && !resource.ImportPath.StartsWith(selectedPath + "/", StringComparison.Ordinal))
                    throw new ArgumentException("Bundle import destination is outside the selected resource. Select the target root for a whole-target bundle.");
                bool allowed = family == "plc" ? PlcPolicy.AllowsResourceType(resource.NativeType)
                    : resource.NativeType.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal);
                if (!allowed) throw new ArgumentException("Bundle contains a resource from another target family.");
            }
            if (family == "unified" && !overwrite) throw new ArgumentException("Unified imports require acknowledgment of native replacement scope. Check Overwrite existing objects.");
            return resources;
        }
    }
}
