using System;
using System.IO;
using System.Reflection;

namespace SiemensMCPServer
{
    // V21 separates the API into product assemblies. Resolve only known domains,
    // using the selected Base assembly's full identity rather than loaded-assembly order.
    internal static class SiemensApiTypes
    {
        public static Type? Resolve(Assembly api, string typeName, Func<AssemblyName, Assembly?> loadModule)
        {
            Type? legacy = api.GetType(typeName, false);
            if (legacy != null) return legacy;
            AssemblyName identity = api.GetName();
            if (identity.Name != "Siemens.Engineering.Base" || identity.Version == null || identity.Version.Major < 21)
                return null;

            string? module = typeName.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal) ? "WinCCUnified"
                : typeName.StartsWith("Siemens.Engineering.Hmi.", StringComparison.Ordinal) ? "WinCC"
                : typeName.StartsWith("Siemens.Engineering.SW.", StringComparison.Ordinal) ? "Step7" : null;
            if (module == null) return null;

            var requested = new AssemblyName(identity.FullName) { Name = "Siemens.Engineering." + module };
            Assembly? assembly = loadModule(requested);
            if (assembly == null) return null; // An optional product module is not installed.
            RequireIdentity(requested, assembly.GetName());
            return assembly.GetType(typeName, false);
        }

        public static void RequireIdentity(AssemblyName requested, AssemblyName actual)
        {
            if (!string.Equals(requested.FullName, actual.FullName, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException("The installed TIA Portal Openness assembly does not match the requested API identity.", requested.FullName);
        }
    }
}
