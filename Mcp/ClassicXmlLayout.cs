using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicLayoutBounds
    {
        [JsonRequired] public int Left { get; set; }
        [JsonRequired] public int Top { get; set; }
        [JsonRequired] public int Width { get; set; }
        [JsonRequired] public int Height { get; set; }
        public ClassicLayoutBounds Copy() => new ClassicLayoutBounds { Left = Left, Top = Top, Width = Width, Height = Height };
        public bool Same(ClassicLayoutBounds other) => Left == other.Left && Top == other.Top && Width == other.Width && Height == other.Height;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicLayoutSelection
    {
        [JsonRequired] public string XPath { get; set; } = "";
        [JsonRequired] public ClassicLayoutBounds ExpectedBounds { get; set; } = null!;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicLayoutRequest
    {
        [JsonRequired] public string Action { get; set; } = "";
        [JsonRequired] public ClassicLayoutSelection[] Items { get; set; } = Array.Empty<ClassicLayoutSelection>();
        public int? DeltaX { get; set; }
        public int? DeltaY { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? Alignment { get; set; }
        public string? Axis { get; set; }
        public string? AnchorXPath { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ClassicLayoutInspection
    {
        [JsonRequired] public string Xml { get; set; } = "";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    internal sealed partial class ClassicXmlDocument
    {
        private const int LayoutLimit = 1000000;
        private static readonly string[] GeometryFields = { "Left", "Top", "Width", "Height" };
        private static readonly string[] LineEndpointFields = { "StartLeft", "StartTop", "EndLeft", "EndTop" };
        private static bool LayoutDimensionsValid(XElement node, int width, int height) => width <= LayoutLimit && height <= LayoutLimit
            && (node.Name == "Hmi.Screen.Line" ? width >= 0 && height >= 0 && (width != 0 || height != 0) : width > 0 && height > 0);
        private sealed class LayoutItem
        {
            public XElement Node = null!;
            public string XPath = "", ContainerXPath = "", Name = "";
            public string? UnsupportedReason;
            public ClassicLayoutBounds? Bounds;
            public XElement[] Fields = Array.Empty<XElement>();
            public XElement[] EndpointFields = Array.Empty<XElement>();
            public int[]? Endpoints;
        }
        public void ValidateNativeLineGeometry()
        {
            if (!resource.Descendants().Any(n => n.Name == "Hmi.Screen.Line" && (string?)n.Attribute("CompositionName") == "ScreenItems")) return;
            var invalid = LayoutItems().FirstOrDefault(i => i.Node.Name == "Hmi.Screen.Line" && i.Bounds == null);
            if (invalid != null) throw new ArgumentException("Invalid native Line geometry: " + invalid.UnsupportedReason);
        }
        private LayoutItem[] LayoutItems()
        {
            if (!new[] { "Hmi.Screen.Screen", "Hmi.Screen.ScreenTemplate", "Hmi.Screen.ScreenPopup", "Hmi.Screen.ScreenSlidein",
                "Hmi.Screen.ScreenOverview", "Hmi.Screen.ScreenGlobalElements" }.Contains(NativeType))
                throw new ArgumentException("Screen layout requires a native classic screen resource.");
            if (Name.Length > 512) throw new ArgumentException("Screen resource names exceed 512 characters.");
            var nodes = resource.DescendantsAndSelf().Take(50001).ToArray();
            if (nodes.Length > 50000 || nodes.Any(n => n.Ancestors().Take(65).Count() > 64))
                throw new ArgumentException("Screen layout exceeds 50000 XML elements or 64 levels.");
            var items = new List<LayoutItem>();
            foreach (var node in nodes.Where(n => n.Name.NamespaceName == "" && n.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)
                && (string?)n.Attribute("CompositionName") == "ScreenItems" && n.Parent?.Name == "ObjectList"))
            {
                string path = Path(node), container = Path(node.Parent!);
                if (path.Length > 4096 || container.Length > 4096) throw new ArgumentException("Screen layout paths exceed 4096 characters.");
                var item = new LayoutItem { Node = node, XPath = path, ContainerXPath = container };
                var attributes = node.Elements("AttributeList").ToArray();
                item.Name = ObjectNameField(node)?.Value ?? "";
                if (item.Name.Length > 512) throw new ArgumentException("Screen item names exceed 512 characters.");
                if (attributes.Length != 1 || item.Name.Length == 0) item.UnsupportedReason = "Exactly one AttributeList and a nonempty item name are required.";
                else
                {
                    var fields = GeometryFields.Select(name => attributes[0].Elements(name).ToArray()).ToArray();
                    int[] values = new int[4];
                    if (fields.Any(f => f.Length != 1 || f[0].HasElements || f[0].Nodes().Any(n => !(n is XText))))
                        item.UnsupportedReason = "Existing scalar Left, Top, Width and Height fields are required; no geometry is synthesized.";
                    else
                    {
                        item.Fields = fields.Select(f => f[0]).ToArray();
                        for (int i = 0; i < 4; i++)
                            if (!int.TryParse(item.Fields[i].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i])
                                || values[i] < -LayoutLimit || values[i] > LayoutLimit)
                                item.UnsupportedReason = "Geometry must contain bounded integers (absolute limit 1000000).";
                        if (item.UnsupportedReason == null && !LayoutDimensionsValid(node, values[2], values[3]))
                            item.UnsupportedReason = "Dimensions must be positive, except native Line controls may have one zero extent and one positive extent.";
                        if (item.UnsupportedReason == null && node.Name == "Hmi.Screen.Line")
                        {
                            var endpoints = LineEndpointFields.Select(name => attributes[0].Elements(name).ToArray()).ToArray();
                            var coordinates = new int[4];
                            if (endpoints.Any(f => f.Length != 1 || f[0].HasAttributes || f[0].HasElements || f[0].Nodes().Any(n => !(n is XText))))
                                item.UnsupportedReason = "Native Line geometry requires four existing scalar endpoint fields; no endpoints are synthesized.";
                            else
                            {
                                item.EndpointFields = endpoints.Select(f => f[0]).ToArray();
                                for (int i = 0; i < 4; i++)
                                    if (!int.TryParse(item.EndpointFields[i].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out coordinates[i])
                                        || coordinates[i] < -LayoutLimit || coordinates[i] > LayoutLimit)
                                        item.UnsupportedReason = "Line endpoints must contain bounded integers (absolute limit 1000000).";
                                if (item.UnsupportedReason == null && (values[0] != Math.Min(coordinates[0], coordinates[2])
                                    || values[1] != Math.Min(coordinates[1], coordinates[3])
                                    || values[2] != Math.Abs((long)coordinates[2] - coordinates[0])
                                    || values[3] != Math.Abs((long)coordinates[3] - coordinates[1])))
                                    item.UnsupportedReason = "Native Line bounds must agree with its primary endpoints.";
                                if (item.UnsupportedReason == null) item.Endpoints = coordinates;
                            }
                        }
                        if (item.UnsupportedReason == null) item.Bounds = new ClassicLayoutBounds { Left = values[0], Top = values[1], Width = values[2], Height = values[3] };
                    }
                }
                items.Add(item);
            }
            return items.ToArray();
        }
        public object LayoutCatalog(int offset, int maxItems)
        {
            if (offset < 0 || offset > 10000 || maxItems < 1 || maxItems > 200) throw new ArgumentException("offset must be 0..10000 and maxItems 1..200.");
            var all = LayoutItems(); var rows = new List<object>(); int characters = 0, next = offset;
            foreach (var item in all.Skip(offset).Take(maxItems))
            {
                var row = new { xPath = item.XPath, item.Name, nativeType = item.Node.Name.LocalName, containerXPath = item.ContainerXPath,
                    bounds = item.Bounds, endpoints = EndpointSnapshot(item.Endpoints), geometryAvailable = item.Bounds != null, unsupportedReason = item.UnsupportedReason,
                    selection = item.Bounds == null ? null : new ClassicLayoutSelection { XPath = item.XPath, ExpectedBounds = item.Bounds } };
                // Count escaped JSON, including repeated selection paths and non-ASCII names.
                int cost = JsonSerializer.Serialize(row, JsonDefaults.Options).Length;
                if (characters + cost > 258048) break;
                characters += cost; rows.Add(row);
                next++;
            }
            return new { Name, NativeType, items = rows, totalItems = all.Length, offset, nextOffset = next, hasMore = next < all.Length,
                payloadLimited = rows.Count < Math.Min(maxItems, Math.Max(0, all.Length - offset)),
                scope = "Stored classic XML geometry only. The native broker checks resource editability and the fresh resource fingerprint. Coordinates remain in each returned container; runtime dynamizations, rotation, canvas bounds and collisions are not evaluated." };
        }
        public object ArrangeLayout(ClassicLayoutRequest request)
        {
            if (request == null || request.Items == null || request.Items.Length < 1 || request.Items.Length > 100)
                throw new ArgumentException("Select 1 to 100 screen items with exact returned paths and expected bounds.");
            ValidateLayoutAction(request);
            var catalog = LayoutItems().ToDictionary(i => i.XPath, StringComparer.Ordinal);
            var selected = new List<LayoutItem>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var selection in request.Items)
            {
                if (selection == null || selection.XPath == null || selection.ExpectedBounds == null || !seen.Add(selection.XPath)
                    || !catalog.TryGetValue(selection.XPath, out var item)) throw new ArgumentException("Each selection must use a distinct exact path from inspect_screen_layout.");
                if (item.Bounds == null) throw new ArgumentException(item.UnsupportedReason);
                if (!item.Bounds.Same(selection.ExpectedBounds)) throw new InvalidOperationException("Screen item bounds changed; read the screen again: " + selection.XPath);
                selected.Add(item);
            }
            if (selected.Any(i => i.Node.Ancestors().Any(a => selected.Any(other => other.Node == a))))
                throw new ArgumentException("A container and its descendant cannot both be selected for one layout change.");
            var after = selected.ToDictionary(i => i.XPath, i => i.Bounds!.Copy(), StringComparer.Ordinal);
            if (request.Action == "move")
                foreach (var item in selected) { after[item.XPath].Left = LayoutCoordinate((decimal)item.Bounds!.Left + request.DeltaX!.Value); after[item.XPath].Top = LayoutCoordinate((decimal)item.Bounds.Top + request.DeltaY!.Value); }
            else if (request.Action == "resize")
                foreach (var item in selected) { if (request.Width.HasValue) after[item.XPath].Width = request.Width.Value; if (request.Height.HasValue) after[item.XPath].Height = request.Height.Value; }
            else
            {
                if (selected.Select(i => i.ContainerXPath).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new ArgumentException("Alignment/distribution requires items in the same XML coordinate container.");
                if (request.Action == "align")
                {
                    var anchor = selected.SingleOrDefault(i => i.XPath == request.AnchorXPath) ?? throw new ArgumentException("anchorXPath must be one of the selected exact paths.");
                    var a = anchor.Bounds!;
                    foreach (var item in selected.Where(i => i != anchor))
                    {
                        var b = after[item.XPath];
                        switch (request.Alignment)
                        {
                            case "left": b.Left = a.Left; break;
                            case "right": b.Left = LayoutCoordinate((decimal)a.Left + a.Width - b.Width); break;
                            case "horizontalCenter": b.Left = LayoutCoordinate(a.Left + ((decimal)a.Width - b.Width) / 2); break;
                            case "top": b.Top = a.Top; break;
                            case "bottom": b.Top = LayoutCoordinate((decimal)a.Top + a.Height - b.Height); break;
                            case "verticalCenter": b.Top = LayoutCoordinate(a.Top + ((decimal)a.Height - b.Height) / 2); break;
                        }
                    }
                }
                else
                {
                    bool horizontal = request.Axis == "horizontal";
                    // LINQ's stable ordering preserves the caller's item order for equal coordinates.
                    var ordered = selected.OrderBy(i => horizontal ? i.Bounds!.Left : i.Bounds!.Top).ToArray();
                    Func<LayoutItem, int> position = i => horizontal ? i.Bounds!.Left : i.Bounds!.Top;
                    Func<LayoutItem, int> size = i => horizontal ? i.Bounds!.Width : i.Bounds!.Height;
                    decimal first = position(ordered[0]), last = position(ordered.Last());
                    decimal free = last + size(ordered.Last()) - first - ordered.Sum(i => (decimal)size(i));
                    if (free < 0) throw new ArgumentException("The selected outer edges leave insufficient space for nonnegative equal gaps.");
                    decimal gap = free / (ordered.Length - 1), prefix = 0;
                    for (int i = 0; i < ordered.Length; i++)
                    {
                        int coordinate = i == ordered.Length - 1 ? position(ordered[i]) : LayoutCoordinate(first + prefix + i * gap);
                        if (horizontal) after[ordered[i].XPath].Left = coordinate; else after[ordered[i].XPath].Top = coordinate;
                        prefix += size(ordered[i]);
                    }
                }
            }
            var edits = new List<ClassicXmlEdit>();
            var afterEndpoints = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var item in selected)
            {
                var b = after[item.XPath]; int[] values = { b.Left, b.Top, b.Width, b.Height };
                if (!LayoutDimensionsValid(item.Node, b.Width, b.Height)) throw new ArgumentException("Resulting dimensions must be positive; only native Line controls allow one zero extent. A zero-length line is not supported.");
                int[] old = { item.Bounds!.Left, item.Bounds.Top, item.Bounds.Width, item.Bounds.Height };
                if (item.Endpoints != null)
                {
                    // Endpoints are primary native properties; bounds alone cannot be imported safely.
                    // Retain each nonzero axis direction. An expanded zero axis runs Start -> End.
                    int right = LayoutCoordinate((decimal)b.Left + b.Width), bottom = LayoutCoordinate((decimal)b.Top + b.Height);
                    bool reverseX = item.Endpoints[0] > item.Endpoints[2], reverseY = item.Endpoints[1] > item.Endpoints[3];
                    int[] endpoints = { reverseX ? right : b.Left, reverseY ? bottom : b.Top, reverseX ? b.Left : right, reverseY ? b.Top : bottom };
                    afterEndpoints.Add(item.XPath, endpoints);
                    for (int i = 0; i < 4; i++) if (item.Endpoints[i] != endpoints[i])
                        edits.Add(new ClassicXmlEdit { XPath = Path(item.EndpointFields[i]), ExpectedValue = item.EndpointFields[i].Value, NewValue = endpoints[i].ToString(CultureInfo.InvariantCulture) });
                }
                for (int i = 0; i < 4; i++) if (old[i] != values[i])
                    edits.Add(new ClassicXmlEdit { XPath = Path(item.Fields[i]), ExpectedValue = item.Fields[i].Value, NewValue = values[i].ToString(CultureInfo.InvariantCulture) });
            }
            if (edits.Count == 0) throw new ArgumentException("The requested layout contains no changes.");
            return new { candidateXml = Edit(edits.ToArray()), changes = new { request.Action, request.Alignment, request.Axis, request.AnchorXPath,
                rounding = "Nearest integer pixel; midpoint away from zero. Distribution preserves both outer edges; gaps may differ by one pixel.",
                lineDirection = "Preserve each nonzero endpoint axis direction; expanding a zero axis places Start at the minimum and End at the maximum.",
                items = selected.Select(i => new { xPath = i.XPath, i.Name, before = i.Bounds, after = after[i.XPath],
                    beforeEndpoints = EndpointSnapshot(i.Endpoints), afterEndpoints = EndpointSnapshot(afterEndpoints.TryGetValue(i.XPath, out var endpoints) ? endpoints : null) }).ToArray(), edits } };
        }
        private static object? EndpointSnapshot(int[]? values) => values == null ? null : new { startLeft = values[0], startTop = values[1], endLeft = values[2], endTop = values[3] };
        private static int LayoutCoordinate(decimal coordinate)
        {
            coordinate = decimal.Round(coordinate, 0, MidpointRounding.AwayFromZero);
            if (coordinate < -LayoutLimit || coordinate > LayoutLimit) throw new ArgumentException("Resulting position exceeds the layout limit of +/-1000000.");
            return (int)coordinate;
        }
        private static void ValidateLayoutAction(ClassicLayoutRequest r)
        {
            bool move = r.Action == "move", align = r.Action == "align", distribute = r.Action == "distribute", resize = r.Action == "resize";
            if (!move && !align && !distribute && !resize) throw new ArgumentException("action must be move, align, distribute or resize.");
            if (move != r.DeltaX.HasValue || move != r.DeltaY.HasValue || align != (r.Alignment != null) || align != (r.AnchorXPath != null)
                || distribute != (r.Axis != null) || (!resize && (r.Width.HasValue || r.Height.HasValue)))
                throw new ArgumentException("Supply only the fields required for the selected layout action.");
            if (move && (Math.Abs((long)r.DeltaX!.Value) > LayoutLimit || Math.Abs((long)r.DeltaY!.Value) > LayoutLimit)) throw new ArgumentException("Move deltas must be within +/-1000000.");
            if (align && (r.Items.Length < 2 || !new[] { "left", "right", "horizontalCenter", "top", "bottom", "verticalCenter" }.Contains(r.Alignment)))
                throw new ArgumentException("Alignment requires at least two items and an explicit supported alignment.");
            if (distribute && (r.Items.Length < 3 || !new[] { "horizontal", "vertical" }.Contains(r.Axis))) throw new ArgumentException("Distribution requires at least three items and horizontal or vertical axis.");
            if (resize && ((!r.Width.HasValue && !r.Height.HasValue) || r.Width < 0 || r.Width > LayoutLimit || r.Height < 0 || r.Height > LayoutLimit))
                throw new ArgumentException("Resize requires width and/or height, each within 0..1000000. Zero extents require a native Line control with a nonzero other extent.");
        }
    }
}
