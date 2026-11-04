using System.Runtime.InteropServices;
using Dictum.Plugins;

namespace Dictum.Core.Tests.Plugins;

public sealed class PluginProtocolTests
{
    [Theory]
    [InlineData("win-x64", "windows", Architecture.X64, "win-x64")]
    [InlineData("win10-x64", "windows", Architecture.X64, "win-x64")]
    [InlineData("win-arm64", "windows", Architecture.Arm64, "win-arm64")]
    [InlineData("linux-x64", "linux", Architecture.X64, "linux-x64")]
    [InlineData("linux-musl-x64", "linux", Architecture.X64, "linux-musl-x64")]
    [InlineData("ubuntu.24.04-x64", "linux", Architecture.X64, "linux-x64")]
    [InlineData("debian.12-arm64", "linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("fedora.41-x64", "linux", Architecture.X64, "linux-x64")]
    [InlineData("osx-arm64", "osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("osx.14-arm64", "osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("osx.14-x64", "osx", Architecture.X64, "osx-x64")]
    [InlineData("freebsd.14-x64", "freebsd", Architecture.X64, "freebsd.14-x64")]
    public void ASystemsRidFallsBackToThePortableOne(string runtimeRid, string os, Architecture architecture, string expected) =>
        Assert.Equal(expected, PluginProtocol.PortableRid(runtimeRid, OSPlatform.Create(os), architecture));

    [Fact]
    public void ThisSystemsRidIsPortable() =>
        Assert.Matches("^(win|linux|osx)(-musl)?-(x64|x86|arm64|arm)$", PluginProtocol.CurrentRid);
}
