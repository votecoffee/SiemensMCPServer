using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SiemensMCPServer
{
    internal static class UnifiedScreenCatalog
    {
        internal sealed class Entry
        {
            public string Path { get; }
            public object Native { get; }
            public Entry(string path, object native) { Path = path; Native = native; }
        }

        public static IList<Entry> Read(object software)
        {
            var result = new List<Entry>();
            int visited = 0;
            AddScreens(software, "", result, ref visited);
            // V16/V17 have a flat screen composition. Later APIs expose groups.
            PropertyInfo? groups = software.GetType().GetProperty("ScreenGroups", BindingFlags.Public | BindingFlags.Instance);
            if (groups != null)
                foreach (object group in Each(groups.GetValue(software, null))) AddGroup(group, "groups", result, 0, ref visited);
            if (result.GroupBy(s => s.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Ambiguous Unified screen paths.");
            return result;
        }

        private static void AddScreens(object owner, string prefix, List<Entry> result, ref int visited)
        {
            foreach (object screen in Each(Get(owner, "Screens")))
            {
                Visit(ref visited);
                result.Add(new Entry(prefix + "screens/" + Uri.EscapeDataString((string)Get(screen, "Name")), screen));
            }
        }

        private static void AddGroup(object group, string prefix, List<Entry> result, int depth, ref int visited)
        {
            Visit(ref visited);
            if (depth >= 32) throw new InvalidOperationException("Unified screen group nesting exceeds 32 levels.");
            string path = prefix + "/" + Uri.EscapeDataString((string)Get(group, "Name")) + "/";
            AddScreens(group, path, result, ref visited);
            foreach (object child in Each(Get(group, "Groups"))) AddGroup(child, path + "groups", result, depth + 1, ref visited);
        }

        private static void Visit(ref int visited)
        {
            if (++visited > 20000) throw new InvalidOperationException("Unified screen catalog exceeds 20000 screens/groups.");
        }

        private static object Get(object owner, string name) => owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner, null)
            ?? throw new NotSupportedException("Installed Unified API does not expose " + owner.GetType().FullName + "." + name);
        private static IEnumerable<object> Each(object? composition) => (composition as IEnumerable)?.Cast<object>()
            ?? throw new NotSupportedException("Installed Unified API returned an unavailable screen composition.");
    }
}
