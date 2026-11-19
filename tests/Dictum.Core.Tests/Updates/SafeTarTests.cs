using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Core.Tests.Updates;

/// <summary>The tarball's extraction: only what lies under its own root comes out.</summary>
public sealed class SafeTarTests : IDisposable
{
    private const string Root = "dictum-0.7.1-linux-x64";

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Folder => Directory.CreateDirectory(_dir.File(Path.Combine("updates", "0.7.1"))).FullName;

    private static PaxTarEntry Dir(string name) => new(TarEntryType.Directory, name) { Mode = Executable };

    private static PaxTarEntry Regular(string name, string text, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)), Mode = mode };

    private static PaxTarEntry Link(TarEntryType type, string name, string target) => new(type, name) { LinkName = target };

    private string Tarball(params TarEntry[] entries)
    {
        var path = _dir.File("dictum.tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var entry in entries)
            {
                tar.WriteEntry(entry);
            }
        }

        return path;
    }

    private void Refused(string tarball)
    {
        var ex = Assert.Throws<UpdateException>(() => SafeTar.Extract(tarball, Folder, Root));

        Assert.Equal(UpdateFailure.NotVerified, ex.Failure);
        Assert.False(Directory.Exists(Path.Combine(Folder, Root)), "a refused tarball left its root behind");
    }

    [Fact]
    public void ATarballWithItsRootExtracts()
    {
        var tarball = Tarball(
            Dir(Root + "/"),
            Regular(Root + "/install.sh", "#!/bin/sh\n", Executable),
            Regular(Root + "/share/dictum.desktop", "[Desktop Entry]\n"));

        var root = SafeTar.Extract(tarball, Folder, Root);

        Assert.Equal(Path.Combine(Folder, Root), root);
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(root, "install.sh")));
        Assert.Equal("[Desktop Entry]\n", File.ReadAllText(Path.Combine(root, "share", "dictum.desktop")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Executable, File.GetUnixFileMode(Path.Combine(root, "install.sh")));
        }
    }

    [Fact]
    public void AnEarlierExtractionIsReplaced()
    {
        var stale = Path.Combine(Folder, Root, "stale.so");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "old");

        SafeTar.Extract(Tarball(Regular(Root + "/install.sh", "new")), Folder, Root);

        Assert.False(File.Exists(stale));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc/x")]
    [InlineData("other-root/x")]
    [InlineData(Root + "/../../x")]
    [InlineData(Root + "/../" + Root + "2/x")]
    public void EntriesThatLeaveTheFolderAreRefused(string name)
    {
        Refused(Tarball(Regular(Root + "/install.sh", "ok"), Regular(name, "bad")));

        Assert.False(File.Exists(_dir.File("x")));
        Assert.False(File.Exists(Path.Combine(_dir.File("updates"), "x")));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("/etc/passwd")]
    [InlineData("share/../../outside")]
    public void ASymlinkOutOfTheRootIsRefused(string target) =>
        Refused(Tarball(Regular(Root + "/install.sh", "ok"), Link(TarEntryType.SymbolicLink, Root + "/lib", target)));

    [Fact]
    public void ASymlinkInsideTheRootIsKept()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("symbolic links need a privilege on Windows");
            return;
        }

        var root = SafeTar.Extract(Tarball(
            Regular(Root + "/share/dictum.svg", "svg"),
            Link(TarEntryType.SymbolicLink, Root + "/icon.svg", "share/dictum.svg")), Folder, Root);

        Assert.Equal("svg", File.ReadAllText(Path.Combine(root, "icon.svg")));
    }

    [Fact]
    public void AHardLinkIsRefused() =>
        Refused(Tarball(Regular(Root + "/install.sh", "ok"), Link(TarEntryType.HardLink, Root + "/copy", Root + "/install.sh")));

    [Theory]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    public void ADeviceOrAFifoIsRefused(TarEntryType type) =>
        Refused(Tarball(Regular(Root + "/install.sh", "ok"), new PaxTarEntry(type, Root + "/odd")));

    [Fact]
    public void ATarballThatIsNotOneIsRefused()
    {
        var path = _dir.File("dictum.tar.gz");
        File.WriteAllText(path, "not gzip");

        Refused(path);
    }
}
