using Dictum.Platform;

namespace Dictum.Core.Tests.Platform;

public class AutostartExtensionsTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TurningOnRewritesAnEntryAnOlderCopyInstalled()
    {
        var autostart = new FakeAutostart { Executable = @"D:\Tools\dictum-old\dictum.exe" };

        Assert.True(await autostart.ApplyAsync(enable: true, @"D:\Tools\dictum-new\dictum.exe", _ct));

        Assert.Equal(@"D:\Tools\dictum-new\dictum.exe", autostart.Executable);
    }

    [Fact]
    public async Task TurningOffRemovesOnlyAnEntryThatExists()
    {
        var installed = new FakeAutostart { Executable = "dictum" };
        var absent = new FakeAutostart();

        Assert.True(await installed.ApplyAsync(enable: false, "dictum", _ct));
        Assert.False(await absent.ApplyAsync(enable: false, "dictum", _ct));

        Assert.False(installed.IsInstalled);
        Assert.Equal(0, absent.Uninstalls);
    }

    private sealed class FakeAutostart : IAutostart
    {
        public string? Executable { get; set; }

        public int Uninstalls { get; private set; }

        public string Location => "fake";

        public bool IsInstalled => Executable is not null;

        public Task InstallAsync(string executable, CancellationToken ct)
        {
            Executable = executable;
            return Task.CompletedTask;
        }

        public Task UninstallAsync(CancellationToken ct)
        {
            Executable = null;
            Uninstalls++;
            return Task.CompletedTask;
        }
    }
}
