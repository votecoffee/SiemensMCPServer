using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SiemensMCPServer.Mcp
{
    // Only the documented library compositions and scalar metadata below are read.
    // Never follows Parent, TypeObject, dependencies or engineering instances.
    internal sealed class LibraryCatalogReader
    {
        internal const int MaxVisits = 20000, MaxPayloadCharacters = 262144, MaxFieldCharacters = 2048;
        private int visits;
        private sealed class Node
        {
            public object Native = null!;
            public string LibraryId = "", Path = "", Kind = "", Name = "";
        }
        public static void ValidatePath(string path)
        {
            if (path.Length > 4096 || path.Any(char.IsControl)) throw new ArgumentException("Invalid library path.");
            if (path == "") return;
            string[] parts = path.Split('/');
            if (parts.Length > 35 || parts[0] != "types" && parts[0] != "masterCopies") throw new ArgumentException("Use an exact returned library path.");
            int index = 1;
            while (index < parts.Length && parts[index].StartsWith("f:", StringComparison.Ordinal)) { Decode(parts[index++], "f:"); }
            if (index > 33) throw new ArgumentException("Library folder nesting exceeds 32 levels.");
            if (index == parts.Length) return;
            if (parts[0] == "types") {
                Decode(parts[index++], "t:");
                if (index < parts.Length) Decode(parts[index++], "v:");
            } else {
                Decode(parts[index++], "m:");
                if (index < parts.Length) {
                    string part = parts[index++]; int content;
                    if (!part.StartsWith("c:", StringComparison.Ordinal) || !int.TryParse(part.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out content)
                        || content < 0 || content > MaxVisits || part != "c:" + content.ToString(CultureInfo.InvariantCulture)) throw new ArgumentException("Invalid content-description path.");
                }
            }
            if (index != parts.Length) throw new ArgumentException("Unsupported library navigation.");
        }
        private static string Decode(string part, string prefix)
        {
            if (!part.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Use an exact returned library path.");
            string name = Uri.UnescapeDataString(part.Substring(prefix.Length));
            if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Any(char.IsControl) || prefix + Uri.EscapeDataString(name) != part)
                throw new ArgumentException("Invalid or noncanonical library path identity.");
            return name;
        }
        internal static string GlobalId(string path)
        {
            if (!System.IO.Path.IsPathRooted(path) || path.Length > 4096) throw new NotSupportedException("Global library has no supported absolute native path.");
            using (var hash = SHA256.Create()) return "global:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
        }
        public LibrarySnapshot Capture(object projectLibrary, IEnumerable<object> globalLibraries, LibraryCapture capture)
        {
            visits = 0;
            capture.Validate();
            var libraries = Libraries(projectLibrary, globalLibraries);
            if (capture.LibraryId == "") return Page(libraries, capture, null);
            Node? root = capture.LibraryId == "project" ? new Node { Native = projectLibrary, Kind = "projectLibrary", LibraryId = "project", Name = "Project library" } : null;
            if (root == null) foreach (var library in libraries) {
                Visit();
                if (library.LibraryId == capture.LibraryId) {
                    if (root != null) throw new InvalidOperationException("Ambiguous open-library identity.");
                    root = library;
                }
            }
            if (root == null) throw new InvalidOperationException("Library is not currently open in the attached TIA process. Rediscover with list_libraries.");
            Node selected = root;
            if (capture.Path != "") {
                foreach (string segment in capture.Path.Split('/')) selected = Find(selected, segment);
            }
            return Page(Children(selected), capture, Row(selected));
        }
        private IEnumerable<Node> Libraries(object projectLibrary, IEnumerable<object> globals)
        {
            yield return new Node { Native = projectLibrary, Kind = "projectLibrary", LibraryId = "project", Name = "Project library" };
            foreach (object library in globals) {
                Visit();
                object path = Required(library, "Path");
                if (!(path is FileInfo file)) throw new NotSupportedException("The open global library does not expose a native file path.");
                yield return new Node { Native = library, Kind = "globalLibrary", LibraryId = GlobalId(file.FullName), Name = Identity(library, "Name") };
            }
        }
        private Node Find(Node owner, string segment)
        {
            Node? found = null;
            foreach (var child in Children(owner)) {
                Visit();
                string leaf = child.Path.Substring(child.Path.LastIndexOf('/') + 1);
                if (leaf == segment) {
                    if (found != null) throw new InvalidOperationException("Ambiguous library child identity.");
                    found = child;
                }
            }
            return found ?? throw new InvalidOperationException("Library path no longer exists. Rediscover its parent.");
        }
        private IEnumerable<Node> Children(Node owner)
        {
            if (owner.Kind == "projectLibrary" || owner.Kind == "globalLibrary") {
                yield return Child(owner, Required(owner.Native, "TypeFolder"), "types", "typeFolder", "Types");
                yield return Child(owner, Required(owner.Native, "MasterCopyFolder"), "masterCopies", "masterCopyFolder", "Master copies");
            } else if (owner.Kind == "typeFolder" || owner.Kind == "masterCopyFolder") {
                foreach (object folder in Each(owner.Native, "Folders")) {
                    string name = Identity(folder, "Name");
                    yield return Child(owner, folder, "f:" + Uri.EscapeDataString(name), owner.Kind, name);
                }
                bool types = owner.Kind == "typeFolder";
                foreach (object item in Each(owner.Native, types ? "Types" : "MasterCopies")) {
                    string name = Identity(item, "Name");
                    yield return Child(owner, item, (types ? "t:" : "m:") + Uri.EscapeDataString(name), types ? "type" : "masterCopy", name);
                }
            } else if (owner.Kind == "type") {
                foreach (object version in Each(owner.Native, "Versions")) {
                    string number = Identity(version, "VersionNumber");
                    yield return Child(owner, version, "v:" + Uri.EscapeDataString(number), "version", number);
                }
            } else if (owner.Kind == "masterCopy") {
                int index = 0;
                foreach (object content in Each(owner.Native, "ContentDescriptions"))
                    yield return Child(owner, content, "c:" + (index++).ToString(CultureInfo.InvariantCulture), "content", Identity(content, "ContentName"));
            }
        }
        private static Node Child(Node parent, object native, string segment, string kind, string name)
        {
            string path = parent.Path.Length == 0 ? segment : parent.Path + "/" + segment;
            ValidatePath(path);
            return new Node { Native = native, Kind = kind, Path = path, LibraryId = parent.LibraryId, Name = name };
        }
        private LibrarySnapshot Page(IEnumerable<Node> nodes, LibraryCapture capture, LibraryRow? selected)
        {
            var rows = new List<LibraryRow>(); int skipped = 0, characters = selected == null ? 0 : Size(selected);
            bool more = false, limited = false;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (Node node in nodes) {
                Visit();
                if (!identities.Add(node.LibraryId + "/" + node.Path)) throw new InvalidOperationException("Ambiguous library page identity.");
                if (skipped++ < capture.Offset) continue;
                if (rows.Count == capture.MaxRows) { more = true; break; }
                var row = Row(node); int size = Size(row);
                if (characters + size > MaxPayloadCharacters) { more = true; limited = true; break; }
                characters += size; rows.Add(row);
            }
            return new LibrarySnapshot { LibraryId = capture.LibraryId, Path = capture.Path, Selected = selected, Rows = rows.ToArray(), Offset = capture.Offset,
                HasMore = more, PayloadLimited = limited, OffsetLimitReached = more && capture.Offset + rows.Count > 10000,
                NextOffset = more && capture.Offset + rows.Count <= 10000 ? (int?)(capture.Offset + rows.Count) : null };
        }
        private static int Size(LibraryRow row) => row.LibraryId.Length + row.Path.Length + row.Kind.Length + row.Name.Length + row.NativeType.Length
            + row.Fields.Sum(p => p.Key.Length + (p.Value?.Length ?? 0)) + row.UnsupportedFields.Sum(s => s.Length) + row.TruncatedFields.Sum(s => s.Length);
        private static LibraryRow Row(Node node)
        {
            var result = new LibraryRow { LibraryId = node.LibraryId, Path = node.Path, Kind = node.Kind, Name = node.Name,
                NativeType = node.Native.GetType().FullName ?? "", CanBrowse = node.Kind != "version" && node.Kind != "content" };
            string[] fields = node.Kind == "globalLibrary" ? new[] { "Path", "Author", "IsReadOnly", "IsModified" }
                : node.Kind == "type" ? new[] { "Guid", "Author", "Namespace", "MinimumTargetDeviceVersion", "Status", "SetForUpdate" }
                : node.Kind == "version" ? new[] { "Guid", "VersionNumber", "Author", "ModifiedDate", "State", "IsDefault" }
                : node.Kind == "masterCopy" ? new[] { "Author", "CreationDate" }
                : node.Kind == "content" ? new[] { "ContentName", "ContentType" } : Array.Empty<string>();
            var unsupported = new List<string>(); var truncated = new List<string>();
            foreach (string field in fields) {
                PropertyInfo? property = node.Native.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                if (property == null) { unsupported.Add(field); continue; }
                string? value = Scalar(property.GetValue(node.Native, null));
                if (value != null && value.Length > MaxFieldCharacters) {
                    int length = MaxFieldCharacters;
                    if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
                    value = value.Substring(0, length); truncated.Add(field);
                }
                result.Fields.Add(field, value);
            }
            result.UnsupportedFields = unsupported.ToArray(); result.TruncatedFields = truncated.ToArray(); return result;
        }
        private static string Identity(object node, string property)
        {
            string name = Scalar(Required(node, property)) ?? "";
            if (name.Length == 0 || name.Length > 512 || name.Any(char.IsControl)) throw new NotSupportedException("Unsupported native library identity.");
            return name;
        }
        private void Visit() { if (++visits > MaxVisits) throw new InvalidOperationException("Library inspection exceeds the 20000-node scan limit. Browse a narrower folder or reduce the offset."); }
        private static object Required(object node, string property) => node.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(node, null)
            ?? throw new NotSupportedException("Native library API does not expose " + node.GetType().FullName + "." + property);
        private static IEnumerable<object> Each(object node, string property) => (Required(node, property) as IEnumerable)?.Cast<object>()
            ?? throw new NotSupportedException("Native library composition is unavailable: " + property);
        private static string? Scalar(object? value)
        {
            if (value == null) return null;
            if (value is string text) return text;
            if (value is bool flag) return flag ? "true" : "false";
            if (value is Guid guid) return guid.ToString("D");
            if (value is Version version) return version.ToString();
            if (value is DateTime date) return date.ToString("o", CultureInfo.InvariantCulture);
            if (value is Type type) return type.FullName;
            if (value is FileInfo file) return file.FullName;
            if (value.GetType().IsEnum) return value.ToString();
            throw new NotSupportedException("Unsupported native library metadata value: " + value.GetType().FullName);
        }
    }
}
