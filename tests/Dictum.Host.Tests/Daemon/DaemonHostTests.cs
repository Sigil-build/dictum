using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Host.Tests.Daemon;

public sealed class DaemonHostTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>The warm-up follows the start, which a busy runner may deliver late.</summary>
    private static readonly TimeSpan SweepPatience = TimeSpan.FromSeconds(15);

    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeHotkeys _hotkeys = new();
    private readonly EchoStt _stt = new("ok");
    private readonly List<FakeEngine> _engines = [];
    private readonly TaskCompletionSource _secondEngine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string SocketPath => _dir.File("d.sock");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AReloadWhileTheHotkeysAreBeingRegisteredIsApplied()
    {
        var registering = _hotkeys.HoldNext();
        await RunAsync(async () =>
        {
            await _hotkeys.Held.Task.WaitAsync(Patience, _ct);

            var reload = new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Reload), _ct);
            await Task.WhenAny(reload, _secondEngine.Task).WaitAsync(Patience, _ct);
            registering.SetResult();
            var response = await reload;

            Assert.True(response.Ok, response.Error);
        });
    }

    [Fact]
    public async Task DoctorWhileTheHotkeysAreBeingRegisteredRunsTheChecks()
    {
        var registering = _hotkeys.HoldNext();
        await RunAsync(async () =>
        {
            await _hotkeys.Held.Task.WaitAsync(Patience, _ct);

            var response = await new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Doctor), _ct);
            registering.SetResult();

            Assert.True(response.Ok, response.Error);
            Assert.Contains(response.Checks!, c => c.Name == PlatformCheckNames.Daemon);
        });
    }

    [Fact]
    public async Task ASoundPlayerTheCallerGivesIsNotDisposedWithTheDaemon()
    {
        var sounds = new DisposableSounds();

        await RunAsync(() => Task.CompletedTask, sounds);

        Assert.False(sounds.Disposed);
    }

    /// <summary>
    /// After the warm-up the daemon stops the plugins that no engine holds, such as those a reload replaced. An instance
    /// whose lease is gone stands for one; a lease alone starts nothing, so no program runs here.
    /// </summary>
    [Fact]
    public async Task AfterTheWarmUpThePluginsNoEngineHoldsAreStopped()
    {
        var log = new ListLog();
        await using var plugins = new PluginHost(new PluginTrustStore(_dir.File("plugin-trust.json"), log), PlatformFactory.CreateProcessGroups(), log);
        var launch = new PluginLaunch("test", "Test", "1.0.0", _dir.Path, _dir.File("plugin.exe"), Model: null, Threads: null, _dir.File("cache"));
        plugins.Acquire(launch).Dispose();
        Assert.Single(plugins.Instances);

        await RunAsync(
            () => Eventually.WaitForAsync(() => plugins.Instances.Count == 0, "the sweep after the warm-up", SweepPatience),
            plugins: plugins);

        Assert.Empty(plugins.Instances);
    }

    /// <summary>
    /// The step after the warm-up that the daemon gets: the sweep, which asks whether the warmed-up engine is still the
    /// one in use. For an engine a reload has replaced it stops nothing, as the plugins it would stop may be the next
    /// engine's; for the engine in use it stops what no engine holds.
    /// </summary>
    [Fact]
    public async Task TheSweepAfterAWarmUpStopsNothingForAReplacedEngine()
    {
        var log = new ListLog();
        await using var plugins = new PluginHost(new PluginTrustStore(_dir.File("plugin-trust.json"), log), PlatformFactory.CreateProcessGroups(), log);
        var launch = new PluginLaunch("test", "Test", "1.0.0", _dir.Path, _dir.File("plugin.exe"), Model: null, Threads: null, _dir.File("cache"));
        plugins.Acquire(launch).Dispose();
        var sweep = DaemonHost.AfterWarmUp(plugins);
        Assert.NotNull(sweep);

        await sweep(() => false);
        Assert.Single(plugins.Instances);

        await sweep(() => true);
        Assert.Empty(plugins.Instances);
    }

    /// <summary>
    /// Runs the daemon on a temp config and socket while <paramref name="body"/> talks to it, waits until it has started,
    /// then stops it.
    /// </summary>
    private async Task RunAsync(Func<Task> body, ISoundPlayer? sounds = null, PluginHost? plugins = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(Config());
        var (config, warnings) = store.LoadWithIssues();
        var platform = new PlatformServices(
            "test",
            () => _hotkeys,
            new FakeInjector(),
            new UnavailableSecretStore(),
            null,
            new FakeNotifier(),
            _ => Task.FromResult<IReadOnlyList<PlatformCheck>>([]));
        var options = new DaemonHostOptions(
            config,
            warnings,
            store,
            new AppPaths(_dir.Path, _dir.Path),
            SocketPath,
            platform,
            NullLog.Instance,
            CreateEngine,
            _ => { },
            _ => started.TrySetResult(),
            sounds ?? new FakeSounds(),
            plugins);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var run = DaemonHost.RunAsync(options, stop.Token);
        try
        {
            await body();
            await started.Task.WaitAsync(Patience, _ct);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await run.WaitAsync(Patience, _ct);
            }
            catch (OperationCanceledException) when (run.IsCanceled)
            {
                // A body that failed early stops the daemon while it still starts.
            }
        }
    }

    private FakeEngine CreateEngine(DictumConfig config)
    {
        var engine = new FakeEngine(config, _stt);
        lock (_engines)
        {
            _engines.Add(engine);
            if (_engines.Count == 2)
            {
                _secondEngine.TrySetResult();
            }
        }

        return engine;
    }

    private static DictumConfig Config() => new()
    {
        Stt = new() { ["s"] = new WhisperLocalSttConfig { Model = "m.bin" } },
        Profiles = [new ProfileConfig { Id = "uk", Stt = "s", Language = "uk", Hotkey = "Ctrl+Alt+Space" }],
    };

    private sealed class DisposableSounds : ISoundPlayer, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task PlayAsync(SoundCue cue, CancellationToken ct) => Task.CompletedTask;

        public void Dispose() => Disposed = true;
    }
}
