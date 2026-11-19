using Dictum.Core;
using Dictum.Host.Updates;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Host.Tests;

/// <summary>The real service's parts: one installer per kind Dictum updates, none for the others.</summary>
public sealed class UpdatesFactoryTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(InstallKind.Msi, typeof(MsiInstaller))]
    [InlineData(InstallKind.AppImage, typeof(AppImageInstaller))]
    [InlineData(InstallKind.Tarball, typeof(TarballInstaller))]
    [InlineData(InstallKind.PackageManager, null)]
    [InlineData(InstallKind.Portable, null)]
    public void EachKindGetsItsInstaller(InstallKind kind, Type? expected)
    {
        var installer = UpdatesFactory.Installer(kind, new PendingUpdateFile(_dir.Path, new ListLog()));

        Assert.Equal(expected, installer?.GetType());
        Assert.True(installer is null || installer.Kind == kind);
    }

    [Fact]
    public void TheServiceKnowsThisBuildsVersionAndInstallation()
    {
        var service = UpdatesFactory.Create(new AppPaths(_dir.File("config"), _dir.File("data")), "0.6.0+abc123", new ListLog());

        Assert.Equal(new ReleaseVersion(0, 6, 0), service.Current);
        Assert.NotNull(service.Installation);
    }
}
