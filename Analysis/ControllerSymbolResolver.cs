using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SiemensMCPServer.Analysis
{
    internal sealed class SymbolResolution
    {
        public string Status { get; set; } = "unresolved";
        public string Reason { get; set; } = "";
        public string Requested { get; set; } = "";
        public string CanonicalSymbol { get; set; } = "";
        public string DataType { get; set; } = "";
        public DeclarationDimension[] Dimensions { get; set; } = Array.Empty<DeclarationDimension>();
        public string[] AliasChain { get; set; } = Array.Empty<string>();
        public string[] DeclarationPaths { get; set; } = Array.Empty<string>();
        public string ExternalAccess { get; set; } = "";
        public bool PhysicalOffsetKnown { get; set; }
    }
    internal sealed class ControllerSymbolResolver
    {
        private readonly ControllerDeclaration controller;
        private readonly Dictionary<string, ControllerSymbol> symbols;
        private readonly Dictionary<string, ControllerType> types;
        private static readonly HashSet<string> Atomics = new HashSet<string>(new[] { "BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "BYTE", "WORD", "DWORD", "LWORD", "REAL", "LREAL", "STRING", "WSTRING", "TIME", "DATE", "DT", "TOD" }, StringComparer.Ordinal);
        public static string Atomic(string type) => type.ToUpperInvariant() == "BOOLEAN" ? "BOOL" : type.ToUpperInvariant();
        public static bool IsAtomic(string type) => Atomics.Contains(Atomic(type));
        private sealed class Segment { public string Name = ""; public int[]? Indices; }
        public ControllerSymbolResolver(ControllerDeclaration controller)
        {
            this.controller = controller;
            symbols = controller.Symbols.ToDictionary(s => s.QualifiedName, ControllerDeclarations.Names);
            types = controller.Types.ToDictionary(t => t.Name, ControllerDeclarations.Names);
        }
        private static List<Segment>? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return null;
            var result = new List<Segment>(); int position = 0;
            while (position < text.Length && result.Count < 64)
            {
                if (text[position] == '[')
                {
                    int end = text.IndexOf(']', position);
                    if (end < 0) return null;
                    var indexes = text.Substring(position + 1, end - position - 1).Split(',');
                    if (indexes.Length > 8) return null;
                    var values = new int[indexes.Length];
                    for (int i = 0; i < values.Length; i++)
                        if (!int.TryParse(indexes[i].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i])) return null;
                    result.Add(new Segment { Indices = values }); position = end + 1;
                }
                else
                {
                    if (position != 0) { if (text[position] != '.') return null; position++; }
                    if (position == text.Length) return null;
                    string name;
                    if (text[position] == '"')
                    {
                        position++; var nameText = new System.Text.StringBuilder(); bool closed = false;
                        while (position < text.Length)
                        {
                            if (text[position] != '"') { nameText.Append(text[position++]); continue; }
                            position++;
                            if (position < text.Length && text[position] == '"') { nameText.Append('"'); position++; continue; }
                            closed = true; break;
                        }
                        if (!closed || nameText.Length == 0) return null; name = nameText.ToString();
                    }
                    else
                    {
                        int begin = position;
                        while (position < text.Length && text[position] != '.' && text[position] != '[') position++;
                        name = text.Substring(begin, position - begin);
                        if (!Regex.IsMatch(name, @"^(?:[A-Za-z_][A-Za-z_0-9:]*|[0-9]+)$", RegexOptions.CultureInvariant)) return null;
                    }
                    result.Add(new Segment { Name = name });
                }
            }
            return position == text.Length && result.Count != 0 && result[0].Indices == null ? result : null;
        }
        private static string Suffix(IEnumerable<Segment> segments) => string.Concat(segments.Select(s => s.Indices == null
            ? "." + (Regex.IsMatch(s.Name, @"^[A-Za-z_0-9:]+$") ? s.Name : "\"" + s.Name.Replace("\"", "\"\"") + "\"")
            : "[" + string.Join(",", s.Indices) + "]"));
        private static string Restricted(string a, string b)
        {
            if (new[] { a, b }.Any(v => v.Equals("None", StringComparison.OrdinalIgnoreCase))) return "None";
            if (new[] { a, b }.Any(v => v.Equals("Read Only", StringComparison.OrdinalIgnoreCase))) return "Read Only";
            return a.Length == 0 ? b : a;
        }
        public SymbolResolution Resolve(string address, string defaultScope = "")
        {
            var result = Resolve(address, defaultScope, new HashSet<string>(ControllerDeclarations.Names), 0);
            result.Requested = address; return result;
        }
        public SymbolResolution ResolveAddress(string address, string defaultScope, string syntax)
        {
            if (syntax == "canonical") return Resolve(address, defaultScope);
            if (syntax != "wincc-rockwell") return new SymbolResolution { Requested = address, Reason = "Unsupported connection address syntax." };
            var program = Regex.Match(address, @"^([A-Za-z_][A-Za-z_0-9]*):([A-Za-z_][A-Za-z_0-9]*)([.\[].*|)$", RegexOptions.CultureInvariant);
            if (!program.Success || address.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)) return Resolve(address, defaultScope);
            string nativeRoot = program.Groups[1].Value + ":" + program.Groups[2].Value;
            string scopedRoot = "Program:" + program.Groups[1].Value + "." + program.Groups[2].Value;
            bool literal = symbols.ContainsKey(nativeRoot), scoped = symbols.ContainsKey(scopedRoot);
            if (literal && scoped) return new SymbolResolution { Status = "ambiguousAddress", Requested = address, Reason = "Address matches both a literal controller tag and a WinCC program-scoped tag." };
            var result = Resolve(scoped ? scopedRoot + program.Groups[3].Value : address, defaultScope); result.Requested = address; return result;
        }
        private SymbolResolution Resolve(string address, string scope, HashSet<string> aliases, int depth)
        {
            var result = new SymbolResolution { Requested = address };
            SymbolResolution Fail(string status, string reason) { result.Status = status; result.Reason = reason; return result; }
            if (depth > 32) return Fail("unresolved", "Alias/type traversal exceeds 32 levels.");
            // Explicit PLCopen addresses are declarations, not inferred physical offsets.
            var addressed = controller.Symbols.Where(s => s.Address.Length != 0 && ControllerDeclarations.Names.Equals(s.Address, address)).ToArray();
            if (addressed.Length > 1) return Fail("ambiguous", "More than one symbol declares this address.");
            if (addressed.Length == 1) address = addressed[0].QualifiedName;
            var parsed = Parse(address);
            if (parsed == null) return Fail("unresolved", "Unsupported symbolic/address syntax; computed and indirect expressions are not evaluated.");
            ControllerSymbol? symbol = null; int consumed = 0;
            // Prefer the longest exact declared scope/root, never a suffix or fuzzy name match.
            for (int count = parsed.TakeWhile(p => p.Indices == null).Count(); count > 0; count--)
            {
                string root = string.Join(".", parsed.Take(count).Select(p => p.Name));
                if (scope.Length != 0 && symbols.TryGetValue(scope + "." + root, out symbol) || symbols.TryGetValue(root, out symbol)) { consumed = count; break; }
            }
            if (symbol == null) return Fail("missingSymbol", "Controller root/scope is absent from the supplied declarations.");
            if (symbol.AliasFor.Length != 0)
            {
                if (!aliases.Add(symbol.QualifiedName)) return Fail("aliasCycle", "Controller aliases form a cycle.");
                var resolved = Resolve(symbol.AliasFor + Suffix(parsed.Skip(consumed)), symbol.Scope, aliases, depth + 1);
                resolved.AliasChain = new[] { symbol.QualifiedName }.Concat(resolved.AliasChain).ToArray();
                resolved.DeclarationPaths = new[] { symbol.QualifiedName }.Concat(resolved.DeclarationPaths).Distinct(ControllerDeclarations.Names).ToArray();
                resolved.ExternalAccess = Restricted(symbol.ExternalAccess, resolved.ExternalAccess);
                if (resolved.Status == "resolved" && parsed.Count == consumed && symbol.DataType.Length != 0 && IsAtomic(symbol.DataType)
                    && Atomic(symbol.DataType) != Atomic(resolved.DataType)) { resolved.Status = "aliasTypeMismatch"; resolved.Reason = "Declared alias type differs from its resolved base."; }
                return resolved;
            }
            string dataType = symbol.DataType, canonical = symbol.QualifiedName;
            var dimensions = symbol.Dimensions; var declarations = new List<string> { canonical }; string access = symbol.ExternalAccess;
            var typeStack = new HashSet<string>(ControllerDeclarations.Names);
            bool Unwrap()
            {
                while (types.TryGetValue(dataType, out var definition) && definition.Kind == "alias")
                {
                    if (!typeStack.Add(dataType) || typeStack.Count > 32) return false;
                    if (dimensions.Length != 0 && definition.Dimensions.Length != 0) return false;
                    if (definition.Dimensions.Length != 0) dimensions = definition.Dimensions;
                    dataType = definition.BaseType;
                }
                return dataType.Length != 0;
            }
            for (int index = consumed; index < parsed.Count; index++)
            {
                if (!Unwrap()) return Fail("unresolved", "Unsupported or cyclic controller type alias.");
                var segment = parsed[index];
                if (segment.Indices != null)
                {
                    if (dimensions.Length != segment.Indices.Length) return Fail("arrayRankMismatch", "Array index rank differs from the declaration.");
                    for (int axis = 0; axis < dimensions.Length; axis++)
                        if (segment.Indices[axis] < dimensions[axis].Lower || segment.Indices[axis] > dimensions[axis].Upper) return Fail("arrayBounds", "Array index is outside the declared bounds.");
                    canonical += "[" + string.Join(",", segment.Indices) + "]"; dimensions = Array.Empty<DeclarationDimension>();
                    declarations.Add(canonical); continue;
                }
                if (dimensions.Length != 0) return Fail("arrayIndexRequired", "Select an array element before addressing a member.");
                string atomic = Atomic(dataType);
                if (int.TryParse(segment.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int bit))
                {
                    int bits = new[] { "SINT", "USINT", "BYTE" }.Contains(atomic) ? 8 : new[] { "INT", "UINT", "WORD" }.Contains(atomic) ? 16
                        : new[] { "DINT", "UDINT", "DWORD" }.Contains(atomic) ? 32 : new[] { "LINT", "ULINT", "LWORD" }.Contains(atomic) ? 64 : 0;
                    if (bit < 0 || bit >= bits) return Fail("bitBounds", "Bit selector is invalid for the declared atomic type.");
                    dataType = "BOOL"; canonical += "." + bit.ToString(CultureInfo.InvariantCulture); declarations.Add(canonical); continue;
                }
                if (!types.TryGetValue(dataType, out var type) || type.Kind != "struct") return Fail("unknownType", "Member declarations for this controller type are unavailable.");
                var member = type.Members.SingleOrDefault(m => ControllerDeclarations.Names.Equals(m.Name, segment.Name));
                if (member == null) return Fail("missingMember", "Controller member is absent from the supplied type declaration.");
                if (member.Hidden) return Fail("hiddenMember", "The selected controller member is hidden.");
                canonical += "." + member.Name; declarations.Add(canonical); dataType = member.DataType; dimensions = member.Dimensions;
                access = Restricted(access, member.ExternalAccess); typeStack.Clear();
            }
            if (!Unwrap()) return Fail("unresolved", "Unsupported or cyclic controller type alias.");
            if (types.TryGetValue(dataType, out var finalType) && finalType.Kind == "unsupported") return Fail("unknownType", "The selected controller type requires an unsupported adapter.");
            if (!IsAtomic(dataType) && !types.ContainsKey(dataType)) return Fail("unknownType", "The selected controller data type is undeclared.");
            result.Status = "resolved"; result.CanonicalSymbol = canonical; result.DataType = IsAtomic(dataType) ? Atomic(dataType) : dataType;
            result.Dimensions = dimensions; result.DeclarationPaths = declarations.ToArray(); result.ExternalAccess = access;
            return result;
        }
    }
}
