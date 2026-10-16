using Dictum.App.Daemon;

namespace Dictum.App.Tests.Daemon;

public class DaemonLauncherTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task OutputGoesToTheLogAndTheExitCodeComesBack()
    {
        var log = new ListLog();
        var launcher = OperatingSystem.IsWindows()
            ? new DaemonProcessLauncher("cmd.exe", ["/c", "echo out & echo err 1>&2 & exit 3"], log)
            : new DaemonProcessLauncher("/bin/sh", ["-c", "echo out; echo err >&2; exit 3"], log);

        using var process = launcher.Launch();

        Assert.Equal(3, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), _ct));
        await Eventually.WaitForAsync(() => log.Has("daemon: out") && log.Has("daemon: err"), "both output lines");
        Assert.True(process.Id > 0);
    }

    [Fact]
    public async Task TheDaemonGetsTheExtraVariables()
    {
        var log = new ListLog();
        var environment = new Dictionary<string, string> { ["DICTUM_TEST_LAUNCH"] = "from-the-app" };
        var launcher = OperatingSystem.IsWindows()
            ? new DaemonProcessLauncher("cmd.exe", ["/c", "echo %DICTUM_TEST_LAUNCH%"], log, environment)
            : new DaemonProcessLauncher("/bin/sh", ["-c", "echo $DICTUM_TEST_LAUNCH"], log, environment);

        using var process = launcher.Launch();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        await Eventually.WaitForAsync(() => log.Has("daemon: from-the-app"), "the variable in the output");
    }

    [Fact]
    public void AMissingExecutableIsReportedBeforeAnythingStarts()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-dictum-{Guid.NewGuid():N}", "dictum.exe");

        var error = Assert.Throws<DaemonLaunchException>(() => new DaemonProcessLauncher(missing, ["daemon"], new ListLog()).Launch());

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task EndingAProcessThatIsAlreadyGoneIsQuiet()
    {
        var launcher = OperatingSystem.IsWindows()
            ? new DaemonProcessLauncher("cmd.exe", ["/c", "exit 0"], new ListLog())
            : new DaemonProcessLauncher("/bin/sh", ["-c", "exit 0"], new ListLog());
        using var process = launcher.Launch();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        await launcher.EndAsync(process.Id, TimeSpan.FromMilliseconds(100), _ct);
    }

    [Fact]
    public async Task EndingRefusesAProcessThatIsNotDictum()
    {
        var log = new ListLog();
        var launcher = new DaemonProcessLauncher("unused", [], log);

        await launcher.EndAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(50), _ct);

        Assert.True(log.Has("is not a Dictum daemon"));
    }
}
