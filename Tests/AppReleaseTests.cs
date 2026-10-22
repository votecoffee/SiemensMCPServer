using System;
using System.Reflection;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class AppReleaseTests
{
    [Theory]
    [InlineData("26.0103.000507", "26.103.507")]
    [InlineData("00.0101.000000", "0.101.0")]
    [InlineData("26.1231.235959", "26.1231.235959")]
    [InlineData("0.22.0", "0.22.0")]
    public void PaddedAndCanonicalIdentitiesCompareNumerically(string display, string package)
    {
        Assert.Equal(package, AppRelease.Canonical(display));
        Assert.True(AppRelease.Equivalent(display, package));
        Assert.Equal(0, Version.Parse(display).CompareTo(Version.Parse(package)));
    }
    [Theory]
    [InlineData("26.1022.123456.0")][InlineData("26.-1.1")]
    [InlineData("26.1022.123456\n")][InlineData("26.1022.2147483648")]
    public void InvalidOrOverflowingComparisonIdentityFailsClosed(string value)
        => Assert.ThrowsAny<Exception>(() => AppRelease.Canonical(value));

    [Fact] public void LoaderIdentityIsBoundedAndProductIdentityRetainsTheFullClock()
    {
        var assembly = typeof(AppReleaseTests).Assembly;
        Assert.Equal(AppRelease.AssemblyVersion, assembly.GetName().Version!.ToString());
        Assert.Equal(AppRelease.DisplayVersion, AppRelease.Identity(assembly));
        Assert.Equal(AppRelease.PackageVersion, AppRelease.Canonical(AppRelease.DisplayVersion));
        var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!;
        Assert.Equal(AppRelease.AssemblyVersion, file.Version);
        var version = assembly.GetName().Version!;
        foreach (int component in new[] { version.Major, version.Minor, version.Build, version.Revision }) Assert.InRange(component, 0, 65534);
        Assert.Equal(173616, Version.Parse("26.1022.173616").Build); // valid package patch; invalid assembly field
    }
}
