using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace SiemensMCPServer.Mcp
{
    // Reads only documented text/enum fields. UnderlyingObject and ReferencedAs
    // engineering handles are deliberately never inspected or serialized.
    internal sealed class PlcReferenceReader
    {
        private readonly PlcReferenceSnapshot snapshot = new PlcReferenceSnapshot();
        private readonly int maxRows;
        private int nodes, characters;
        private bool stopped;
        private readonly List<PlcReferenceRow> rows = new List<PlcReferenceRow>();
        private static readonly string[] ObjectFields = { "Name", "Address", "TypeName", "Device", "Path" };
        private static readonly string[] LocationFields = { "Name", "ReferenceLocation", "ReferenceType", "Access", "ReferencedAsName", "Address", "TypeName" };
        private PlcReferenceReader(int maxRows) { this.maxRows = maxRows; }
        public static PlcReferenceSnapshot Read(object result, int maxRows)
        {
            if (maxRows < 1 || maxRows > 1000) throw new ArgumentException("Use 1 to 1000 cross-reference rows.");
            var reader = new PlcReferenceReader(maxRows);
            reader.Sources(Each(result, "Sources"), Array.Empty<string>(), 0);
            reader.snapshot.Rows = reader.rows.ToArray(); return reader.snapshot;
        }
        private static object? Get(object value, string property)
        {
            var member = value.GetType().GetProperty(property);
            if (member == null || !member.CanRead || member.GetIndexParameters().Length != 0)
                throw new NotSupportedException("Native cross-reference field is unavailable: " + property);
            return member.GetValue(value, null);
        }
        private static IEnumerable Each(object value, string property) => Get(value, property) as IEnumerable
            ?? throw new NotSupportedException("Native cross-reference composition is unavailable: " + property);
        private Dictionary<string, string> Fields(object value, string[] names)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                object? native = Get(value, name);
                if (native != null && !(native is string) && !native.GetType().IsEnum)
                    throw new NotSupportedException("Cross-reference field is not text or an enum: " + name);
                string text = native?.ToString() ?? "";
                string bounded = PlcInspectionFiles.Prefix(text, 4096);
                if (text != bounded) snapshot.FieldsTruncated = true;
                fields[name] = bounded;
            }
            return fields;
        }
        private bool Node()
        {
            if (stopped) return false;
            if (++nodes > 10000) { Stop(); return false; } return true;
        }
        private void Stop() { stopped = true; snapshot.Truncated = true; }
        private void Add(Dictionary<string, string> source, Dictionary<string, string>? reference, Dictionary<string, string>? location, string[] ancestry)
        {
            int size = ancestry.Sum(s => s.Length) + source.Values.Sum(s => s.Length)
                + (reference?.Values.Sum(s => s.Length) ?? 0) + (location?.Values.Sum(s => s.Length) ?? 0);
            if (rows.Count >= maxRows || characters + size > 262144) { Stop(); return; }
            characters += size; rows.Add(new PlcReferenceRow { SourceAncestry = ancestry, Source = source, Reference = reference, Location = location });
        }
        private void Sources(IEnumerable sources, string[] ancestry, int depth)
        {
            foreach (object source in sources)
            {
                if (!Node()) return;
                if (depth >= 32) { Stop(); return; }
                var fields = Fields(source, ObjectFields); bool anyReference = false;
                foreach (object reference in Each(source, "References"))
                {
                    if (!Node()) return;
                    anyReference = true; var referenceFields = Fields(reference, ObjectFields); bool anyLocation = false;
                    foreach (object location in Each(reference, "Locations"))
                    {
                        if (!Node()) return;
                        anyLocation = true; Add(fields, referenceFields, Fields(location, LocationFields), ancestry);
                        if (stopped) return;
                    }
                    if (!anyLocation) Add(fields, referenceFields, null, ancestry);
                    if (stopped) return;
                }
                if (!anyReference) Add(fields, null, null, ancestry);
                if (stopped) return;
                Sources(Each(source, "Children"), ancestry.Concat(new[] { fields["Name"] }).ToArray(), depth + 1);
                if (stopped) return;
            }
        }
    }
}
