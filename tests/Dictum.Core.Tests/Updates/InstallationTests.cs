using Dictum.Updates;

namespace Dictum.Core.Tests.Updates;

/// <summary>How Dictum was installed, told from facts built by hand: no real registry, folder or environment.</summary>
public sealed class InstallationTests
{
    private const string WindowsHome = @"C:\Users\u";
    private const string MsiFolder = @"C:\Users\u\AppData\Local\Programs\Dictum";
    private const string Home = "/home/u";

    private static InstallFacts Windows(string? process, string? msiFolder) =>
        new(IsWindows: true, IsLinux: false, process, AppImage: null, msiFolder, WindowsHome, CanWrite: _ => true, FileExists: _ => false);

    private static InstallFacts Linux(string? process, string? appImage = null, Func<string, bool>? canWrite = null, Func<string, bool>? exists = null) =>
        new(IsWindows: false, IsLinux: true, process, appImage, MsiInstallFolder: null, Home, canWrite ?? (_ => true), exists ?? (_ => false));

    [Theory]
    [InlineData(MsiFolder + @"\")]
    [InlineData(MsiFolder)]
    [InlineData(@"c:\users\U\AppData\Local\Programs\DICTUM\")]
    public void TheMsiFolderMakesAnMsiInstall(string registered)
    {
        var installation = InstallDetector.Detect(Windows(MsiFolder + @"\dictum-gui.exe", registered));

        Assert.Equal(new Installation(InstallKind.Msi, MsiFolder), installation);
    }

    [Fact]
    public void TheCliInTheMsiFolderIsAnMsiInstallToo() =>
        Assert.Equal(InstallKind.Msi, InstallDetector.Detect(Windows(MsiFolder + @"\dictum.exe", MsiFolder)).Kind);

    [Theory]
    [InlineData(@"D:\Tools\Dictum\dictum-gui.exe", MsiFolder)]
    [InlineData(MsiFolder + @"\sub\dictum-gui.exe", MsiFolder)]
    [InlineData(MsiFolder + @"\dictum-gui.exe", null)]
    public void ADifferentFolderOnWindowsIsPortable(string process, string? registered) =>
        Assert.Equal(new Installation(InstallKind.Portable, null), InstallDetector.Detect(Windows(process, registered)));

    [Fact]
    public void AnAppImageInAWritableFolderUpdatesItself()
    {
        var facts = Linux("/tmp/.mount_DictuXYZ/usr/bin/dictum-gui", "/home/u/Applications/Dictum.AppImage",
            canWrite: folder => folder == "/home/u/Applications");

        Assert.Equal(new Installation(InstallKind.AppImage, "/home/u/Applications/Dictum.AppImage"), InstallDetector.Detect(facts));
    }

    [Fact]
    public void AnAppImageInAReadOnlyFolderIsPortable()
    {
        var facts = Linux("/tmp/.mount_DictuXYZ/usr/bin/dictum-gui", "/opt/apps/Dictum.AppImage", canWrite: _ => false);

        Assert.Equal(new Installation(InstallKind.Portable, null), InstallDetector.Detect(facts));
    }

    [Fact]
    public void TheInstallShFolderIsATarballInstall()
    {
        var facts = Linux("/home/u/.local/lib/dictum/dictum-gui", exists: path => path == "/home/u/.local/lib/dictum/uninstall.sh");

        Assert.Equal(new Installation(InstallKind.Tarball, "/home/u/.local/lib/dictum"), InstallDetector.Detect(facts));
    }

    [Fact]
    public void TheInstallShFolderWithoutUninstallShIsPortable() =>
        Assert.Equal(InstallKind.Portable, InstallDetector.Detect(Linux("/home/u/.local/lib/dictum/dictum-gui")).Kind);

    [Fact]
    public void OptDictumIsThePackageManagers() =>
        Assert.Equal(new Installation(InstallKind.PackageManager, null), InstallDetector.Detect(Linux("/opt/dictum/dictum-gui", canWrite: _ => false)));

    [Fact]
    public void AWritableFolderOutsideHomeIsPortable() =>
        Assert.Equal(InstallKind.Portable, InstallDetector.Detect(Linux("/srv/dictum/dictum-gui", canWrite: _ => true)).Kind);

    [Fact]
    public void AReadOnlyFolderInsideHomeIsPortable() =>
        Assert.Equal(InstallKind.Portable, InstallDetector.Detect(Linux("/home/u/dictum/dictum-gui", canWrite: _ => false)).Kind);

    [Fact]
    public void AnUnpackedTarballInDownloadsIsPortable() =>
        Assert.Equal(InstallKind.Portable, InstallDetector.Detect(Linux("/home/u/Downloads/dictum-0.7.1-linux-x64/dictum-gui")).Kind);

    [Fact]
    public void NoProcessPathIsPortable()
    {
        Assert.Equal(InstallKind.Portable, InstallDetector.Detect(Linux(null)).Kind);
        Assert.Equal(InstallKind.Portable, InstallDetector.Detect(Windows(null, MsiFolder)).Kind);
    }

    [Fact]
    public void NeitherWindowsNorLinuxIsPortable()
    {
        var facts = new InstallFacts(false, false, "/Applications/Dictum/dictum-gui", null, null, "/Users/u", _ => false, _ => true);

        Assert.Equal(new Installation(InstallKind.Portable, null), InstallDetector.Detect(facts));
    }
}
