using System.Buffers.Binary;
using System.Xml.Linq;
using Dictum.Testing;

namespace Dictum.Core.Tests;

public class PackagingTests
{
    private static string Icons => Path.Combine(Repository.Root, "packaging", "icons");

    private static string[] PkgbuildLines => File.ReadAllLines(Path.Combine(Repository.Root, "packaging", "linux", "aur", "PKGBUILD"));

    /// <summary>The PKGBUILD's lines without their comments.</summary>
    private static IEnumerable<string> PkgbuildCode => PkgbuildLines.Select(line => line.Split('#')[0]);

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

    // The repository lives in the Sigil-build organization; the package's page and its download come from there.
    [Fact]
    public void PkgbuildPointsAtTheOrganization()
    {
        Assert.Contains("url='https://github.com/Sigil-build/dictum'", PkgbuildCode);

        var source = Assert.Single(PkgbuildCode, line => line.StartsWith("source=", StringComparison.Ordinal));
        Assert.StartsWith("source=(\"https://github.com/Sigil-build/dictum/releases/download/v${pkgver}/", source);
    }

    // The release workflow rewrites pkgver from the tag; the checked-in PKGBUILD, which makepkg uses on a local
    // tarball, names the version the packages are built with.
    [Fact]
    public void ThePkgbuildNamesTheVersionThePackagesAreBuiltWith()
    {
        var props = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"));
        var version = props.Descendants("VersionPrefix").Single().Value;

        Assert.Contains($"pkgver={version}", PkgbuildLines);
    }

    // Arch lists every license that applies to a package: Dictum's, and those of the libraries it ships, among them
    // libuiohook (LGPL), Skia, HarfBuzz and the code Skia and Avalonia include, as THIRD-PARTY-NOTICES.txt has them.
    [Fact]
    public void ThePkgbuildListsDictumsLicenseAndThoseOfTheLibrariesItShips()
    {
        HashSet<string> shipped =
        [
            "MIT", "LGPL-3.0-or-later", "BSD-3-Clause", "MIT-Modern-Variant", "Apache-2.0", "MS-PL",
            "FTL OR GPL-2.0-or-later", "libpng-2.0", "IJG", "Zlib", "LicenseRef-Adobe-DNG-SDK",
        ];

        Assert.Superset(shipped, PkgbuildLicenses());
    }

    // An MIT license names its copyright holder and the notices carry the third-party ones, so Arch wants both files
    // in /usr/share/licenses/<pkgname>/. test-pkgbuild.sh installs the package for real, but only where Docker runs.
    [Theory]
    [InlineData("LICENSE")]
    [InlineData("THIRD-PARTY-NOTICES.txt")]
    public void ThePkgbuildInstallsTheFileIntoItsLicensesFolder(string file)
    {
        var licenseInstalls = PkgbuildCode
            .Select(Words)
            .Where(words => words is ["install", ..] && words.Any(w => w.Contains("/usr/share/licenses/$pkgname", StringComparison.Ordinal)));

        Assert.Contains(licenseInstalls, words => words.Any(w => w.EndsWith($"/{file}", StringComparison.Ordinal)));
    }

    /// <summary>The entries of the PKGBUILD's license array, which can span lines.</summary>
    private static HashSet<string> PkgbuildLicenses()
    {
        var code = string.Join('\n', PkgbuildCode);
        var start = code.IndexOf("\nlicense=(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The PKGBUILD has no license array.");
        var body = code[(start + "\nlicense=(".Length)..code.IndexOf(')', start)];

        return [.. body.Split('\'').Where((_, i) => i % 2 == 1)];
    }

    /// <summary>A line's words, with the quotes around them taken off.</summary>
    private static string[] Words(string line) =>
        [.. line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '\''))];
}
