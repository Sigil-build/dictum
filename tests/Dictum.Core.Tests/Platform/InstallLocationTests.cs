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
}
