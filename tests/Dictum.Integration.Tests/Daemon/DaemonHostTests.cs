using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Integration.Tests.Daemon;

public sealed class DaemonHostTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-host", Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeHotkeys _hotkeys = new();
    private readonly EchoStt _stt = new("ok");
    private readonly List<FakeEngine> _engines = [];
    private readonly TaskCompletionSource _secondEngine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DaemonHostTests() => Directory.CreateDirectory(_dir);

    private string SocketPath => Path.Combine(_dir, "d.sock");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    /// <summary>Runs the daemon on a temp config and socket while <paramref name="body"/> talks to it, then stops it.</summary>
    private async Task RunAsync(Func<Task> body)
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
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
            new AppPaths(_dir, _dir),
            SocketPath,
            platform,
            NullLog.Instance,
            CreateEngine,
            _ => { },
            _ => { },
            new FakeSounds());

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var run = DaemonHost.RunAsync(options, stop.Token);
        try
        {
            await body();
        }
        finally
        {
            await stop.CancelAsync();
            await run.WaitAsync(Patience, _ct);
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
}
