using Dictum.Updates;

namespace Dictum.Core.Tests.Updates;

public sealed class ReleaseFilesTests
{
    private static readonly ReleaseVersion Version = new(0, 7, 1);

    [Theory]
    [InlineData(InstallKind.Msi, "dictum-0.7.1-win-x64.msi")]
    [InlineData(InstallKind.AppImage, "dictum-0.7.1-linux-x64.AppImage")]
    [InlineData(InstallKind.Tarball, "dictum-0.7.1-linux-x64.tar.gz")]
    [InlineData(InstallKind.Portable, null)]
    [InlineData(InstallKind.PackageManager, null)]
    public void EachKindDownloadsItsOwnPackage(InstallKind kind, string? name) => Assert.Equal(name, ReleaseFiles.PackageName(kind, Version));

    [Fact]
    public void TheAttestationIsNamedForTheVersion() => Assert.Equal("dictum-0.7.1.intoto.jsonl", ReleaseFiles.AttestationName(Version));

    [Fact]
    public void DownloadsComeFromTheTagsReleaseFiles() =>
        Assert.Equal(new Uri("https://github.com/Sigil-build/dictum/releases/download/v0.7.1/x"), ReleaseFiles.DownloadUrl(Version, "x"));

    [Fact]
    public void ThePkgbuildIsAReleaseFile() =>
        Assert.Equal(new Uri("https://github.com/Sigil-build/dictum/releases/download/v0.7.1/PKGBUILD"), ReleaseFiles.PkgbuildUrl(Version));
}
