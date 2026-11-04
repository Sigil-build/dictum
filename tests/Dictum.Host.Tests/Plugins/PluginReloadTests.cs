using System.Diagnostics;
using Dictum.Core;
using Dictum.Core.Configuration;
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
        var engine = DictumHost.Create(_paths, _log, config, plugins: _plugins);
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
