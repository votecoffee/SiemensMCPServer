using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiemensMCPServer.Mcp
{
    public sealed class EngineeringEdit
    {
        [JsonRequired] public string Attribute { get; set; } = "";
        [JsonRequired] public string ExpectedValue { get; set; } = "";
        [JsonRequired] public string NewValue { get; set; } = "";
    }
    public sealed class CreationArgument
    {
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string Value { get; set; } = "";
    }
    internal sealed class EngineeringValue
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Editable { get; set; }
        public bool IsNull { get; set; }
        public string[] Choices { get; set; } = Array.Empty<string>();
    }
    internal sealed class EngineeringChild
    {
        public string Path { get; set; } = "";
        public string Label { get; set; } = "";
        public string NativeType { get; set; } = "";
    }
    internal sealed class EngineeringParameter
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Mandatory { get; set; }
        public string[] Choices { get; set; } = Array.Empty<string>();
    }
    internal sealed class EngineeringCreation
    {
        public string NativeType { get; set; } = "";
        public EngineeringParameter[] Parameters { get; set; } = Array.Empty<EngineeringParameter>();
    }
    internal sealed class EngineeringSnapshot
    {
        public string TargetId { get; set; } = "";
        public string Path { get; set; } = "";
        public string NativeType { get; set; } = "";
        public EngineeringValue[] Attributes { get; set; } = Array.Empty<EngineeringValue>();
        public EngineeringChild[] Children { get; set; } = Array.Empty<EngineeringChild>();
        public EngineeringCreation[] Creations { get; set; } = Array.Empty<EngineeringCreation>();
        public string[] Errors { get; set; } = Array.Empty<string>();
        public string[] UnsupportedAttributes { get; set; } = Array.Empty<string>();
        public bool CanDelete { get; set; }
        public bool CanExport { get; set; }
        public bool CanImport { get; set; }
        public bool InheritedDynamization { get; set; }
        public void ProtectInheritedDynamization()
        {
            InheritedDynamization = true;
            foreach (var attribute in Attributes) attribute.Editable = false;
            CanDelete = false;
            CanImport = false;
            Creations = Array.Empty<EngineeringCreation>();
        }
        [JsonIgnore] public string Fingerprint => TextListDocument.Hash(JsonSerializer.Serialize(this, JsonDefaults.Options));
        [JsonIgnore] public string ReadbackFingerprint
        {
            get
            {
                if (!NativeType.StartsWith("Siemens.Engineering.SW.", StringComparison.Ordinal)) return Fingerprint;
                // Siemens updates these computed fields when an authored PLC property changes.
                // They still participate in the pre-apply stale check, but not candidate content equality.
                string[] computed = { "ModifiedDate", "CodeModifiedDate", "InterfaceModifiedDate", "StructureModified", "ParameterModified", "CompileDate", "IsConsistent" };
                var copy = JsonSerializer.Deserialize<EngineeringSnapshot>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
                copy.Attributes = copy.Attributes.Where(a => a.Editable || !computed.Contains(a.Name, StringComparer.Ordinal)).ToArray();
                return copy.Fingerprint;
            }
        }
        public EngineeringSnapshot Edit(EngineeringEdit[] edits)
        {
            if (Errors.Length != 0) throw new InvalidOperationException("Object inspection is incomplete; changes are disabled. " + string.Join("; ", Errors));
            if (edits == null || edits.Length < 1 || edits.Length > 1000) throw new ArgumentException("Supply 1 to 1000 attribute edits.");
            var result = JsonSerializer.Deserialize<EngineeringSnapshot>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edit in edits)
            {
                if (edit == null || edit.Attribute == null || edit.ExpectedValue == null || edit.NewValue == null || !seen.Add(edit.Attribute)) throw new ArgumentException("Missing or duplicate edit fields.");
                var value = result.Attributes.SingleOrDefault(a => a.Name == edit.Attribute);
                if (value == null || !value.Editable) throw new InvalidOperationException("Attribute is not editable: " + edit.Attribute);
                if (value.Value != edit.ExpectedValue) throw new InvalidOperationException("Expected value differs: " + edit.Attribute);
                value.Value = EngineeringCodec.Format(EngineeringCodec.Parse(edit.NewValue, EngineeringCodec.Resolve(value.Type)));
                value.IsNull = false;
            }
            if (result.Fingerprint == Fingerprint) throw new ArgumentException("No changes in preview.");
            return result;
        }
    }
    internal static class EngineeringCodec
    {
        public static Type Resolve(string name) => Type.GetType(name, false) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null)
            ?? throw new NotSupportedException("Type is unavailable: " + name);
        public static bool Supports(Type type) => type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(Color) || type == typeof(TimeSpan)
            || type == typeof(DateTime) || new[] { typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) }.Contains(type);
        public static string Format(object value)
        {
            if (value is Color color) return "#" + unchecked((uint)color.ToArgb()).ToString("X8", CultureInfo.InvariantCulture);
            if (value is bool flag) return flag ? "true" : "false";
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (value is DateTime time) return time.ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeSpan span) return span.ToString("c", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        public static object Parse(string text, Type type)
        {
            if (!Supports(type)) throw new NotSupportedException("Unsupported scalar type: " + type.FullName);
            if (type == typeof(string)) { if (text.Length > 4 * 1024 * 1024) throw new ArgumentException("Text is too large."); return text; }
            if (type.IsEnum)
            {
                // Symbolic names only. No numeric casts or unknown flag bits.
                var names = Enum.GetNames(type);
                if (text.Contains(',') && !type.IsDefined(typeof(FlagsAttribute), false)) throw new ArgumentException("This enum accepts a single named value.");
                if (text.Split(',').Any(n => !names.Contains(n.Trim(), StringComparer.Ordinal))) throw new ArgumentException("Use advertised enum names.");
                return Enum.Parse(type, text, false);
            }
            if (type == typeof(Color))
            {
                if (text.Length != 9 || text[0] != '#') throw new ArgumentException("Use #AARRGGBB colors.");
                return Color.FromArgb(unchecked((int)uint.Parse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
            if (type == typeof(bool)) { if (text != "true" && text != "false") throw new ArgumentException("Use true or false."); return text == "true"; }
            if (type == typeof(TimeSpan)) return TimeSpan.ParseExact(text, "c", CultureInfo.InvariantCulture);
            if (type == typeof(DateTime)) return DateTime.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            object value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)) || value is float f && (float.IsNaN(f) || float.IsInfinity(f))) throw new ArgumentException("Finite numbers are required.");
            return value;
        }
    }
}
