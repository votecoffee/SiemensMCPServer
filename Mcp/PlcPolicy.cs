using System;
using System.Linq;

namespace SiemensTiaOpenness.Mcp
{
    internal static class PlcPolicy
    {
        internal static readonly string[] RootNavigators = { "BlockGroup", "TypeGroup", "TagTableGroup", "ExternalSourceGroup" };
        internal static bool AllowsRootNavigator(string name) => RootNavigators.Contains(name, StringComparer.Ordinal);
        internal static void RequireOffline(string? state)
        {
            if (state != "Offline") throw new InvalidOperationException("PLC engineering export/mutation requires a verified Offline state. Current state: " + (state ?? "unavailable") + ". Set the PLC offline in TIA and read its status again.");
        }
        internal static bool AllowsResourceType(string type) => type.StartsWith("Siemens.Engineering.SW.Blocks.", StringComparison.Ordinal)
            || type.StartsWith("Siemens.Engineering.SW.Types.", StringComparison.Ordinal) || type.StartsWith("Siemens.Engineering.SW.Tags.", StringComparison.Ordinal)
            || type.StartsWith("Siemens.Engineering.SW.ExternalSources.", StringComparison.Ordinal);
    }
}
