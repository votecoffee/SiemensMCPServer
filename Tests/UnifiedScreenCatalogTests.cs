using System;
using System.Linq;
using System.Reflection;
using SiemensMCPServer;
using Xunit;

public sealed class UnifiedScreenCatalogTests
{
    public sealed class Screen { public string Name { get; set; } = ""; }
    public class FlatSoftware { public Screen[] Screens { get; set; } = Array.Empty<Screen>(); }
    public sealed class GroupedSoftware : FlatSoftware { public Group[] ScreenGroups { get; set; } = Array.Empty<Group>(); }
    public sealed class Group : FlatSoftware
    {
        public string Name { get; set; } = "";
        public Group[] Groups { get; set; } = Array.Empty<Group>();
    }
    public sealed class BrokenGroups : FlatSoftware { public Group[] ScreenGroups => throw new InvalidOperationException("Native group read failed"); }

    [Fact] public void FlatApiPreservesNativeScreenIdentityAndEscapesPaths()
    {
        var screen = new Screen { Name = "Über / Main" };
        var catalog = UnifiedScreenCatalog.Read(new FlatSoftware { Screens = new[] { screen } });
        Assert.Equal("screens/%C3%9Cber%20%2F%20Main", Assert.Single(catalog).Path);
        Assert.Same(screen, catalog[0].Native);
    }
    [Fact] public void GroupedApiRetainsExistingRootAndNestedPaths()
    {
        var software = new GroupedSoftware { Screens = new[] { new Screen { Name = "Root" } },
            ScreenGroups = new[] { new Group { Name = "Area/1", Screens = new[] { new Screen { Name = "Main" } },
                Groups = new[] { new Group { Name = "Child", Screens = new[] { new Screen { Name = "Main" } } } } } } };
        Assert.Equal(new[] { "screens/Root", "groups/Area%2F1/screens/Main", "groups/Area%2F1/groups/Child/screens/Main" },
            UnifiedScreenCatalog.Read(software).Select(s => s.Path));
    }
    [Fact] public void UnavailableOrFailedGroupsNeverMasqueradeAsAFlatApi()
    {
        Assert.Throws<NotSupportedException>(() => UnifiedScreenCatalog.Read(new GroupedSoftware { ScreenGroups = null! }));
        Assert.Throws<TargetInvocationException>(() => UnifiedScreenCatalog.Read(new BrokenGroups()));
        Assert.Throws<NotSupportedException>(() => UnifiedScreenCatalog.Read(new FlatSoftware { Screens = null! }));
    }
    [Fact] public void DuplicateScreenPathsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => UnifiedScreenCatalog.Read(new FlatSoftware {
            Screens = new[] { new Screen { Name = "Main" }, new Screen { Name = "Main" } } }));
    }
    [Fact] public void CyclicAndOversizedCatalogsAreBounded()
    {
        var group = new Group { Name = "Loop" }; group.Groups = new[] { group };
        Assert.Throws<InvalidOperationException>(() => UnifiedScreenCatalog.Read(new GroupedSoftware { ScreenGroups = new[] { group } }));
        Assert.Throws<InvalidOperationException>(() => UnifiedScreenCatalog.Read(new FlatSoftware {
            Screens = Enumerable.Range(0, 20001).Select(i => new Screen { Name = i.ToString() }).ToArray() }));
        Assert.Throws<InvalidOperationException>(() => UnifiedScreenCatalog.Read(new GroupedSoftware {
            ScreenGroups = Enumerable.Range(0, 20001).Select(i => new Group { Name = i.ToString() }).ToArray() }));
    }
}
