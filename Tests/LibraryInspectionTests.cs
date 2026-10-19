using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class LibraryInspectionTests
{
    public class Library
    {
        public TypeFolder TypeFolder { get; set; } = new();
        public CopyFolder MasterCopyFolder { get; set; } = new();
        public object Parent => throw new Exception("Parent must not be read");
    }
    public sealed class GlobalLibrary : Library
    {
        public string Name { get; set; } = "Common library";
        public FileInfo Path { get; set; } = new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synthetic-library.al15_1"));
        public string Author { get; set; } = "Author";
        public bool IsReadOnly => true;
        public bool IsModified => false;
    }
    public sealed class TypeFolder
    {
        public string Name { get; set; } = "";
        public TypeFolder[] Folders { get; set; } = Array.Empty<TypeFolder>();
        public LibraryType[] Types { get; set; } = Array.Empty<LibraryType>();
    }
    public sealed class CopyFolder
    {
        public string Name { get; set; } = "";
        public CopyFolder[] Folders { get; set; } = Array.Empty<CopyFolder>();
        public Copy[] MasterCopies { get; set; } = Array.Empty<Copy>();
    }
    public sealed class LibraryType
    {
        public string Name { get; set; } = "Counter";
        public Guid Guid { get; set; } = Guid.Parse("0bfdaafa-d5b8-4d40-9503-75e7f7d95edf");
        public string Author { get; set; } = "Author";
        public TypeVersion[] Versions { get; set; } = Array.Empty<TypeVersion>();
        public object Comment => throw new Exception("Complex comment must not be read");
    }
    public enum State { InWork, Released }
    public sealed class TypeVersion
    {
        public Guid Guid { get; set; } = Guid.NewGuid();
        public Version VersionNumber { get; set; } = new(1, 2, 3);
        public string Author { get; set; } = "Author";
        public DateTime ModifiedDate => new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        public State State => State.Released;
        public object TypeObject => throw new Exception("TypeObject must not be read");
        public object Dependencies => throw new Exception("Dependencies must not be read");
        public object Dependents => throw new Exception("Dependents must not be read");
        public object MasterCopiesContainingInstances => throw new Exception("Instances must not be read");
    }
    public sealed class Copy
    {
        public string Name { get; set; } = "Station template";
        public string Author => "Author";
        public DateTime CreationDate => new(2020, 1, 1);
        public Content[] ContentDescriptions { get; set; } = Array.Empty<Content>();
    }
    public sealed class Content
    {
        public string ContentName => "Screen prototype";
        public Type ContentType => typeof(Content);
        public object Parent => throw new Exception("Parent must not be read");
    }
    public sealed class BrokenVersion
    {
        public Version VersionNumber => new(1, 0);
        public string Author => throw new InvalidOperationException("Native metadata read failed");
    }
    public sealed class BrokenType
    {
        public string Name => "Broken";
        public object[] Versions => new object[] { new BrokenVersion() };
    }
    public sealed class BrokenTypeFolder { public object[] Folders => Array.Empty<object>(); public object[] Types => new object[] { new BrokenType() }; }
    public sealed class BrokenLibrary { public BrokenTypeFolder TypeFolder => new(); public CopyFolder MasterCopyFolder => new(); }
    private static LibraryCapture Capture(string path = "", int offset = 0, int maxRows = 100) => new() { LibraryId = "project", Path = path, Offset = offset, MaxRows = maxRows };
    private static LibrarySnapshot Browse(object library, string path = "", int offset = 0, int maxRows = 100)
        => new LibraryCatalogReader().Capture(library, Array.Empty<object>(), Capture(path, offset, maxRows));
    private static object Request(object? capture = null, object? options = null) => new {
        expectedProjectPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synthetic.ap15_1"), capture = capture ?? new { libraryId = "project", path = "types" }, options = options ?? new { } };

    [Fact] public void ListsOnlySuppliedOpenLibrariesAndUsesStablePathIdsWithoutOpeningFiles()
    {
        var global = new GlobalLibrary();
        var snapshot = new LibraryCatalogReader().Capture(new Library(), new[] { global }, new LibraryCapture());
        Assert.Equal(new[] { "projectLibrary", "globalLibrary" }, snapshot.Rows.Select(r => r.Kind));
        Assert.Equal("project", snapshot.Rows[0].LibraryId);
        Assert.Equal(LibraryCatalogReader.GlobalId(global.Path.FullName.ToUpperInvariant()), snapshot.Rows[1].LibraryId);
        Assert.Equal(global.Path.FullName, snapshot.Rows[1].Fields["Path"]);
        Assert.False(snapshot.HasMore); Assert.False(global.Path.Exists);
    }
    [Fact] public void ProjectBrowsingNeverEnumeratesGlobalLibraries()
    {
        var unavailable = Enumerable.Range(0, 1).Select<int, object>(_ => throw new Exception("Globals accessed"));
        var snapshot = new LibraryCatalogReader().Capture(new Library(), unavailable, Capture());
        Assert.Equal(new[] { "types", "masterCopies" }, snapshot.Rows.Select(r => r.Path));
    }
    [Fact] public void BrowseTypesVersionsAndCopiesReadsOnlyDocumentedMetadata()
    {
        var version = new TypeVersion(); var type = new LibraryType { Versions = new[] { version } };
        var library = new Library { TypeFolder = new TypeFolder { Folders = new[] { new TypeFolder { Name = "Über/Station", Types = new[] { type } } } },
            MasterCopyFolder = new CopyFolder { MasterCopies = new[] { new Copy { ContentDescriptions = new[] { new Content() } } } } };
        Assert.Equal("types/f:%C3%9Cber%2FStation", Assert.Single(Browse(library, "types").Rows).Path);
        var entry = Assert.Single(Browse(library, "types/f:%C3%9Cber%2FStation").Rows);
        Assert.Equal(type.Guid.ToString("D"), entry.Fields["Guid"]); Assert.Contains("Namespace", entry.UnsupportedFields);
        string path = entry.Path + "/v:1.2.3";
        var result = Browse(library, entry.Path); Assert.Equal(path, Assert.Single(result.Rows).Path);
        Assert.Equal("Released", result.Rows[0].Fields["State"]); Assert.Contains("IsDefault", result.Rows[0].UnsupportedFields);
        var leaf = Browse(library, path); Assert.Empty(leaf.Rows); Assert.False(leaf.Selected!.CanBrowse);
        Assert.Equal(version.Guid.ToString("D"), leaf.Selected.Fields["Guid"]);
        var copy = Assert.Single(Browse(library, "masterCopies").Rows);
        var content = Assert.Single(Browse(library, copy.Path).Rows);
        Assert.Equal(typeof(Content).FullName, content.Fields["ContentType"]);
        Assert.Equal("content", Browse(library, content.Path).Selected!.Kind);
    }
    [Fact] public void PagingIsExplicitAndDoesNotReadMetadataPastThePage()
    {
        var library = new Library { TypeFolder = new TypeFolder { Types = Enumerable.Range(0, 5).Select(i => new LibraryType { Name = "T" + i }).ToArray() } };
        var reader = new LibraryCatalogReader();
        var first = reader.Capture(library, Array.Empty<object>(), Capture("types", maxRows: 2));
        Assert.Equal(new[] { "T0", "T1" }, first.Rows.Select(r => r.Name)); Assert.Equal(2, first.NextOffset); Assert.True(first.HasMore);
        var second = reader.Capture(library, Array.Empty<object>(), Capture("types", first.NextOffset!.Value, 2));
        Assert.Equal(new[] { "T2", "T3" }, second.Rows.Select(r => r.Name));
        var last = reader.Capture(library, Array.Empty<object>(), Capture("types", 4, 2));
        Assert.Single(last.Rows); Assert.False(last.HasMore); Assert.Null(last.NextOffset);
    }
    [Fact] public void MissingAndAmbiguousIdentitiesFailWithoutAnEmptyResultClaim()
    {
        var global = new GlobalLibrary(); var reader = new LibraryCatalogReader();
        string id = LibraryCatalogReader.GlobalId(global.Path.FullName);
        Assert.Equal("globalLibrary", reader.Capture(new Library(), new[] { global }, new LibraryCapture { LibraryId = id }).Selected!.Kind);
        Assert.Throws<InvalidOperationException>(() => reader.Capture(new Library(), Array.Empty<object>(), new LibraryCapture { LibraryId = id }));
        Assert.Throws<InvalidOperationException>(() => reader.Capture(new Library(), new[] { global, global }, new LibraryCapture { LibraryId = id }));
        var library = new Library { TypeFolder = new TypeFolder { Types = new[] { new LibraryType(), new LibraryType() } } };
        Assert.Throws<InvalidOperationException>(() => Browse(library, "types"));
        Assert.Throws<InvalidOperationException>(() => Browse(library, "types/t:Counter"));
        Assert.Throws<InvalidOperationException>(() => Browse(new Library(), "types/t:Missing"));
    }
    [Theory]
    [InlineData("Parent")][InlineData("types/../Parent")][InlineData("types/p:TypeObject")]
    [InlineData("types/t:Counter/Dependencies")][InlineData("types/t:Counter/v:1.0/Parent")]
    [InlineData("masterCopies/m:Copy/c:00")][InlineData("masterCopies/m:Copy/c:-1")]
    [InlineData("types/f:Bad%2fname")][InlineData("types/f:Bad%")][InlineData("types//t:Counter")]
    public void UnsupportedNavigationAndNoncanonicalPathsAreRejected(string path)
        => Assert.Throws<ArgumentException>(() => Capture(path).Validate());
    [Fact] public void NestingScanPayloadAndFieldBoundsAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => Capture("types/" + string.Join("/", Enumerable.Repeat("f:F", 33))).Validate());
        var library = new Library { TypeFolder = new TypeFolder { Types = Enumerable.Range(0, 20001).Select(i => new LibraryType { Name = "T" + i }).ToArray() } };
        Assert.Throws<InvalidOperationException>(() => Browse(library, "types/t:T0"));
        library.TypeFolder.Types = Enumerable.Range(0, 200).Select(i => new LibraryType { Name = "T" + i, Author = new string('x', 2047) + "😀tail" }).ToArray();
        var page = Browse(library, "types", maxRows: 200);
        Assert.True(page.PayloadLimited); Assert.True(page.HasMore); Assert.InRange(page.Rows.Length, 1, 199);
        Assert.All(page.Rows, row => { Assert.Contains("Author", row.TruncatedFields); Assert.Equal(2047, row.Fields["Author"]!.Length); });
        library.TypeFolder.Types = Enumerable.Range(0, 10002).Select(i => new LibraryType { Name = "T" + i }).ToArray();
        var limit = Browse(library, "types", offset: 10000, maxRows: 1);
        Assert.True(limit.HasMore); Assert.True(limit.OffsetLimitReached); Assert.Null(limit.NextOffset);
    }
    [Fact] public void NativeMetadataFailuresPropagate()
        => Assert.Throws<TargetInvocationException>(() => Browse(new BrokenLibrary(), "types/t:Broken"));

    private sealed class Backend : ILibraryInspectionBackend
    {
        public int Calls;
        public LibrarySnapshot CaptureLibraries(string expectedProjectPath, LibraryCapture capture)
        { Calls++; return new LibrarySnapshot { LibraryId = capture.LibraryId, Path = capture.Path, Rows = new[] { new LibraryRow { Name = "Über 😀" } } }; }
    }
    [Fact] public void WorkflowValidatesBeforeNativeCaptureAndWorkersReceiveOnlySnapshots()
    {
        var backend = new Backend(); var workflow = new LibraryInspectionWorkflow(backend, new LocalFeatureEngine());
        var result = ExtensionWire.Json(workflow.Read("browse_library", "libraryCatalog", ExtensionWire.Json(Request())));
        Assert.Equal("Über 😀", result.GetProperty("data").GetProperty("rows")[0].GetProperty("name").GetString());
        Assert.False(result.GetProperty("librariesOpened").GetBoolean()); Assert.Equal(1, backend.Calls);
        Assert.Throws<ArgumentException>(() => workflow.Read("list_libraries", "libraryCatalog", ExtensionWire.Json(Request())));
        Assert.Throws<ArgumentException>(() => workflow.Read("browse_library", "libraryCatalog", ExtensionWire.Json(Request(options: new { open = true }))));
        Assert.Throws<NotSupportedException>(() => workflow.Read("browse_library", "source", ExtensionWire.Json(Request())));
        Assert.Throws<JsonException>(() => workflow.Read("browse_library", "libraryCatalog", ExtensionWire.Json(new { expectedProjectPath = "C:/synthetic.ap19", capture = new { libraryId = "project", path = "" }, options = new { }, snapshot = new { injected = true } })));
        Assert.Equal(1, backend.Calls);
    }
    [Fact] public void ProjectSnapshotRouteCannotBeUsedAsAWriteOrByAnotherModule()
    {
        var operation = FeatureOperations.Describe("project").First();
        Assert.True(operation.IsCompatible("project")); Assert.False(operation.IsCompatible("plc"));
        operation.Preview = true; Assert.False(operation.IsCompatible("project"));
        operation.Preview = false; operation.SnapshotKind = "source"; Assert.False(operation.IsCompatible("project"));
    }
}
