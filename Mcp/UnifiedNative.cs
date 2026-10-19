using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;

namespace SiemensMCPServer.Mcp
{
    // Reflection is restricted to documented public Openness members, so classic builds
    // do not acquire a static reference to Unified types absent from older Siemens APIs.
    internal static class UnifiedNative
    {
        private const string SoftwareType = "Siemens.Engineering.HmiUnified.HmiSoftware";
        private const string ScreenType = "Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen";
        private const string ButtonType = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton";

        public static object ApiCapabilities()
        {
            Assembly assembly = typeof(Siemens.Engineering.HW.Software).Assembly;
            Type? software = ResolveType(SoftwareType);
            Type? screen = ResolveType(ScreenType);
            Type? button = ResolveType(ButtonType);
            bool screens = Has(software, "Screens") && Has(screen, "ScreenItems");
            bool edits = screens && Has(button, "Text") && Has(button, "Left") && Has(button, "Top") && Has(button, "Width") && Has(button, "Height") && Has(button, "Dynamizations");
            return new {
                apiAssembly = assembly.GetName().Name, apiVersion = assembly.GetName().Version!.ToString(),
                classicTextLists = true, unifiedDiscovery = software != null, unifiedScreenInspection = screens,
                comfortXmlResourceEdits = true, classicTextListStructureChanges = true, primaryEngineeringProfile = "Comfort/classic V15.1",
                classicTranslationSlotRemoval = new { available = assembly.GetName().Version!.Major == 15 && assembly.GetName().Version!.Minor == 1 ? (bool?)false : null,
                    reason = "V15.1 XML import retains active language slots; clear existing translation text instead. Other profiles require native acceptance." },
                classicAlarmDefinitions = new { available = false, reason = "No dedicated classic alarm-definition endpoint in this adapter or the inspected V15.1 HmiTarget API. Native editor alarm XLSX workflows are separate from screen XML." },
                plcEngineering = new { available = ResolveType("Siemens.Engineering.SW.PlcSoftware") != null,
                    scope = "Offline blocks, PLC data types, tag tables/groups, native XML import/export, SCL/STL source generation and compile diagnostics. Native trials pending.",
                    downloadOrRuntimeControl = false },
                plcInspection = PlcInspectionNative.Capabilities(),
                projectLibraries = LibraryInspectionNative.Capabilities(),
                projectTexts = ProjectTextNative.Capabilities(),
                unifiedScreenEdits = edits, unifiedScreenGroups = Has(software, "ScreenGroups"),
                unifiedTextListNativeApiPresent = Has(software, "HmiTextLists"), unifiedTextListEditing = UnifiedTextListNative.Supports(software),
                engineeringObjectTools = new { browse = true, typedScalarEdits = true, nativeCreationAndDeletion = "per-object self-description", nativeImportExport = "per-object method presence", bulkImport = "native resources and supported folder hierarchy only" },
                unifiedRootNavigators = software == null ? Array.Empty<string>() : software.GetProperties().Where(p => p.Name != "Parent").Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                scope = "Comfort/classic and Unified API-visible objects, supported scalar properties, advertised creation/deletion, native resource and bulk transfers. Complex values require native resource import or a specialized adapter. Native acceptance pending."
            };
        }

        public static bool CanInspect(object software) => Has(software.GetType(), "Screens");
        private static Type? ResolveType(string name) => SiemensApiTypes.Resolve(typeof(Project).Assembly, name, TiaPortalAssemblyResolver.LoadOptionalModule);
        private static bool Has(Type? type, string name) => type?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) != null;
        private static PropertyInfo Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new NotSupportedException("Installed Unified API does not expose " + target.GetType().FullName + "." + name);
        private static object Get(object target, string name) => Property(target, name).GetValue(target, null)!;
        private static IEnumerable<object> Each(object composition) => ((IEnumerable)composition).Cast<object>();

        internal sealed class NamedObject
        {
            public string Path = "";
            public object Native = null!;
            public string Name => (string)Get(Native, "Name");
        }

        public static IList<NamedObject> Screens(object software)
        {
            if (!CanInspect(software)) throw new NotSupportedException("This installed Unified API does not expose screen discovery.");
            return UnifiedScreenCatalog.Read(software).Select(s => new NamedObject { Path = s.Path, Native = s.Native }).ToList();
        }
        public static object FindScreen(object software, string path) => Screens(software).SingleOrDefault(s => s.Path == path)?.Native
            ?? throw new InvalidOperationException("Unified screen path does not exist. Use hmi_list_screens.");

        public static IList<NamedObject> Items(object screen)
        {
            var items = Each(Get(screen, "ScreenItems")).Select(item => new NamedObject {
                Path = "items/" + Uri.EscapeDataString((string)Get(item, "Name")), Native = item }).ToList();
            if (items.GroupBy(i => i.Path, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new InvalidOperationException("Ambiguous Unified screen item paths.");
            return items;
        }
        public static object FindObject(object software, string screenPath, string itemPath)
        {
            object screen = FindScreen(software, screenPath);
            return itemPath.Length == 0 ? screen : Items(screen).SingleOrDefault(i => i.Path == itemPath)?.Native
                ?? throw new InvalidOperationException("Unified item path does not exist. Use hmi_list_screen_items.");
        }

        public static UnifiedObjectState Read(object target, string hmiId, string screenPath, string itemPath)
        {
            var state = new UnifiedObjectState { HmiId = hmiId, ScreenPath = screenPath, ItemPath = itemPath, NativeType = target.GetType().FullName! };
            // Failure to read bindings is an error, never permission to overwrite them.
            state.DynamizedProperties = Each(Get(target, "Dynamizations")).Select(d => (string)Get(d, "PropertyName")).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var scalars = new List<UnifiedScalar>();
            foreach (string name in UnifiedScalarCodec.Allowed.OrderBy(n => n, StringComparer.Ordinal))
            {
                PropertyInfo? property = target.GetType().GetProperty(name);
                if (property == null || !UnifiedScalarCodec.Types.Contains(property.PropertyType.FullName!)) continue;
                scalars.Add(new UnifiedScalar { Property = name, Type = property.PropertyType.FullName!, Value = UnifiedScalarCodec.Format(property.GetValue(target, null)!),
                    Editable = property.CanWrite && !state.IsDynamic(name) });
            }
            state.Scalars = scalars.ToArray();
            var texts = new List<UnifiedText>();
            foreach (string name in UnifiedTextCodec.Allowed.OrderBy(n => n, StringComparer.Ordinal))
            {
                PropertyInfo? property = target.GetType().GetProperty(name);
                if (property == null || property.PropertyType != typeof(MultilingualText)) continue;
                var multilingual = (MultilingualText)property.GetValue(target, null)!;
                foreach (MultilingualTextItem item in multilingual.Items)
                {
                    string native = item.Text ?? "";
                    string? text;
                    bool simple = UnifiedTextCodec.TryRead(native, out text);
                    texts.Add(new UnifiedText { Property = name, Culture = item.Language.Culture.Name, NativeText = native, Text = text,
                        Editable = simple && !state.IsDynamic(name) });
                }
            }
            if (texts.GroupBy(t => t.Property + "\0" + t.Culture.ToLowerInvariant(), StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Duplicate native Unified text languages.");
            state.Texts = texts.OrderBy(t => t.Property, StringComparer.Ordinal).ThenBy(t => t.Culture, StringComparer.Ordinal).ToArray();
            return state;
        }

        public static void WriteChanged(object target, UnifiedObjectState before, UnifiedObjectState candidate)
        {
            foreach (var scalar in candidate.Scalars)
            {
                var old = before.Scalars.Single(s => s.Property == scalar.Property);
                if (old.Value == scalar.Value) continue;
                if (!old.Editable || before.IsDynamic(scalar.Property)) throw new InvalidOperationException("Native scalar is not editable.");
                Property(target, scalar.Property).SetValue(target, UnifiedScalarCodec.Parse(scalar.Value, scalar.Type), null);
            }
            foreach (var text in candidate.Texts)
            {
                var old = before.Texts.Single(t => t.Property == text.Property && t.Culture == text.Culture);
                if (old.NativeText == text.NativeText) continue;
                if (!old.Editable || before.IsDynamic(text.Property)) throw new InvalidOperationException("Native text is not editable.");
                var multilingual = (MultilingualText)Get(target, text.Property);
                var item = multilingual.Items.Cast<MultilingualTextItem>().Single(t => string.Equals(t.Language.Culture.Name, text.Culture, StringComparison.Ordinal));
                item.Text = text.NativeText;
            }
        }
    }
}
