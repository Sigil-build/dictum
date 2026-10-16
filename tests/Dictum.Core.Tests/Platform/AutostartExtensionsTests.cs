using Dictum.Platform;

namespace Dictum.Core.Tests.Platform;

public class AutostartExtensionsTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TurningOnRewritesAnEntryAnOlderCopyInstalled()
    {
        var autostart = new FakeAutostart { Item = new LoginItem(LoginApp.Daemon, @"D:\Tools\dictum-old\dictum.exe") };

        Assert.True(await autostart.ApplyAsync(enable: true, new LoginItem(LoginApp.DesktopApp, @"D:\Tools\dictum-new\dictum-gui.exe"), _ct));

        Assert.Equal(new LoginItem(LoginApp.DesktopApp, @"D:\Tools\dictum-new\dictum-gui.exe"), autostart.Item);
    }

    [Fact]
    public async Task TurningOffRemovesOnlyAnEntryThatExists()
    {
        var installed = new FakeAutostart { Item = new LoginItem(LoginApp.Daemon, "dictum") };
        var absent = new FakeAutostart();
        var any = new LoginItem(LoginApp.Daemon, "dictum");

        Assert.True(await installed.ApplyAsync(enable: false, any, _ct));
        Assert.False(await absent.ApplyAsync(enable: false, any, _ct));

        Assert.Null(installed.Installed);
        Assert.Equal(0, absent.Uninstalls);
    }

    private sealed class FakeAutostart : IAutostart
    {
        public LoginItem? Item { get; set; }

        public int Uninstalls { get; private set; }

        public LoginApp? Installed => Item?.App;

        public string Location(LoginApp app) => "fake";

        public Task InstallAsync(LoginItem item, CancellationToken ct)
        {
            Item = item;
            return Task.CompletedTask;
        }

        public Task UninstallAsync(CancellationToken ct)
        {
            Item = null;
            Uninstalls++;
            return Task.CompletedTask;
        }
    }
}
