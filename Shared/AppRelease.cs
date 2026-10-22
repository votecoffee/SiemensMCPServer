using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace SiemensMCPServer
{
    internal static partial class AppRelease
    {
        public static string Canonical(string value)
        {
            if (value == null || value.Length > 64 || !Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+\\z")) throw new ArgumentException("Use a three-part numeric app version.");
            return string.Join(".", value.Split('.').Select(part => int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
        }
        public static bool Equivalent(string left, string right) => Canonical(left) == Canonical(right);
        public static string Identity(Assembly assembly)
        {
            var info = (AssemblyInformationalVersionAttribute?)assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).FirstOrDefault();
            return info?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
    }
}
