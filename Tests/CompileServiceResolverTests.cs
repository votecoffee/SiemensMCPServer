using System;
using System.Collections.Generic;
using System.Linq;
using SiemensMCPServer;
using Xunit;

public sealed class CompileServiceResolverTests
{
    private sealed class Compiler { public void Compile() => throw new InvalidOperationException("Native compile failed"); }
    private readonly object software = new();
    private const string Path = "devices/Panel/Runtime";
    private static CompileScope[] Owners() => new[] {
        new CompileScope(new object(), "deviceItem", Path), new CompileScope(new object(), "device", "devices/Panel") };

    [Fact] public void SoftwareCompilerWinsWithoutReadingOwners()
    {
        var expected = new Compiler();
        var chosen = CompileServiceResolver.Resolve(software, Path, true, _ => expected, () => throw new Exception("Owners accessed"));
        Assert.Same(expected, chosen.Service); Assert.Same(software, chosen.Scope.Target); Assert.False(chosen.OwnerFallback);
    }
    [Fact] public void ClassicAndPlcSoftwareDoNotUseOwnerFallback()
    {
        int probes = 0;
        Assert.Throws<NotSupportedException>(() => CompileServiceResolver.Resolve<Compiler>(software, Path, false,
            _ => { probes++; return null!; }, () => throw new Exception("Owners accessed")));
        Assert.Equal(1, probes);
    }
    [Fact] public void NearestOwnerWinsAndCompileFailureNeverProbesBroaderScopes()
    {
        var owners = Owners(); var probes = new List<object>(); var compiler = new Compiler();
        var chosen = CompileServiceResolver.Resolve(software, Path, true, node => {
            probes.Add(node); return ReferenceEquals(node, owners[0].Target) ? compiler : null!;
        }, () => owners);
        Assert.True(chosen.OwnerFallback); Assert.Equal("deviceItem", chosen.Scope.Kind);
        Assert.Equal(new[] { software, owners[0].Target }, probes);
        Assert.Throws<InvalidOperationException>(() => chosen.Service.Compile());
        Assert.Equal(2, probes.Count);
    }
    [Fact] public void DeviceCompilerIsSelectedOnlyAfterSoftwareAndDeviceItemHaveNoService()
    {
        var owners = Owners(); int probes = 0; var compiler = new Compiler();
        var chosen = CompileServiceResolver.Resolve(software, Path, true, node => {
            probes++; return ReferenceEquals(node, owners[1].Target) ? compiler : null!;
        }, () => owners);
        Assert.Equal("device", chosen.Scope.Kind); Assert.Equal("devices/Panel", chosen.Scope.Path); Assert.Equal(3, probes);
        Assert.Throws<NotSupportedException>(() => CompileServiceResolver.Resolve<Compiler>(software, Path, true, _ => null!, () => owners));
    }
    [Fact] public void ServiceLookupErrorsPropagateWithoutTryingAnotherOwner()
    {
        int probes = 0;
        var failure = Assert.Throws<InvalidOperationException>(() => CompileServiceResolver.Resolve<Compiler>(software, Path, true, node => {
            if (++probes == 2) throw new InvalidOperationException("Lookup failed"); return null!;
        }, Owners));
        Assert.Equal("Lookup failed", failure.Message); Assert.Equal(2, probes);
    }
    [Fact] public void InvalidOwnerChainsAreRejectedBeforeAnyOwnerServiceLookup()
    {
        var owners = Owners();
        var invalid = new[] {
            Array.Empty<CompileScope>(), new[] { owners[0] }, new[] { owners[0], new CompileScope(new object(), "project", "devices") },
            new[] { new CompileScope(new object(), "deviceItem", "devices/Other/Runtime"), owners[1] },
            new[] { owners[0], new CompileScope(new object(), "device", "devices/Other") },
            new[] { owners[0], new CompileScope(owners[0].Target, "device", "devices/Panel") },
            new[] { new CompileScope(software, "deviceItem", Path), owners[1] }, new[] { owners[0], null! },
            Enumerable.Range(0, 10).Select(_ => owners[0]).ToArray()
        };
        foreach (var chain in invalid) {
            int probes = 0;
            Assert.Throws<NotSupportedException>(() => CompileServiceResolver.Resolve<Compiler>(software, Path, true,
                _ => { probes++; return null!; }, () => chain));
            Assert.Equal(1, probes);
        }
    }
}
