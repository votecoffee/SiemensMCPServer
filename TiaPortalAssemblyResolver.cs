using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace SiemensMCPServer
{
    internal static class TiaPortalAssemblyResolver
    {
        private const string OpennessRegistryPath = @"SOFTWARE\Siemens\Automation\Openness";
        private const string LegacyBaseAssemblyName = "Siemens.Engineering";
        private const string V21BaseAssemblyName = "Siemens.Engineering.Base";

        private static CompatibleInstallation _selectedInstallation;

        public static string SelectedEngineeringVersion
        {
            get { return _selectedInstallation == null ? null : _selectedInstallation.DisplayVersion; }
        }

        public static string ReferencedApiVersion
        {
            get
            {
                AssemblyName reference = GetReferencedBaseAssembly();
                return reference == null || reference.Version == null
                    ? null
                    : reference.Version.ToString();
            }
        }

        public static IList<string> GetCompatibleEngineeringVersions()
        {
            return FindCompatibleInstallations()
                .OrderByDescending(item => ParseVersion(item.RegistryVersion))
                .Select(item => item.DisplayVersion)
                .ToList();
        }

        public static void Initialize(string engineeringVersion)
        {
            if (string.IsNullOrWhiteSpace(engineeringVersion))
                throw new ArgumentException("A TIA Portal engineering version must be selected.", "engineeringVersion");

            CompatibleInstallation selected = FindCompatibleInstallations()
                .FirstOrDefault(item => string.Equals(
                    item.DisplayVersion,
                    NormalizeDisplayVersion(engineeringVersion),
                    StringComparison.OrdinalIgnoreCase));

            if (selected == null)
                throw new InvalidOperationException("The selected TIA Portal version does not provide the Openness API required by this build.");

            _selectedInstallation = selected;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSiemensEngineeringAssembly;
        }

        private static Assembly ResolveSiemensEngineeringAssembly(object sender, ResolveEventArgs args)
        {
            if (_selectedInstallation == null)
                return null;

            var requested = new AssemblyName(args.Name);
            if (requested.Name == null ||
                !requested.Name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string path = Path.Combine(_selectedInstallation.ApiDirectory, requested.Name + ".dll");
            if (!File.Exists(path))
                return null;

            SiemensApiTypes.RequireIdentity(requested, AssemblyName.GetAssemblyName(path));
            Assembly loaded = Assembly.LoadFrom(path);
            SiemensApiTypes.RequireIdentity(requested, loaded.GetName());
            return loaded;
        }

        internal static Assembly LoadOptionalModule(AssemblyName requested)
        {
            if (requested.Name != "Siemens.Engineering.WinCCUnified" && requested.Name != "Siemens.Engineering.WinCC"
                && requested.Name != "Siemens.Engineering.Step7")
                throw new ArgumentException("Only documented Siemens product modules can be loaded.", "requested");
            if (_selectedInstallation == null)
                throw new InvalidOperationException("Select a compatible TIA Portal installation before loading its product modules.");
            return ResolveSiemensEngineeringAssembly(null, new ResolveEventArgs(requested.FullName));
        }

        private static IList<CompatibleInstallation> FindCompatibleInstallations()
        {
            AssemblyName referencedBase = GetReferencedBaseAssembly();
            if (referencedBase == null || referencedBase.Version == null)
                return new List<CompatibleInstallation>();

            string apiVersion = referencedBase.Version.ToString();
            string baseAssemblyName = referencedBase.Name;
            var installations = new Dictionary<string, CompatibleInstallation>(StringComparer.OrdinalIgnoreCase);

            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey openness = baseKey.OpenSubKey(OpennessRegistryPath))
                    {
                        if (openness == null)
                            continue;

                        foreach (string registryVersion in openness.GetSubKeyNames())
                        {
                            using (RegistryKey engineeringKey = openness.OpenSubKey(registryVersion))
                            {
                                if (engineeringKey == null)
                                    continue;

                                string displayVersion = engineeringKey.GetValue("PortalVersion") as string;
                                if (string.IsNullOrWhiteSpace(displayVersion))
                                    displayVersion = ToDisplayVersion(registryVersion);

                                string assemblyPath = FindBaseAssemblyPath(
                                    engineeringKey,
                                    apiVersion,
                                    baseAssemblyName);

                                if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                                    continue;

                                try
                                {
                                    SiemensApiTypes.RequireIdentity(referencedBase, AssemblyName.GetAssemblyName(assemblyPath));
                                }
                                catch (IOException) { continue; }
                                catch (BadImageFormatException) { continue; }

                                string directory = Path.GetDirectoryName(assemblyPath);
                                if (string.IsNullOrWhiteSpace(directory))
                                    continue;

                                var item = new CompatibleInstallation(
                                    registryVersion,
                                    NormalizeDisplayVersion(displayVersion),
                                    directory);

                                installations[item.DisplayVersion] = item;
                            }
                        }
                    }
                }
                catch
                {
                    // A missing or inaccessible registry view should not block discovery from the other view.
                }
            }

            return installations.Values.ToList();
        }

        private static string FindBaseAssemblyPath(
            RegistryKey engineeringKey,
            string apiVersion,
            string baseAssemblyName)
        {
            using (RegistryKey publicApi = engineeringKey.OpenSubKey("PublicAPI"))
            using (RegistryKey apiKey = publicApi == null ? null : publicApi.OpenSubKey(apiVersion))
            {
                if (apiKey == null)
                    return null;

                string direct = apiKey.GetValue(baseAssemblyName) as string;
                if (baseAssemblyName != V21BaseAssemblyName && !string.IsNullOrWhiteSpace(direct) && File.Exists(direct))
                    return direct;

                foreach (string framework in new[] { "net48" })
                {
                    using (RegistryKey frameworkKey = apiKey.OpenSubKey(framework))
                    {
                        if (frameworkKey == null)
                            continue;

                        string path = frameworkKey.GetValue(baseAssemblyName) as string;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                            return path;
                    }
                }
            }

            return null;
        }

        private static AssemblyName GetReferencedBaseAssembly()
        {
            AssemblyName[] references = Assembly.GetExecutingAssembly().GetReferencedAssemblies();

            return references.FirstOrDefault(a =>
                       string.Equals(a.Name, LegacyBaseAssemblyName, StringComparison.OrdinalIgnoreCase))
                   ?? references.FirstOrDefault(a =>
                       string.Equals(a.Name, V21BaseAssemblyName, StringComparison.OrdinalIgnoreCase));
        }

        private static string ToDisplayVersion(string registryVersion)
        {
            string value = registryVersion == null ? string.Empty : registryVersion.Trim();
            Version parsed;
            if (Version.TryParse(value, out parsed))
            {
                if (parsed.Build <= 0 && parsed.Revision <= 0 && parsed.Minor == 0)
                    return "V" + parsed.Major;

                if (parsed.Build <= 0 && parsed.Revision <= 0)
                    return "V" + parsed.Major + "." + parsed.Minor;
            }

            return NormalizeDisplayVersion(value);
        }

        private static string NormalizeDisplayVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string normalized = value.Trim();
            return normalized.StartsWith("V", StringComparison.OrdinalIgnoreCase)
                ? "V" + normalized.Substring(1)
                : "V" + normalized;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new Version(0, 0);

            string normalized = value.Trim();
            if (normalized.StartsWith("V", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);

            Version parsed;
            return Version.TryParse(normalized, out parsed) ? parsed : new Version(0, 0);
        }

        private sealed class CompatibleInstallation
        {
            public CompatibleInstallation(string registryVersion, string displayVersion, string apiDirectory)
            {
                RegistryVersion = registryVersion;
                DisplayVersion = displayVersion;
                ApiDirectory = apiDirectory;
            }

            public string RegistryVersion { get; private set; }
            public string DisplayVersion { get; private set; }
            public string ApiDirectory { get; private set; }
        }
    }
}
