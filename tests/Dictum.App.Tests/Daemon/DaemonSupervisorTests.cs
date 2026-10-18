using System.Collections.Concurrent;
using System.Diagnostics;
using Dictum.App.Daemon;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Daemon;

public sealed class DaemonSupervisorTests : IAsyncDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-sup", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLog _log = new();
    private readonly ConcurrentQueue<DaemonLink> _links = new();
    private readonly FakeLauncher _launcher;
    private DaemonSupervisor? _supervisor;
    private bool _configExists = true;

    public DaemonSupervisorTests()
    {
        Directory.CreateDirectory(_dir);
        _launcher = new FakeLauncher(SocketPath);
    }

    private string SocketPath => Path.Combine(_dir, "d.sock");

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }

        await _launcher.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ConnectsToARunningDaemonWithoutStartingOne()
    {
        await using var running = FakeDaemon.Start(SocketPath);
        var supervisor = Start();

        await WaitForAsync(DaemonLinkState.Connected);

        Assert.Equal(0, _launcher.Launches);
        Assert.Equal(running.ProcessId, supervisor.Link.Status!.ProcessId);
    }

    [Fact]
    public async Task StartsTheDaemonWhenNoneIsRunning()
    {
        Start();

        await WaitForAsync(DaemonLinkState.Connected);

        Assert.Equal(1, _launcher.Launches);
        Assert.Equal(DaemonLinkState.Starting, _links.First().State);
    }

    [Fact]
    public async Task EventsAndStatusChangesArrive()
    {
        var supervisor = Start();
        var events = new ConcurrentQueue<IpcEvent>();
        supervisor.EventReceived += events.Enqueue;
        await WaitForAsync(DaemonLinkState.Connected);
        var daemon = _launcher.Current!;

        daemon.Events.Publish(new IpcEvent("state", State: "Recording", Profile: "uk"));
        daemon.Paused = true;
        daemon.Events.Publish(IpcEvent.ForStatus(daemon.Status()));

        await Eventually.WaitForAsync(() => supervisor.Link.Status?.Paused == true, "the paused status");
        Assert.Contains(events, e => e is { Event: "state", State: "Recording" });
    }

    [Fact]
    public async Task ADaemonThatExitsIsStartedAgain()
    {
        var supervisor = Start();
        await WaitForAsync(DaemonLinkState.Connected);

        await _launcher.Current!.ExitAsync();

        await Eventually.WaitForAsync(() => _launcher.Launches == 2 && supervisor.Link.State == DaemonLinkState.Connected, "the restart");
        Assert.Contains(_links, l => l.State == DaemonLinkState.Restarting);
    }

    [Fact]
    public async Task GivesUpAfterThreeRestartsInFiveMinutes()
    {
        var supervisor = Start();
        for (var launch = 1; launch <= 4; launch++)
        {
            var expected = launch;
            await Eventually.WaitForAsync(() => _launcher.Launches == expected && supervisor.Link.State == DaemonLinkState.Connected, $"start {expected}");
            await _launcher.Current!.ExitAsync();
        }

        await WaitForAsync(DaemonLinkState.Failed);
        Assert.Equal(4, _launcher.Launches);
        Assert.Contains("dictum-gui.log", supervisor.Link.Problem);

        supervisor.Restart();

        await Eventually.WaitForAsync(() => _launcher.Launches == 5 && supervisor.Link.State == DaemonLinkState.Connected, "the start the user asked for");
    }

    [Fact]
    public async Task RestartsAreCountedInAFiveMinuteWindow()
    {
        var supervisor = Start();
        for (var launch = 1; launch <= 3; launch++)
        {
            var expected = launch;
            await Eventually.WaitForAsync(() => _launcher.Launches == expected && supervisor.Link.State == DaemonLinkState.Connected, $"start {expected}");
            await _launcher.Current!.ExitAsync();
        }

        await Eventually.WaitForAsync(() => _launcher.Launches == 4 && supervisor.Link.State == DaemonLinkState.Connected, "the third restart");
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _launcher.Current!.ExitAsync();

        await Eventually.WaitForAsync(() => _launcher.Launches == 5 && supervisor.Link.State == DaemonLinkState.Connected, "a restart once the window moved on");
    }

    [Fact]
    public async Task ADaemonThatExitsWhileStartingFailsWithItsExitCode()
    {
        _launcher.Outcome = _ => LaunchOutcome.ExitsAtOnce;
        var supervisor = Start();

        await WaitForAsync(DaemonLinkState.Failed);

        Assert.Equal(4, _launcher.Launches);
        Assert.Contains("code 2", supervisor.Link.Problem);
    }

    [Fact]
    public async Task ADaemonThatNeverListensTimesOut()
    {
        _launcher.Outcome = _ => LaunchOutcome.NeverListens;
        var supervisor = Start(Options() with { StartTimeout = TimeSpan.FromMilliseconds(150) });

        await WaitForAsync(DaemonLinkState.Failed);

        Assert.Contains("did not answer", supervisor.Link.Problem);
    }

    [Fact]
    public async Task AMissingExecutableFailsAtOnce()
    {
        _launcher.Refuse = new DaemonLaunchException("dictum.exe was not found");
        var supervisor = Start();

        await WaitForAsync(DaemonLinkState.Failed);

        Assert.Equal("dictum.exe was not found", supervisor.Link.Problem);
    }

    [Fact]
    public async Task ADaemonWithoutSubscribeIsReportedAsAnotherVersion()
    {
        var old = FakeDaemon.Start(SocketPath, new FakeDaemonOptions(ProcessId: 77, Subscribes: false, StopsOnShutdown: false));
        _launcher.Adopt(old);
        var supervisor = Start();

        await WaitForAsync(DaemonLinkState.VersionMismatch);
        Assert.Equal(0, _launcher.Launches);

        supervisor.Restart();

        await Eventually.WaitForAsync(() => _launcher.Launches == 1 && supervisor.Link.State == DaemonLinkState.Connected, "our daemon");
        Assert.Equal([77], _launcher.Ended);
        Assert.Contains("shutdown", old.Commands);
    }

    [Fact]
    public async Task AnotherProtocolIsReportedAndRestartReplacesTheDaemon()
    {
        var other = FakeDaemon.Start(SocketPath, new FakeDaemonOptions(ProcessId: 78, Protocol: IpcProtocol.Version + 1));
        _launcher.Adopt(other);
        var supervisor = Start();

        await WaitForAsync(DaemonLinkState.VersionMismatch);
        Assert.Equal(IpcProtocol.Version + 1, supervisor.Link.Status!.Protocol);

        supervisor.Restart();

        await Eventually.WaitForAsync(() => _launcher.Launches == 1 && supervisor.Link.State == DaemonLinkState.Connected, "our daemon");
        Assert.Equal(0, await other.Exited);
    }

    [Fact]
    public async Task WithoutAConfigNothingStartsUntilOneAppears()
    {
        _configExists = false;
        Start();

        await WaitForAsync(DaemonLinkState.NotConfigured);
        await Task.Delay(100, _ct);
        Assert.Equal(0, _launcher.Launches);

        Volatile.Write(ref _configExists, true);

        await WaitForAsync(DaemonLinkState.Connected);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task QuitStopsTheDaemonAndDoesNotRestartIt()
    {
        var supervisor = Start();
        await WaitForAsync(DaemonLinkState.Connected);
        var daemon = _launcher.Current!;

        await supervisor.QuitAsync();

        Assert.Equal(DaemonLinkState.Stopped, supervisor.Link.State);
        Assert.Contains("shutdown", daemon.Commands);
        Assert.Equal(0, await daemon.Exited.WaitAsync(TimeSpan.FromSeconds(5), _ct));
        await Task.Delay(200, _ct);
        Assert.Equal(1, _launcher.Launches);
    }

    [Fact]
    public async Task QuitDoesNotWaitLongerThanItsTimeout()
    {
        var stubborn = FakeDaemon.Start(SocketPath, new FakeDaemonOptions(StopsOnShutdown: false));
        _launcher.Adopt(stubborn);
        var supervisor = Start(Options() with { QuitTimeout = TimeSpan.FromMilliseconds(300) });
        await WaitForAsync(DaemonLinkState.Connected);
        var watch = Stopwatch.StartNew();

        await supervisor.QuitAsync();

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        Assert.Equal(DaemonLinkState.Stopped, supervisor.Link.State);
        Assert.False(stubborn.Exited.IsCompleted);
    }

    [Fact]
    public async Task CommandsReachTheDaemon()
    {
        var supervisor = Start();
        await WaitForAsync(DaemonLinkState.Connected);

        var answer = await supervisor.SendAsync(new IpcRequest("use-profile", "uk"), _ct);

        Assert.True(answer.Ok);
        Assert.Contains("use-profile", _launcher.Current!.Commands);
    }

    [Fact]
    public async Task AFailingEventHandlerDoesNotStopSupervision()
    {
        var supervisor = Start();
        supervisor.EventReceived += _ => throw new InvalidOperationException("handler bug");
        await WaitForAsync(DaemonLinkState.Connected);
        var daemon = _launcher.Current!;

        daemon.Events.Publish(new IpcEvent("state", State: "Recording"));
        daemon.Paused = true;
        daemon.Events.Publish(IpcEvent.ForStatus(daemon.Status()));

        await Eventually.WaitForAsync(() => supervisor.Link.Status?.Paused == true, "the status after the failing handler");
        Assert.True(_log.Has("handler bug"));
    }

    [Fact]
    public async Task ADaemonStoppedOnPurposeIsNotStartedAgain()
    {
        var supervisor = Start();
        await WaitForAsync(DaemonLinkState.Connected);

        // What 'dictum shutdown' does: the daemon announces that it is stopping, then exits.
        await supervisor.SendAsync(new IpcRequest("shutdown"), _ct);

        await WaitForAsync(DaemonLinkState.Failed);
        await Task.Delay(300, _ct);
        Assert.Equal(1, _launcher.Launches);
        Assert.Equal("The daemon was stopped.", supervisor.Link.Problem);

        supervisor.Restart();

        await Eventually.WaitForAsync(() => _launcher.Launches == 2 && supervisor.Link.State == DaemonLinkState.Connected, "the start the user asked for");
    }

    [Fact]
    public async Task AnUnexpectedFailureLeavesRestartWorking()
    {
        var old = FakeDaemon.Start(SocketPath, new FakeDaemonOptions(ProcessId: 77, Subscribes: false, StopsOnShutdown: false));
        _launcher.Adopt(old);
        _launcher.EndFails = new InvalidOperationException("ending failed");
        var supervisor = Start();
        await WaitForAsync(DaemonLinkState.VersionMismatch);

        supervisor.Restart();
        await WaitForAsync(DaemonLinkState.Failed);
        Assert.Contains("dictum-gui.log", supervisor.Link.Problem);
        Assert.True(_log.Has("ending failed"));

        _launcher.EndFails = null;
        supervisor.Restart();
        await WaitForAsync(DaemonLinkState.VersionMismatch);
        supervisor.Restart();

        await Eventually.WaitForAsync(() => _launcher.Launches == 1 && supervisor.Link.State == DaemonLinkState.Connected, "our daemon");
    }

    [Fact]
    public async Task QuitWhileTheDaemonIsStartingEndsIt()
    {
        _launcher.Outcome = _ => LaunchOutcome.NeverListens;
        var supervisor = Start(Options() with { QuitTimeout = TimeSpan.FromMilliseconds(300) });
        await Eventually.WaitForAsync(() => _launcher.Launches == 1, "the launch");

        await supervisor.QuitAsync();

        Assert.Equal([1001], _launcher.Ended);
        Assert.Equal(DaemonLinkState.Stopped, supervisor.Link.State);
    }

    // PR 5b: the wizard's save starts the daemon at once, not at the next poll.
    [Fact]
    public async Task ASavedConfigStartsTheDaemonWithoutWaitingForThePoll()
    {
        _configExists = false;
        var supervisor = Start(Options() with { ConfigPollInterval = TimeSpan.FromHours(1) });
        await WaitForAsync(DaemonLinkState.NotConfigured);

        Volatile.Write(ref _configExists, true);
        supervisor.ConfigSaved();

        await WaitForAsync(DaemonLinkState.Connected);
        Assert.Equal(1, _launcher.Launches);
    }

    // PR 5b: a nudge while there is still no config keeps waiting; a later save still counts.
    [Fact]
    public async Task ANudgeWithoutAConfigKeepsWaiting()
    {
        _configExists = false;
        var supervisor = Start(Options() with { ConfigPollInterval = TimeSpan.FromHours(1) });
        await WaitForAsync(DaemonLinkState.NotConfigured);

        supervisor.ConfigSaved();
        await Task.Delay(100, _ct);
        Assert.Equal((DaemonLinkState.NotConfigured, 0), (supervisor.Link.State, _launcher.Launches));

        Volatile.Write(ref _configExists, true);
        supervisor.ConfigSaved();
        await WaitForAsync(DaemonLinkState.Connected);
    }

    private static SupervisorOptions Options() => new()
    {
        RetryDelay = TimeSpan.FromMilliseconds(20),
        ConfigPollInterval = TimeSpan.FromMilliseconds(20),
        StartTimeout = TimeSpan.FromSeconds(5),
        QuitTimeout = TimeSpan.FromSeconds(1),
    };

    private DaemonSupervisor Start(SupervisorOptions? options = null)
    {
        _supervisor = new DaemonSupervisor(
            SocketPath, () => Volatile.Read(ref _configExists), _launcher, _log, "dictum-gui.log", _clock, options ?? Options());
        _supervisor.LinkChanged += _links.Enqueue;
        _supervisor.Start();
        return _supervisor;
    }

    private Task WaitForAsync(DaemonLinkState state) =>
        Eventually.WaitForAsync(() => _supervisor!.Link.State == state, state.ToString());
}
