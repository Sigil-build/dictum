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
}
