using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SiemensMCPServer;
using Xunit;

public sealed class ApiCompatibilityTests
{
    private const string Unified = "Siemens.Engineering.HmiUnified.HmiSoftware";
    private const string Graphics = "Siemens.Engineering.Hmi.Globalization.GraphicsProvider";
    private const string Plc = "Siemens.Engineering.SW.PlcSoftware";
    private static AssemblyName Identity(string name, string version = "21.0.0.0", string token = "29bfe5fdf4ba5d3b")
        => new(name + ", Version=" + version + ", Culture=neutral, PublicKeyToken=" + token);
    private sealed class Api : Assembly
    {
        private readonly AssemblyName identity;
        private readonly Dictionary<string, Type> types;
        public Api(AssemblyName identity, params string[] types)
        {
            this.identity = identity; this.types = new();
            foreach (string name in types) this.types[name] = typeof(ApiCompatibilityTests);
        }
        public override AssemblyName GetName() => identity;
        public override Type? GetType(string name, bool throwOnError) => types.TryGetValue(name, out var type) ? type : null;
    }

    [Fact] public void LegacyTypesResolveWithoutProbingProductModules()
    {
        var legacy = new Api(Identity("Siemens.Engineering", "19.0.0.0"), Unified);
        Assert.NotNull(SiemensApiTypes.Resolve(legacy, Unified, _ => throw new Exception("Unexpected load")));
        Assert.Null(SiemensApiTypes.Resolve(legacy, Graphics, _ => throw new Exception("Unexpected load")));
    }
    [Theory]
    [InlineData(Unified, "WinCCUnified")]
    [InlineData(Graphics, "WinCC")]
    [InlineData(Plc, "Step7")]
    public void ModularTypesLoadOnlyTheirExactProductIdentity(string typeName, string product)
    {
        var api = new Api(Identity("Siemens.Engineering.Base")); int loads = 0;
        Assert.NotNull(SiemensApiTypes.Resolve(api, typeName, requested => {
            loads++; Assert.Equal(Identity("Siemens.Engineering." + product).FullName, requested.FullName);
            return new Api(requested, typeName);
        }));
        Assert.Equal(1, loads);
    }
    [Fact] public void OptionalModuleAbsenceReportsUnsupportedAndUnrelatedTypesNeverLoad()
    {
        var api = new Api(Identity("Siemens.Engineering.Base"));
        Assert.Null(SiemensApiTypes.Resolve(api, Unified, _ => null));
        Assert.Null(SiemensApiTypes.Resolve(api, "System.IO.File", _ => throw new Exception("Unexpected load")));
        Assert.Null(SiemensApiTypes.Resolve(api, "Siemens.Engineering.HW.Device", _ => throw new Exception("Unexpected load")));
    }
    [Theory]
    [InlineData("Siemens.Engineering.WinCC", "21.0.0.0", "29bfe5fdf4ba5d3b")]
    [InlineData("Siemens.Engineering.WinCCUnified", "20.0.0.0", "29bfe5fdf4ba5d3b")]
    [InlineData("Siemens.Engineering.WinCCUnified", "21.0.0.0", "d29ec89db764dbb5")]
    public void WrongProductVersionOrSigningKeyIsRejected(string name, string version, string token)
    {
        Assert.Throws<FileLoadException>(() => SiemensApiTypes.Resolve(new Api(Identity("Siemens.Engineering.Base")), Unified,
            _ => new Api(Identity(name, version, token), Unified)));
    }
    [Fact] public void LoaderFailuresRemainVisible()
    {
        Assert.Throws<BadImageFormatException>(() => SiemensApiTypes.Resolve(new Api(Identity("Siemens.Engineering.Base")), Unified,
            _ => throw new BadImageFormatException("Corrupt module")));
    }

    public sealed class LegacyProject { public object Graphics { get; } = new(); }
    public sealed class BrokenLegacyProject { public object Graphics => throw new InvalidOperationException("Native read failed"); }
    public sealed class EmptyLegacyProject { public object? Graphics => null; }
    public sealed class Provider { public object Graphics { get; } = new(); }
    public sealed class BrokenProvider { public object Graphics => throw new InvalidOperationException("Native service failed"); }
    [Fact] public void LegacyGraphicsNeverRequestsServices()
    {
        var project = new LegacyProject();
        Assert.Same(project.Graphics, ProjectGraphicsAccess.Get(project, _ => throw new Exception("Type lookup"), (_, _) => throw new Exception("Service lookup")));
    }
    [Fact] public void ModularGraphicsRequestsOnlyTheDocumentedProjectService()
    {
        object project = new(); var provider = new Provider(); int calls = 0;
        Assert.Same(provider.Graphics, ProjectGraphicsAccess.Get(project, name => {
            Assert.Equal(ProjectGraphicsAccess.ProviderType, name); return typeof(Provider);
        }, (owner, type) => { calls++; Assert.Same(project, owner); Assert.Equal(typeof(Provider), type); return provider; }));
        Assert.Equal(1, calls);
    }
    [Fact] public void LegacyGraphicsFailuresNeverTryAnotherRoute()
    {
        Assert.Throws<TargetInvocationException>(() => ProjectGraphicsAccess.Get(new BrokenLegacyProject(),
            _ => throw new Exception("Unexpected fallback"), (_, _) => throw new Exception("Unexpected fallback")));
        Assert.Throws<NotSupportedException>(() => ProjectGraphicsAccess.Get(new EmptyLegacyProject(),
            _ => throw new Exception("Unexpected fallback"), (_, _) => throw new Exception("Unexpected fallback")));
    }
    [Fact] public void MissingOrInvalidGraphicsServicesFailClearly()
    {
        Assert.Throws<NotSupportedException>(() => ProjectGraphicsAccess.Get(new object(), _ => null, (_, _) => throw new Exception("Unexpected lookup")));
        Assert.Throws<NotSupportedException>(() => ProjectGraphicsAccess.Get(new object(), _ => typeof(Provider), (_, _) => null));
        Assert.Throws<InvalidOperationException>(() => ProjectGraphicsAccess.Get(new object(), _ => typeof(Provider), (_, _) => new object()));
        Assert.Throws<InvalidOperationException>(() => ProjectGraphicsAccess.Get(new object(), _ => typeof(Provider), (_, _) => throw new InvalidOperationException("Lookup failed")));
        Assert.Throws<TargetInvocationException>(() => ProjectGraphicsAccess.Get(new object(), _ => typeof(BrokenProvider), (_, _) => new BrokenProvider()));
    }
}
