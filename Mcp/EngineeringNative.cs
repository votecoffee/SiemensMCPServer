using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace SiemensMCPServer.Mcp
{
    // Only navigates descendants of the selected HMI/PLC software. Parent/services are excluded.
    internal static class EngineeringNative
    {
        internal static object Resolve(object root, string path)
        {
            object node = root;
            if (path == "") return node;
            var parts = path.Split('/');
            if (parts.Length > 128 || parts.Any(p => p.Length == 0)) throw new ArgumentException("Invalid engineering path.");
            foreach (string part in parts)
            {
                var child = Children(node, "").SingleOrDefault(c => c.Key == part);
                if (child.Value == null) throw new InvalidOperationException("Object path no longer exists. Browse the engineering target again: " + path);
                node = child.Value;
            }
            return node;
        }
        private static string Key(object child, int index)
        {
            if (child is MultilingualTextItem text) return "l:" + Uri.EscapeDataString(text.Language.Culture.Name);
            var prop = child.GetType().GetProperty("Name");
            if (prop?.PropertyType == typeof(string)) return "n:" + Uri.EscapeDataString((string)prop.GetValue(child, null)!);
            // Classic slide-ins have a stable side/type identity, but no Name property.
            if (child.GetType().FullName == "Siemens.Engineering.Hmi.Screen.ScreenSlidein")
                return "s:" + Uri.EscapeDataString(Convert.ToString(child.GetType().GetProperty("SlideinType")!.GetValue(child, null), System.Globalization.CultureInfo.InvariantCulture)!);
            return "i:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static List<KeyValuePair<string, object>> Children(object node, string unused)
        {
            var result = new List<KeyValuePair<string, object>>();
            if (node is IEngineeringComposition composition)
            {
                if (composition.Count > 20000) throw new InvalidOperationException("Composition exceeds the 20000-object inspection limit.");
                for (int i = 0; i < composition.Count; i++) result.Add(new KeyValuePair<string, object>(Key(composition[i], i), composition[i]));
            }
            else if (node is IEngineeringObject engineering)
            {
                foreach (var info in engineering.GetCompositionInfos().OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    if (info.Name == "Parent" || node is PlcSoftware && !PlcPolicy.AllowsRootNavigator(info.Name)) continue;
                    object? value = engineering.GetComposition(info.Name);
                    if (value != null) result.Add(new KeyValuePair<string, object>("p:" + Uri.EscapeDataString(info.Name), value));
                }
                // Multilingual texts and embedded parts can be public navigators without composition metadata.
                foreach (var prop in node.GetType().GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length != 0 || prop.Name == "Parent" || prop.Name == "Language") continue;
                    if (node is PlcSoftware && !PlcPolicy.AllowsRootNavigator(prop.Name)) continue;
                    if (!typeof(IEngineeringCompositionOrObject).IsAssignableFrom(prop.PropertyType)) continue;
                    string key = "p:" + Uri.EscapeDataString(prop.Name);
                    if (result.Any(c => c.Key == key)) continue;
                    object? value = prop.GetValue(node, null);
                    if (value != null) result.Add(new KeyValuePair<string, object>(key, value));
                }
            }
            if (result.GroupBy(c => c.Key, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new InvalidOperationException("Ambiguous child names; inspection cannot select an exact object.");
            return result;
        }
        private static bool Protected(object node) => node.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal);
        private static bool Dynamic(object node, string name)
        {
            var prop = node.GetType().GetProperty("Dynamizations");
            if (prop == null) return false;
            foreach (object binding in (IEnumerable)prop.GetValue(node, null)!)
            {
                string bound = (string)binding.GetType().GetProperty("PropertyName")!.GetValue(binding, null)!;
                if (bound == name || bound.StartsWith(name + ".", StringComparison.Ordinal) || name.StartsWith(bound + ".", StringComparison.Ordinal)) return true;
            }
            return false;
        }
        internal static bool HasDynamicAncestor(object root, string path)
        {
            object node = root;
            if (path.Length == 0) return false;
            foreach (string part in path.Split('/'))
            {
                // Bindings on a composite property also protect its text items/parts.
                // Explicit traversal into Dynamizations/EventHandlers remains available.
                if (part.StartsWith("p:", StringComparison.Ordinal) && Dynamic(node, Uri.UnescapeDataString(part.Substring(2)))) return true;
                var child = Children(node, "").SingleOrDefault(c => c.Key == part);
                if (child.Value == null) throw new InvalidOperationException("Object path changed while checking owner dynamizations.");
                node = child.Value;
            }
            return false;
        }
        internal static EngineeringSnapshot Read(object node, string targetId, string path)
        {
            var state = new EngineeringSnapshot { TargetId = targetId, Path = path, NativeType = node.GetType().FullName!,
                CanDelete = path.Length != 0 && !path.Split('/').Last().StartsWith("p:", StringComparison.Ordinal) && !Protected(node) && node.GetType().GetMethod("Delete", Type.EmptyTypes) != null,
                CanExport = TransferNative.ExportMethod(node) != null, CanImport = TransferNative.ImportMethod(node) != null };
            var errors = new List<string>();
            try { state.Children = Children(node, "").Select(c => new EngineeringChild { Path = Join(path, c.Key), Label = c.Key, NativeType = c.Value.GetType().FullName! }).ToArray(); }
            catch (Exception ex) { errors.Add("children: " + ex.GetBaseException().Message); }
            var values = new Dictionary<string, EngineeringValue>(StringComparer.Ordinal);
            var unsupported = new List<string>();
            if (node is IEngineeringObject engineering)
            {
                foreach (var info in engineering.GetAttributeInfos().OrderBy(a => a.Name, StringComparer.Ordinal))
                {
                    if ((info.AccessMode & EngineeringAttributeAccessMode.Read) == 0) continue;
                    try
                    {
                        // SupportedTypes was added after V15.1; fall back to a public typed property.
                        var supported = info.GetType().GetProperty("SupportedTypes")?.GetValue(info, null) as IEnumerable<Type>
                            ?? new[] { node.GetType().GetProperty(info.Name)?.PropertyType }.Where(t => t != null).Cast<Type>();
                        var types = supported.Where(EngineeringCodec.Supports).ToArray();
                        if (!AddValue(values, node, info.Name, engineering.GetAttribute(info.Name), (info.AccessMode & EngineeringAttributeAccessMode.Write) != 0, types.Length == 1 ? types[0] : null))
                            unsupported.Add(info.Name + ": " + string.Join(" | ", supported.Select(t => t.FullName)));
                    }
                    catch (Exception ex) { errors.Add(info.Name + ": " + ex.GetBaseException().Message); }
                }
            }
            foreach (var prop in node.GetType().GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0 || values.ContainsKey(prop.Name) || !EngineeringCodec.Supports(prop.PropertyType)) continue;
                try { AddValue(values, node, prop.Name, prop.GetValue(node, null), prop.GetSetMethod(false) != null, prop.PropertyType); }
                catch (Exception ex) { errors.Add(prop.Name + ": " + ex.GetBaseException().Message); }
            }
            state.Attributes = values.Values.OrderBy(v => v.Name, StringComparer.Ordinal).ToArray();
            state.UnsupportedAttributes = unsupported.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (node is IEngineeringComposition comp)
            {
                try { state.Creations = comp.IsReadOnly ? Array.Empty<EngineeringCreation>() : comp.GetCreationInfos().Select(c => new EngineeringCreation {
                    NativeType = c.Type.FullName!, Parameters = c.ParameterInfos.Select(p => Parameter(node, c.Type, p)).ToArray()
                }).OrderBy(c => c.NativeType, StringComparer.Ordinal).ToArray(); }
                catch (Exception ex) { errors.Add("creation metadata: " + ex.GetBaseException().Message); }
            }
            state.Errors = errors.ToArray();
            return state;
        }
        private static EngineeringParameter Parameter(object composition, Type createdType, EngineeringCreationParameterInfo parameter)
        {
            Type? type = createdType.GetProperties().FirstOrDefault(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))?.PropertyType;
            if (type == null || !EngineeringCodec.Supports(type))
                type = composition.GetType().GetMethods().Where(m => m.Name == "Create").SelectMany(m => m.GetParameters())
                    .FirstOrDefault(p => p.Name!.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase) && EngineeringCodec.Supports(p.ParameterType))?.ParameterType;
            if (type == null && parameter.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) type = typeof(string);
            return new EngineeringParameter { Name = parameter.Name, Mandatory = parameter.IsMandatory, Type = type?.FullName ?? "unsupported",
                Choices = type?.IsEnum == true ? Enum.GetNames(type) : Array.Empty<string>() };
        }
        private static bool AddValue(IDictionary<string, EngineeringValue> values, object node, string name, object? value, bool writable, Type? declaredType)
        {
            Type? type = value?.GetType() ?? declaredType;
            if (type == null || !EngineeringCodec.Supports(type)) return false;
            values[name] = new EngineeringValue { Name = name, Type = type.FullName!, Value = value == null ? "<null>" : EngineeringCodec.Format(value), IsNull = value == null,
                Editable = writable && !Protected(node) && !Dynamic(node, name), Choices = type.IsEnum ? Enum.GetNames(type) : Array.Empty<string>() };
            return true;
        }
        internal static string Join(string parent, string child) => parent.Length == 0 ? child : parent + "/" + child;
        internal static void Write(object node, EngineeringSnapshot before, EngineeringSnapshot candidate)
        {
            foreach (var value in candidate.Attributes)
            {
                var old = before.Attributes.Single(v => v.Name == value.Name);
                if (value.Value == old.Value && value.IsNull == old.IsNull) continue;
                if (!old.Editable || Protected(node) || Dynamic(node, value.Name)) throw new InvalidOperationException("Attribute cannot be changed.");
                object parsed = EngineeringCodec.Parse(value.Value, EngineeringCodec.Resolve(value.Type));
                var prop = node.GetType().GetProperty(value.Name);
                if (prop?.GetSetMethod(false) != null) prop.SetValue(node, parsed, null);
                else ((IEngineeringObject)node).SetAttribute(value.Name, parsed);
            }
        }
        internal static object Create(object node, EngineeringSnapshot before, string typeName, CreationArgument[] arguments)
        {
            var creation = before.Creations.SingleOrDefault(c => c.NativeType == typeName) ?? throw new NotSupportedException("Type is not advertised by this composition.");
            var args = CreationValues(creation, arguments);
            return ((IEngineeringComposition)node).Create(EngineeringCodec.Resolve(typeName), args);
        }
        internal static Dictionary<string, object> CreationValues(EngineeringCreation creation, CreationArgument[] arguments)
        {
            if (arguments == null || arguments.Any(a => a == null || a.Name == null || a.Value == null) || arguments.GroupBy(a => a.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new ArgumentException("Missing or duplicate creation arguments.");
            if (creation.Parameters.Where(p => p.Mandatory).Any(p => !arguments.Any(a => a.Name == p.Name))) throw new ArgumentException("Missing mandatory creation arguments.");
            return arguments.ToDictionary(a => a.Name, a => {
                var p = creation.Parameters.SingleOrDefault(p => p.Name == a.Name) ?? throw new ArgumentException("Unknown creation argument: " + a.Name);
                return EngineeringCodec.Parse(a.Value, EngineeringCodec.Resolve(p.Type));
            }, StringComparer.Ordinal);
        }
    }
}
