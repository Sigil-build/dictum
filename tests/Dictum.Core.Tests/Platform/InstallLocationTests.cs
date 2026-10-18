using Dictum.Platform;

namespace Dictum.Core.Tests.Platform;

public class InstallLocationTests
{
    private const string AppImage = "/home/me/Apps/dictum-0.3.0-linux-x64.AppImage";

    // Review focus 1: the AppImage mounts under a new /tmp/.mount_* path every run; only the file itself lasts.
    [Fact]
    public void InsideAnAppImageTheDesktopAppIsTheAppImage() =>
        Assert.Equal(new LoginItem(LoginApp.DesktopApp, AppImage), InstallLocation.DesktopApp("/tmp/.mount_x/usr/bin/dictum-gui", AppImage));

    // Review focus 1: AppRun starts the CLI when the first argument is "dictum".
    [Fact]
    public void InsideAnAppImageTheDaemonRunsThroughIt()
    {
        var item = InstallLocation.Daemon("/tmp/.mount_x/usr/bin/dictum", AppImage);

        Assert.Equal((AppImage, true), (item.Executable, item.AppImage));
        Assert.Equal(["dictum", "daemon"], item.Arguments);
        Assert.Equal($"\"{AppImage}\" dictum daemon", item.CommandLine);
    }

    // Review focus 2.
    [Fact]
    public void OutsideAnAppImageTheRunningFilesAreUsed()
    {
        Assert.Equal(new LoginItem(LoginApp.DesktopApp, "/opt/dictum/dictum-gui"), InstallLocation.DesktopApp("/opt/dictum/dictum-gui", null));
        Assert.Equal(new LoginItem(LoginApp.Daemon, "/opt/dictum/dictum"), InstallLocation.Daemon("/opt/dictum/dictum", null));
    }

    // Final review I1: a terminal inside another AppImage (an editor) passes its APPIMAGE and APPDIR on to whatever it
    // starts; Dictum installed from the tar.gz must not record that program. Only an AppImage Dictum runs from counts.
    [Theory]
    [InlineData("/home/me/Apps/dictum.AppImage", "/tmp/.mount_dict1", "/tmp/.mount_dict1/usr/bin/dictum", "/home/me/Apps/dictum.AppImage")]
    [InlineData("/home/me/Apps/Cursor.AppImage", "/tmp/.mount_curs1", "/home/me/.local/lib/dictum/dictum", null)]
    [InlineData("/home/me/Apps/Cursor.AppImage", null, "/home/me/.local/lib/dictum/dictum", null)]
    [InlineData(null, null, "/opt/dictum/dictum", null)]
    [InlineData("/home/me/Apps/dictum.AppImage", "/tmp/.mount_dict1", "/tmp/.mount_dict10/usr/bin/dictum", null)]
    public void OnlyTheAppImageDictumRunsFromCounts(string? appImage, string? appDir, string processPath, string? expected) =>
        Assert.Equal(expected, InstallLocation.AppImageOf(appImage, appDir, processPath));

    // Final review I2: what outlives this run decides init's warning about a temporary folder, not the AppImage's mount.
    [Fact]
    public void TheLastingPathIsTheAppImageInsideOne()
    {
        Assert.Equal(AppImage, InstallLocation.Lasting("/tmp/.mount_x/usr/bin/dictum", AppImage));
        Assert.Equal("/opt/dictum/dictum", InstallLocation.Lasting("/opt/dictum/dictum", null));
    }
}
