using System.Diagnostics;
using System.Text.Json.Nodes;
using Dictum.Core;
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

    /// <summary>A plugin's own start failures carry it (<see cref="PluginProcess"/>).</summary>
    private const string StartHint = "See the plugin's output, if it printed any, and its own documentation.";

    /// <summary>
    /// How long a crashing plugin lives after its info answer. Dictum's start ends only once it has read that answer,
    /// so a stalled runner needs room here, or a crash lands inside the start and fails it instead.
    /// </summary>
    private const int CrashAfterMs = 500;

    /// <summary>What a callback on a plugin's stopping token throws, as the tests that make its cancel fail have it.</summary>
    private const string CancelFailure = "The test fails this cancel.";

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

        TestPlugins.EndAllIn(_dir);
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

    /// <summary>
    /// The sweep asks whether its engine is current only once it has read the leases. Here the engine says it is, and
    /// lets go of its lease right after it has looked, as a reload that swaps it just then would. The lease was read
    /// before that, so the plugin is kept; the next engine's sweep, after its warm-up, decides.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheSweepAsksWhetherItsEngineIsCurrentAfterReadingTheLeases()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder()));
        await lease.Instance.ReadyAsync(ct);

        await host.StopUnusedAsync(() =>
        {
            var wasCurrent = true;
            lease.Dispose();
            return wasCurrent;
        }).WaitAsync(ct);

        Assert.Same(lease.Instance, Assert.Single(host.Instances));
        Assert.Equal(PluginInstanceState.Ready, lease.Instance.State);
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

    /// <summary>
    /// Ledger 72: the caller whose wait began the start stops waiting, as a dictation cancelled while the plugin loads
    /// its model does. That stops only its own wait: the start goes on for the other caller, and the plugin starts once.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ACallersTokenStopsOnlyItsOwnWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        using var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { LoadMs = 1500 })));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var firstWait = lease.Instance.ReadyAsync(cancelled.Token);
        var secondWait = lease.Instance.ReadyAsync(ct);

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait);
        Assert.Same(await secondWait, await lease.Instance.ReadyAsync(ct));
        Assert.Equal(PluginInstanceState.Ready, lease.Instance.State);
        Assert.Equal(1, lease.Instance.Launches);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnExitAfterReadyIsRestarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs });
        using var lease = host.Acquire(TestPlugins.Launch(folder));
        await lease.Instance.ReadyAsync(ct);
        var first = lease.Instance.ProcessId;

        // The plugin reads its behaviour at each start, so the restarted one stays up.
        new TestPluginBehaviour().Save(Path.Combine(folder, BehaviourFile));
        await Eventually.WaitForAsync(
            () => lease.Instance is { State: PluginInstanceState.Ready, ProcessId: { } pid } && pid != first, "the restarted plugin", Patience);

        Assert.Equal(2, lease.Instance.Launches);
    }

    /// <summary>
    /// Review focus 2: the plugin leaves a process that holds its output, then crashes. The crash is noticed when the
    /// plugin's own process exits, not when its pipes close, which would be a minute later here.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ACrashIsNoticedWhileAChildHoldsTheOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs, LeaveChild = true });
        using var lease = host.Acquire(TestPlugins.Launch(folder));
        await lease.Instance.ReadyAsync(ct);
        var first = lease.Instance.ProcessId;
        new TestPluginBehaviour().Save(Path.Combine(folder, BehaviourFile));

        // The plugin writes the child's pid right before it crashes.
        using var child = await TestPlugins.LeftChildAsync(folder, Patience);
        await Eventually.WaitForAsync(
            () => lease.Instance is { State: PluginInstanceState.Ready, ProcessId: { } pid } && pid != first, "the restarted plugin", TimeSpan.FromSeconds(10));

        Assert.Equal(2, lease.Instance.Launches);
    }

    /// <summary>
    /// Review focus 2: on Windows the plugin and every process it starts are in one group, which ends with the plugin.
    /// Without a group the child runs on for a minute, as <c>AProcessLeftRunningAfterTheExitIsWarnedOfOnce</c> shows.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AChildLeftByACrashIsEnded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has a group that ends what a plugin started");
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs, LeaveChild = true });
        using var lease = host.Acquire(TestPlugins.Launch(folder));
        await lease.Instance.ReadyAsync(ct);
        new TestPluginBehaviour().Save(Path.Combine(folder, BehaviourFile));

        using var child = await TestPlugins.LeftChildAsync(folder, Patience);

        if (child is not null)
        {
            await child.WaitForExitAsync(ct).WaitAsync(Patience, ct);
        }
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AfterThreeRestartsInFiveMinutesItStaysDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host(NoRestartDelay, new ManualClock());
        using var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs })));
        await lease.Instance.ReadyAsync(ct);

        await Eventually.WaitForAsync(() => lease.Instance.State == PluginInstanceState.Failed, "the plugin given up on", Patience);

        Assert.Equal(4, lease.Instance.Launches);
        Assert.Null(lease.Instance.ProcessId);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));
        Assert.Contains("kept stopping", error.Message, StringComparison.Ordinal);
        Assert.Equal("Plugin test kept stopping; Dictum stopped restarting it after 3 restarts within 5 minutes.", error.Message);
        Assert.Equal("Save the settings or restart the daemon to try again.", error.Hint);
    }

    /// <summary>
    /// A restart whose own start fails is one more exit within the budget, as the daemon's supervisor counts it, and
    /// is tried again after the delay. The plugin reads its behaviour at each start, so every restart here fails.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ARestartThatCannotStartIsTriedAgainWithinTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs });
        using var lease = host.Acquire(TestPlugins.Launch(folder));
        await lease.Instance.ReadyAsync(ct);

        new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["no CUDA here"] }.Save(Path.Combine(folder, BehaviourFile));
        await Eventually.WaitForAsync(() => lease.Instance.State == PluginInstanceState.Failed, "the plugin given up on", Patience);

        Assert.Equal(4, lease.Instance.Launches);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));
        Assert.Equal("Plugin test kept stopping; Dictum stopped restarting it after 3 restarts within 5 minutes.", error.Message);
        Assert.Equal("Save the settings or restart the daemon to try again.", error.Hint);
        Assert.Contains("no CUDA here", error.Output);
    }

    /// <summary>
    /// A restart is a launch like any other: the user's confirmation is checked again, and once it is gone the plugin
    /// stays stopped. The record is forgotten rather than the program changed, since Windows locks a running program.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ARestartChecksTheConfirmationAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        using var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs })));
        await lease.Instance.ReadyAsync(ct);

        _trust.Forget("test");
        await Eventually.WaitForAsync(() => lease.Instance.State == PluginInstanceState.Stopped, "the restart refused", Patience);

        Assert.Equal(1, lease.Instance.Launches);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));
        Assert.Equal("Plugin test has not been confirmed on this computer.", error.Message);
        Assert.Equal(1, lease.Instance.Launches);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    /// <summary>A start that begins once a stop is under way, as a restart whose delay has just ended may, launches nothing.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AStartUnderAStopLaunchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = PluginHttp.CreateClient();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var instance = new PluginInstance(
            TestPlugins.Launch(ConfirmedFolder()),
            _trust,
            _log,
            http,
            PlatformFactory.CreateProcessGroups(),
            PluginHostOptions.Default,
            TimeProvider.System,
            stopping.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => instance.ReadyAsync(ct));

        Assert.Equal(0, instance.Launches);
        Assert.Equal(PluginInstanceState.Stopped, instance.State);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task RestartsOutsideTheWindowDoNotCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualClock();
        var host = Host(NoRestartDelay, clock);
        using var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder(new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs })));
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
        var folder = ConfirmedFolder(new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["no CUDA here"] });
        var launch = TestPlugins.Launch(folder);
        using var first = host.Acquire(launch);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => first.Instance.ReadyAsync(ct));
        Assert.Equal("Plugin test exited with code 3 while starting.", error.Message);
        Assert.Equal(StartHint, error.Hint);
        Assert.Equal(PluginInstanceState.Failed, first.Instance.State);

        // Without a new lease it stays failed: later callers, such as Settings' Test, get why and how to retry.
        var later = await Assert.ThrowsAsync<PluginUnavailableException>(() => first.Instance.ReadyAsync(ct));
        Assert.Equal(error.Message, later.Message);
        Assert.Equal($"{StartHint} Then save the settings or restart the daemon to try again.", later.Hint);
        Assert.Contains("no CUDA here", later.Output);
        Assert.Equal(error.Output, later.Output);
        Assert.Equal(1, first.Instance.Launches);

        new TestPluginBehaviour().Save(Path.Combine(folder, BehaviourFile));
        using var second = host.Acquire(launch);
        await second.Instance.ReadyAsync(ct);

        Assert.Same(first.Instance, second.Instance);
        Assert.Equal(PluginInstanceState.Ready, second.Instance.State);
        Assert.Equal(2, second.Instance.Launches);
    }

    /// <summary>
    /// Review focus 5: a config copied from another computer names a plugin nobody confirmed here. Its folder has
    /// spaces, so the hint quotes it for the command to work when pasted.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnUnconfirmedPluginNeverStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = TestPlugins.Folder(_dir, folderName: "плагін з пробілом");
        using var lease = host.Acquire(TestPlugins.Launch(folder));

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));

        Assert.Equal("Plugin test has not been confirmed on this computer.", error.Message);
        Assert.Equal($"Confirm it with 'dictum plugins add \"{folder}\"'.", error.Hint);
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

        Assert.Equal($"Plugin test changed since you added it ({Path.GetFileName(launch.Executable)}).", error.Message);
        Assert.Equal($"Confirm it with 'dictum plugins add \"{folder}\"'.", error.Hint);
        Assert.Equal(0, lease.Instance.Launches);
        Assert.Equal(PluginInstanceState.Stopped, lease.Instance.State);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    /// <summary>
    /// A file that plugin.json lists, here the test plugin's own code, which its launcher loads, changed after the user
    /// confirmed the plugin; the launcher did not. The error names the file.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AListedFileChangedAfterConfirmingNeverStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = TestPlugins.Folder(_dir, program: [TestPlugins.AssemblyFile]);
        var launch = TestPlugins.Launch(folder);
        _trust.Confirm(launch.PluginId, folder, launch.Executable);
        AppendByte(Path.Combine(folder, TestPlugins.AssemblyFile));
        using var lease = host.Acquire(launch);

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));

        Assert.Equal($"Plugin test changed since you added it ({TestPlugins.AssemblyFile}).", error.Message);
        Assert.Equal($"Confirm it with 'dictum plugins add \"{folder}\"'.", error.Hint);
        Assert.Equal(0, lease.Instance.Launches);
        Assert.Equal(PluginInstanceState.Stopped, lease.Instance.State);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    /// <summary>
    /// Ledger 139: a model path that a batch launcher cannot be given is refused before anything runs, so it is no
    /// launch. The instance fails as a start that fails does, so later callers get why, and how to try again.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ARefusedBatchLaunchIsNotCounted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows runs a batch file through cmd.exe");
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = TestPlugins.BatchLauncherFolder(_dir, "plugin.cmd");
        var launch = TestPlugins.Launch(folder, Directory.CreateDirectory(_dir.File("R&D")).FullName);
        _trust.Confirm(launch.PluginId, folder, launch.Executable);
        using var lease = host.Acquire(launch);

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));

        Assert.StartsWith("Plugin test starts through plugin.cmd, which cannot be given ", error.Message);
        Assert.Equal(0, lease.Instance.Launches);
        Assert.Equal(PluginInstanceState.Failed, lease.Instance.State);
        Assert.False(File.Exists(Path.Combine(folder, TestPlugins.StartedMark)), "the launcher ran");
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

    /// <summary>
    /// The check reads plugin.json afresh, not the launch the provider resolved once, so a program list edited and
    /// confirmed again starts at the next try too, with no reload.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ConfirmingAnEditedProgramListAgainLetsItStartWithoutAReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();
        var launch = TestPlugins.Launch(folder);
        var manifestFile = Path.Combine(folder, PluginProtocol.ManifestFile);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestFile))!;
        manifest["program"] = new JsonArray(TestPlugins.AssemblyFile);
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        using var lease = host.Acquire(launch);
        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => lease.Instance.ReadyAsync(ct));
        Assert.Equal($"Plugin test changed since you added it ({TestPlugins.AssemblyFile}).", error.Message);

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

    /// <summary>
    /// Ledger 77: a part of the disposal that throws does not stop the rest. Here letting go of one plugin fails once it
    /// has stopped; the other plugin is still stopped, and the host's client is still disposed.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task DisposingGoesOnWhenOnePartThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host(groups: new FailingGroups(PlatformFactory.CreateProcessGroups()));
        var folder = ConfirmedFolder();
        using var failing = host.Acquire(TestPlugins.Launch(folder, threads: 2));
        await failing.Instance.ReadyAsync(ct);
        using var other = host.Acquire(TestPlugins.Launch(folder, threads: 4));
        await other.Instance.ReadyAsync(ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask().WaitAsync(ct));

        Assert.Equal(FailingGroups.Failure, error.Message);
        Assert.Equal(PluginInstanceState.Stopped, other.Instance.State);
        Assert.False(TestPlugins.RunsIn(_dir), "a plugin still runs");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Http.GetAsync(new Uri("http://127.0.0.1:1/"), ct));
    }

    /// <summary>
    /// Ledger 77's main risk: the disposal's first part cancels every start, and a callback on that token that throws
    /// fails the cancel. That used to end the disposal there, with every plugin still running.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task DisposingStopsEveryPluginWhenTheCancelThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var folder = ConfirmedFolder();
        using var failing = host.Acquire(TestPlugins.Launch(folder, threads: 2));
        using var other = host.Acquire(TestPlugins.Launch(folder, threads: 4));
        await Task.WhenAll(failing.Instance.ReadyAsync(ct), other.Instance.ReadyAsync(ct));
        _ = failing.Instance.Stopping.Register(() => throw new InvalidOperationException(CancelFailure));

        var error = await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask().WaitAsync(ct));

        Assert.Contains(CancelFailure, error.Flatten().InnerExceptions.Select(inner => inner.Message));
        Assert.Equal(PluginInstanceState.Stopped, failing.Instance.State);
        Assert.Equal(PluginInstanceState.Stopped, other.Instance.State);
        Assert.False(TestPlugins.RunsIn(_dir), "a plugin still runs");
    }

    /// <summary>The same for the one plugin a sweep stops: a cancel that throws no longer leaves its process running.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ASweepStopsThePluginWhenItsCancelThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Host();
        var lease = host.Acquire(TestPlugins.Launch(ConfirmedFolder()));
        await lease.Instance.ReadyAsync(ct);
        var pid = lease.Instance.ProcessId!.Value;
        _ = lease.Instance.Stopping.Register(() => throw new InvalidOperationException(CancelFailure));
        lease.Dispose();

        var error = await Assert.ThrowsAsync<AggregateException>(() => host.StopUnusedAsync().WaitAsync(ct));

        Assert.Contains(CancelFailure, error.Flatten().InnerExceptions.Select(inner => inner.Message));
        Assert.Empty(host.Instances);
        Assert.False(Runs(pid), "the plugin still runs");
    }

    private PluginHost Host(PluginHostOptions? options = null, TimeProvider? time = null, IProcessGroups? groups = null)
    {
        var host = new PluginHost(_trust, groups ?? PlatformFactory.CreateProcessGroups(), _log, options, time);
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

    /// <summary>
    /// The system's groups, except that letting go of the first one fails once it has ended: as Dictum lets go of that
    /// plugin, after it has stopped and its group has killed what it left.
    /// </summary>
    private sealed class FailingGroups(IProcessGroups groups) : IProcessGroups
    {
        public const string Failure = "The test cannot let go of this group.";

        private int _enclosed;

        public IDisposable Enclose(Process process)
        {
            var group = groups.Enclose(process);
            return Interlocked.Increment(ref _enclosed) == 1 ? new FailingGroup(group) : group;
        }

        private sealed class FailingGroup(IDisposable group) : IDisposable
        {
            private int _disposals;

            public void Dispose()
            {
                group.Dispose();
                if (Interlocked.Increment(ref _disposals) > 1)
                {
                    throw new InvalidOperationException(Failure);
                }
            }
        }
    }
}
