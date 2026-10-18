using Dictum.Platform;

namespace Dictum.Core.Tests.Platform;

public class LoginItemTests
{
    [Fact]
    public void TheDesktopAppNextToDictumIsPreferred()
    {
        using var dir = new TempDirectory();
        var dictum = dir.File(OperatingSystem.IsWindows() ? "dictum.exe" : "dictum");
        File.WriteAllText(dictum, "");
        File.WriteAllText(dir.File(LoginItem.DesktopAppFile), "");

        Assert.Equal(new LoginItem(LoginApp.DesktopApp, dir.File(LoginItem.DesktopAppFile)), LoginItem.NextTo(dictum));
    }

    [Fact]
    public void WithoutTheDesktopAppTheDaemonStartsAlone()
    {
        using var dir = new TempDirectory();
        var dictum = dir.File(OperatingSystem.IsWindows() ? "dictum.exe" : "dictum");

        var item = LoginItem.NextTo(dictum);

        Assert.Equal(new LoginItem(LoginApp.Daemon, dictum), item);
        Assert.Equal(["daemon"], item.Arguments);
    }

    // Review focus 1: inside an AppImage the desktop app is always bundled, and the AppImage is what lasts.
    [Fact]
    public void InsideAnAppImageInitStartsTheAppImage() =>
        Assert.Equal(
            new LoginItem(LoginApp.DesktopApp, "/home/me/Apps/dictum.AppImage"),
            LoginItem.NextTo("/tmp/.mount_x/usr/bin/dictum", "/home/me/Apps/dictum.AppImage"));

    // Review focus 2: the Run key text stays as it was.
    [Fact]
    public void TheCommandLineQuotesTheExecutable()
    {
        Assert.Equal("\"C:\\Tools\\Dictum\\dictum.exe\" daemon", new LoginItem(LoginApp.Daemon, @"C:\Tools\Dictum\dictum.exe").CommandLine);
        Assert.Equal("\"C:\\Tools\\Dictum\\dictum-gui.exe\"", new LoginItem(LoginApp.DesktopApp, @"C:\Tools\Dictum\dictum-gui.exe").CommandLine);
    }
}
