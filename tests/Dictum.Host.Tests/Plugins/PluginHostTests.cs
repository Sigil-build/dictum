using System.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Host.Tests.Plugins;

/// <summary>
/// The plugin host over copies of the test plugin, with a real trust record in a temp folder. Each plugin is confirmed
/// as <c>dictum plugins add</c> would, unless the test says otherwise.
/// </summary>
public sealed class PluginHostTests : IAsyncDisposable
{
    private const string BehaviourFile = "behaviour.json";

    /// <summary>For waits on starts, crashes and restarts, which a busy runner may deliver late.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>For the tests that crash the plugin again and again.</summary>
    private static readonly PluginHostOptions NoRestartDelay = PluginHostOptions.Default with { RestartDelay = TimeSpan.Zero };

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();
    private readonly List<PluginHost> _hosts = [];
    private readonly PluginTrustStore _trust;

    public PluginHostTests()
    {
        _trust = new PluginTrustStore(_dir.File("plugin-trust.json"), _log);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        _dir.Dispose();
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheSameKeySharesOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var launch = TestPlugins.Launch(ConfirmedFolder());

        using var a = host.Acquire(launch);
        using var b = host.Acquire(launch);
        var first = await a.Instance.ReadyAsync(ct);
        var second = await b.Instance.ReadyAsync(ct);

        Assert.Same(a.Instance, b.Instance);
        Assert.Same(first, second);
        Assert.Equal(1, a.Instance.Launches);
        Assert.Single(host.Instances);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task OtherThreadsStartAnotherProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();

        using var two = host.Acquire(TestPlugins.Launch(folder, threads: 2));
        using var four = host.Acquire(TestPlugins.Launch(folder, threads: 4));
        await Task.WhenAll(two.Instance.ReadyAsync(ct), four.Instance.ReadyAsync(ct));

        Assert.NotSame(two.Instance, four.Instance);
        Assert.NotNull(two.Instance.ProcessId);
        Assert.NotNull(four.Instance.ProcessId);
        Assert.NotEqual(two.Instance.ProcessId, four.Instance.ProcessId);
        Assert.Equal(2, host.Instances.Count);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task StopUnusedStopsOnlyInstancesWithoutALease()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();
        using var kept = host.Acquire(TestPlugins.Launch(folder, threads: 2));
        var released = host.Acquire(TestPlugins.Launch(folder, threads: 4));
        await Task.WhenAll(kept.Instance.ReadyAsync(ct), released.Instance.ReadyAsync(ct));
        var keptPid = kept.Instance.ProcessId!.Value;
        var releasedPid = released.Instance.ProcessId!.Value;

        released.Dispose();
        await host.StopUnusedAsync().WaitAsync(ct);

        Assert.Same(kept.Instance, Assert.Single(host.Instances));
        Assert.Equal(PluginInstanceState.Ready, kept.Instance.State);
        Assert.True(Runs(keptPid), "the leased plugin was stopped");
        Assert.Equal(PluginInstanceState.Stopped, released.Instance.State);
        Assert.False(Runs(releasedPid), "the released plugin still runs");
    }

    /// <summary>
    /// The sweep's stop is not a crash. With no restart delay, a restart would launch the swept plugin again within
    /// milliseconds, long before a new start of the same plugin is ready.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AStopDictumAskedForIsNotRestarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host(NoRestartDelay);
        var launch = TestPlugins.Launch(ConfirmedFolder());
        var swept = host.Acquire(launch);
        await swept.Instance.ReadyAsync(ct);
        swept.Dispose();
        await host.StopUnusedAsync().WaitAsync(ct);

        using var next = host.Acquire(launch);
        await next.Instance.ReadyAsync(ct);

        Assert.NotSame(swept.Instance, next.Instance);
        Assert.Equal(1, swept.Instance.Launches);
        Assert.Equal(PluginInstanceState.Stopped, swept.Instance.State);
    }

    /// <summary>The order of a reload: the new engine takes its lease before the old engine lets go of its own.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ANewLeaseTakenBeforeTheOldOneIsReleasedKeepsTheProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var launch = TestPlugins.Launch(ConfirmedFolder());
        var a = host.Acquire(launch);
        await a.Instance.ReadyAsync(ct);
        var pid = a.Instance.ProcessId;

        using var b = host.Acquire(launch);
        a.Dispose();
        await host.StopUnusedAsync().WaitAsync(ct);
        await b.Instance.ReadyAsync(ct);

        Assert.Equal(pid, b.Instance.ProcessId);
        Assert.Equal(1, b.Instance.Launches);
    }

    /// <summary>Review focus 2: a reload while the plugin still loads its model joins that start; the model loads once.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AReloadDuringASlowStartJoinsTheStartingProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var launch = TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { LoadMs = 1500 }));
        var a = host.Acquire(launch);
        var aReady = a.Instance.ReadyAsync(ct);

        using var b = host.Acquire(launch);
        a.Dispose();
        await host.StopUnusedAsync().WaitAsync(ct);
        Assert.Equal(PluginInstanceState.Starting, b.Instance.State);
        var bReady = b.Instance.ReadyAsync(ct);

        Assert.Same(await aReady, await bReady);
        Assert.Equal(1, b.Instance.Launches);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnExitAfterReadyIsRestarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder(new TestPluginBehaviour { ExitAfterReadyMs = 200 });
        using var lease = host.Acquire(TestPlugins.Launch(folder));
        await lease.Instance.ReadyAsync(ct);
        var first = lease.Instance.ProcessId;

        // The plugin reads its behaviour at each start, so the restarted one stays up.
        new TestPluginBehaviour().Save(Path.Combine(folder, BehaviourFile));
        await Eventually.WaitForAsync(
            () => lease.Instance is { State: PluginInstanceState.Ready, ProcessId: { } pid } && pid != first, "the restarted plugin", Patience);

        Assert.Equal(2, lease.Instance.Launches);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AfterThreeRestartsInFiveMinutesItStaysDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host(NoRestartDelay, new ManualClock());
        using var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { ExitAfterReadyMs = 100 })));
        await lease.Instance.ReadyAsync(ct);

        await Eventually.WaitForAsync(() => lease.Instance.State == PluginInstanceState.Failed, "the plugin given up on", Patience);

        Assert.Equal(4, lease.Instance.Launches);
        Assert.Null(lease.Instance.ProcessId);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));
        Assert.Contains("kept stopping", error.Message, StringComparison.Ordinal);
        Assert.Equal("Plugin test kept stopping; Dictum stopped restarting it after 3 restarts within 5 minutes.", error.Message);
        Assert.Equal("Save the settings or restart the daemon to try again.", error.Hint);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task RestartsOutsideTheWindowDoNotCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var host = Host(NoRestartDelay, clock);
        using var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { ExitAfterReadyMs = 100 })));
        await lease.Instance.ReadyAsync(ct);

        for (var launches = 2; launches <= 5; launches++)
        {
            await Eventually.WaitForAsync(
                () => lease.Instance.Launches >= launches || lease.Instance.State == PluginInstanceState.Failed, $"launch {launches}", Patience);
            clock.Advance(TimeSpan.FromMinutes(6));
        }

        Assert.NotEqual(PluginInstanceState.Failed, lease.Instance.State);
        Assert.True(lease.Instance.Launches >= 5, $"{lease.Instance.Launches} launches");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ANewLeaseRetriesAFailedPlugin()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder(new TestPluginBehaviour { ExitAtStart = 3 });
        var launch = TestPlugins.Launch(folder);
        using var first = host.Acquire(launch);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => first.Instance.ReadyAsync(ct));
        Assert.Equal("Plugin test exited with code 3 while starting.", error.Message);
        Assert.Equal(PluginInstanceState.Failed, first.Instance.State);

        // Without a new lease it stays failed, and says why.
        Assert.Same(error, await Assert.ThrowsAsync<PluginUnavailableException>(() => first.Instance.ReadyAsync(ct)));
        Assert.Equal(1, first.Instance.Launches);

        new TestPluginBehaviour().Save(Path.Combine(folder, BehaviourFile));
        using var second = host.Acquire(launch);
        await second.Instance.ReadyAsync(ct);

        Assert.Same(first.Instance, second.Instance);
        Assert.Equal(PluginInstanceState.Ready, second.Instance.State);
        Assert.Equal(2, second.Instance.Launches);
    }

    /// <summary>Review focus 5: a config copied from another computer names a plugin nobody confirmed here.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnUnconfirmedPluginNeverStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = TestPlugins.Folder(_dir);
        using var lease = host.Acquire(TestPlugins.Launch(folder));

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));

        Assert.Equal("Plugin test has not been confirmed on this computer.", error.Message);
        Assert.Equal($"Confirm it with 'dictum plugins add {folder}'.", error.Hint);
        Assert.Equal(0, lease.Instance.Launches);
        Assert.Equal(PluginInstanceState.Stopped, lease.Instance.State);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    /// <summary>Review focus 5: the program in a confirmed folder is no longer the one the user confirmed.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProgramChangedAfterConfirmingNeverStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();
        var launch = TestPlugins.Launch(folder);
        AppendByte(launch.Executable);
        using var lease = host.Acquire(launch);

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));

        Assert.Equal("Plugin test changed since you added it.", error.Message);
        Assert.Equal($"Confirm it with 'dictum plugins add {folder}'.", error.Hint);
        Assert.Equal(0, lease.Instance.Launches);
        Assert.Equal(PluginInstanceState.Stopped, lease.Instance.State);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ConfirmingAgainLetsItStartWithoutAReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();
        var launch = TestPlugins.Launch(folder);
        AppendByte(launch.Executable);
        using var lease = host.Acquire(launch);
        await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));

        _trust.Confirm(launch.PluginId, folder, launch.Executable);
        await lease.Instance.ReadyAsync(ct);

        Assert.Equal(PluginInstanceState.Ready, lease.Instance.State);
        Assert.Equal(1, lease.Instance.Launches);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task DisposeStopsEveryProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();
        using var two = host.Acquire(TestPlugins.Launch(folder, threads: 2));
        using var four = host.Acquire(TestPlugins.Launch(folder, threads: 4));
        await Task.WhenAll(two.Instance.ReadyAsync(ct), four.Instance.ReadyAsync(ct));
        var model = TestPlugins.Model(_dir, new TestPluginBehaviour { LoadMs = 60_000 });
        using var loading = host.Acquire(TestPlugins.Launch(folder, model));
        var loadingReady = loading.Instance.ReadyAsync(ct);
        await Eventually.WaitForAsync(() => loading.Instance.Launches == 1, "the launch of the slow plugin", Patience);
        Assert.True(TestPlugins.RunsIn(_dir), "the check does not see the running plugins");

        await host.DisposeAsync().AsTask().WaitAsync(ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loadingReady);
        Assert.False(TestPlugins.RunsIn(_dir), "a plugin still runs");
        Assert.Empty(host.Instances);
    }

    private PluginHost Host(PluginHostOptions? options = null, TimeProvider? time = null)
    {
        var host = new PluginHost(_trust, _log, options, time);
        _hosts.Add(host);
        return host;
    }

    /// <summary>The test plugin copied into this test's folder and confirmed, with the behaviour when given.</summary>
    private string ConfirmedFolder(TestPluginBehaviour? behaviour = null)
    {
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);
        var launch = TestPlugins.Launch(folder);
        _trust.Confirm(launch.PluginId, folder, launch.Executable);
        return folder;
    }

    /// <summary>Changes the program as an update or a tampering would; a byte past its end leaves it able to run.</summary>
    private static void AppendByte(string path)
    {
        using var stream = new FileStream(path, FileMode.Append);
        stream.WriteByte(0);
    }

    private static bool Runs(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process has that id any more.
            return false;
        }
    }
}
