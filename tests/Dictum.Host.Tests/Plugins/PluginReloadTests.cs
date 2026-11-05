using System.Diagnostics;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Host.Daemon;
using Dictum.Host.Tests.Daemon;
using Dictum.Platform;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Host.Tests.Plugins;

/// <summary>
/// Real engines over one plugin host, reloaded in the daemon's order: the new engine is built, the old one disposed,
/// the new one warmed up, and then the plugins no engine holds are stopped.
/// </summary>
public sealed class PluginReloadTests : IAsyncDisposable
{
    /// <summary>For waits on a plugin's start, which a busy runner may deliver late.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();
    private readonly PluginHost _plugins;
    private readonly AppPaths _paths;
    private readonly string _folder;
    private readonly string _model;
    private readonly List<DictumHost> _engines = [];

    public PluginReloadTests()
    {
        var trust = new PluginTrustStore(_dir.File("plugin-trust.json"), _log);
        _plugins = new PluginHost(trust, PlatformFactory.CreateProcessGroups(), _log);
        _paths = new AppPaths(_dir.Path, _dir.Path);
        _folder = TestPlugins.Folder(_dir);
        var launch = TestPlugins.Launch(_folder);
        trust.Confirm(launch.PluginId, _folder, launch.Executable);
        _model = TestPlugins.Model(_dir, new TestPluginBehaviour());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            await engine.DisposeAsync();
        }

        await _plugins.DisposeAsync();
        _dir.Dispose();
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AReloadWithTheSameProviderKeepsTheProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Engine(Config());
        await a.WarmUpAsync(ct);
        var instance = Assert.Single(_plugins.Instances);
        Assert.Equal(PluginInstanceState.Ready, instance.State);
        var pid = instance.ProcessId;

        await ReloadAsync(a, Engine(Config()), ct);

        Assert.Same(instance, Assert.Single(_plugins.Instances));
        Assert.Equal(PluginInstanceState.Ready, instance.State);
        Assert.Equal(pid, instance.ProcessId);
        Assert.Equal(1, instance.Launches);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AReloadThatChangesThreadsReplacesTheProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Engine(Config());
        await a.WarmUpAsync(ct);
        var old = Assert.Single(_plugins.Instances);
        var oldPid = old.ProcessId!.Value;

        await ReloadAsync(a, Engine(Config(threads: 2)), ct);

        var current = Assert.Single(_plugins.Instances);
        Assert.NotSame(old, current);
        Assert.Equal(2, current.Key.Threads);
        Assert.Equal(PluginInstanceState.Ready, current.State);
        Assert.Equal(PluginInstanceState.Stopped, old.State);
        Assert.False(Runs(oldPid), "the old plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProviderNoProfileUsesIsStoppedAfterTheReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Engine(Config());
        await a.WarmUpAsync(ct);
        var oldPid = Assert.Single(_plugins.Instances).ProcessId!.Value;

        await ReloadAsync(a, Engine(Config(profileStt: "w")), ct);

        Assert.Empty(_plugins.Instances);
        Assert.False(Runs(oldPid), "the plugin still runs");
    }

    /// <summary>
    /// One plugin provider that fails in a way nobody planned for does not end the warm-up: the next profile's plugin
    /// still takes its lease and starts, so the sweep after the warm-up keeps it.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ABrokenProviderDoesNotStopTheWarmUpOfTheNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new DictumConfig
        {
            Stt =
            {
                ["broken"] = new PluginSttConfig { Plugin = "broken", Model = _model },
                ["p"] = new PluginSttConfig { Plugin = "test", Model = _model },
            },
            Plugins =
            {
                ["broken"] = new PluginRegistration { Path = _dir.File("bad\0name") },
                ["test"] = new PluginRegistration { Path = _folder },
            },
            Profiles = [new ProfileConfig { Id = "first", Stt = "broken" }, new ProfileConfig { Id = "second", Stt = "p" }],
        };

        await Engine(config).WarmUpAsync(ct);
        await _plugins.StopUnusedAsync().WaitAsync(ct);

        Assert.Equal(PluginInstanceState.Ready, Assert.Single(_plugins.Instances).State);
    }

    /// <summary>
    /// Ledger 91: a disposed engine makes no provider. A provider made after the disposal, which nothing disposes, would
    /// hold its plugin's lease for good once used, as a warm-up uses it.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task GetSttAfterDisposeTakesNoLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = Engine(Config());
        await engine.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => engine.GetStt("p"));
        Assert.Throws<ObjectDisposedException>(() => engine.CreateAdHocStt("q", new PluginSttConfig { Plugin = "test", Model = _model }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.WarmUpAsync(ct));
        Assert.Empty(_plugins.Instances);
    }

    /// <summary>
    /// Final review item 9: a reload replaces the engine and disposes the old one between the daemon's check that the
    /// warmed-up engine is current and the sweep. The new engine takes its lease only at its warm-up, after the hotkeys
    /// are registered, so at the sweep no lease holds the plugin. The sweep sees that its engine is no longer current
    /// and stops nothing, and the new engine keeps the process.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AReloadDuringTheSweepKeepsTheNewEnginesPlugin()
    {
        var ct = TestContext.Current.CancellationToken;
        var hotkeys = new FakeHotkeys();
        var platform = new PlatformServices(
            "test",
            () => hotkeys,
            new FakeInjector(),
            new UnavailableSecretStore(),
            null,
            new FakeNotifier(),
            _ => Task.FromResult<IReadOnlyList<PlatformCheck>>([]));
        var firstSweepWaits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSweepEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweeps = 0;

        // The daemon disposes the engines it makes: the old one at the reload, the current one with itself.
        await using var daemon = new DictumDaemon(
            Config(),
            config => DictumHost.Create(_paths, _log, config, plugins: () => _plugins),
            platform,
            new FakeSounds(),
            _log,
            afterWarmUp: async isCurrent =>
            {
                // DaemonHost's sweep. The first one waits, past the daemon's check, until the reload has replaced its engine.
                var first = Interlocked.Increment(ref sweeps) == 1;
                if (first)
                {
                    firstSweepWaits.SetResult();
                    await reloaded.Task;
                }

                await _plugins.StopUnusedAsync(isCurrent);
                if (first)
                {
                    firstSweepEnded.SetResult();
                }
            });
        await daemon.StartAsync(ct);
        await firstSweepWaits.Task.WaitAsync(Patience, ct);
        var instance = Assert.Single(_plugins.Instances);
        var pid = instance.ProcessId;

        // The reload disposes the old engine, then waits in the hotkeys' registration, before the new engine warms up.
        var registering = hotkeys.HoldNext();
        var reload = daemon.ApplyConfigAsync(Config(), ct);
        await hotkeys.Held.Task.WaitAsync(Patience, ct);
        reloaded.SetResult();
        await firstSweepEnded.Task.WaitAsync(Patience, ct);
        registering.SetResult();
        await reload.WaitAsync(Patience, ct);
        await daemon.IdleAsync().WaitAsync(Patience, ct);

        Assert.Equal(2, sweeps);
        Assert.Equal(PluginInstanceState.Ready, instance.State);
        Assert.Same(instance, Assert.Single(_plugins.Instances));
        Assert.Equal(pid, instance.ProcessId);
        Assert.Equal(1, instance.Launches);
    }

    /// <summary>The rest of the daemon's reload, once the new engine is built: dispose the old one, warm up the new one, sweep.</summary>
    private async Task ReloadAsync(DictumHost old, DictumHost next, CancellationToken ct)
    {
        _engines.Remove(old);
        await old.DisposeAsync();
        await next.WarmUpAsync(ct);
        await _plugins.StopUnusedAsync().WaitAsync(ct);
    }

    private DictumHost Engine(DictumConfig config)
    {
        var engine = DictumHost.Create(_paths, _log, config, plugins: () => _plugins);
        _engines.Add(engine);
        return engine;
    }

    /// <summary>Provider <c>p</c> on the test plugin, and a Whisper provider <c>w</c> whose model is missing, which fails its check at once.</summary>
    private DictumConfig Config(int? threads = null, string profileStt = "p") => new()
    {
        Stt =
        {
            ["p"] = new PluginSttConfig { Plugin = "test", Model = _model, Threads = threads },
            ["w"] = new WhisperLocalSttConfig { Model = "missing.bin" },
        },
        Plugins = { ["test"] = new PluginRegistration { Path = _folder } },
        Profiles = [new ProfileConfig { Id = "dictate", Stt = profileStt }],
    };

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
