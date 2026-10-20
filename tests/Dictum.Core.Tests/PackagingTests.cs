using System.Buffers.Binary;
using System.Xml.Linq;
using Dictum.Testing;

namespace Dictum.Core.Tests;

public class PackagingTests
{
    private static string Icons => Path.Combine(Repository.Root, "packaging", "icons");

    private static string[] PkgbuildLines => File.ReadAllLines(Path.Combine(Repository.Root, "packaging", "linux", "aur", "PKGBUILD"));

    // Every size Windows picks from (16 for small icons up to 256 for large tiles), so none is scaled.
    [Fact]
    public void TheIconHasEverySizeWindowsUses()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Icons, "dictum.ico"));
        Assert.Equal((0, 1), (BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))));

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + (16 * i)] is 0 ? 256 : bytes[6 + (16 * i)]).Order();

        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 256], sizes);
    }

    [Theory]
    [InlineData("dictum-256.png", 256)]
    [InlineData("dictum-512.png", 512)]
    public void ThePngIconsHaveTheirSize(string file, int size)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Icons, file));

        Assert.Equal(
            (size, size),
            ((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))));
    }

    [Fact]
    public void TheScalableIconIsAnSvg() =>
        Assert.StartsWith("<svg", File.ReadAllText(Path.Combine(Icons, "dictum.svg")).TrimStart());

    // The release workflow rewrites pkgver from the tag; the checked-in PKGBUILD, which makepkg uses on a local
    // tarball, names the version the packages are built with.
    [Fact]
    public void ThePkgbuildNamesTheVersionThePackagesAreBuiltWith()
    {
        var props = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"));
        var version = props.Descendants("VersionPrefix").Single().Value;

        Assert.Contains($"pkgver={version}", PkgbuildLines);
    }

    // An MIT license names its copyright holder, so Arch wants the file itself in /usr/share/licenses/<pkgname>/.
    // test-pkgbuild.sh installs the package for real, but only where Docker runs.
    [Fact]
    public void ThePkgbuildIsMitAndInstallsTheLicenseAndTheNotices()
    {
        Assert.Contains("license=('MIT')", PkgbuildLines);
        Assert.Contains(
            "  install -Dm644 -t \"$pkgdir/usr/share/licenses/$pkgname\" \"$src/LICENSE\" \"$src/THIRD-PARTY-NOTICES.txt\"",
            PkgbuildLines);
    }
}
