using Dictum.App.Daemon;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Daemon;

/// <summary>
/// The supervisor with the real <c>dictum daemon</c>, in temp folders, with the microphone replaced by a file.
/// Enabled with DICTUM_INTEGRATION=1 and DICTUM_TEST_MODEL=&lt;path to a ggml model&gt;. The profile has no hotkey,
/// so it never competes with a daemon the user is running, and nothing is pasted.
/// </summary>
public sealed class RealDaemonTests : IAsyncDisposable
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("DICTUM_TEST_MODEL");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-real", Guid.NewGuid().ToString("N")[..8]);
    private DaemonSupervisor? _supervisor;

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.QuitAsync();
            await _supervisor.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task EventsArriveAndTheSampleIsTranscribed()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1", "Set DICTUM_INTEGRATION=1 to run");
        Assert.SkipWhen(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set DICTUM_TEST_MODEL to a ggml model");

        var config = Path.Combine(_dir, "config");
        var socket = Path.Combine(_dir, "d.sock");
        var store = new ConfigStore(Path.Combine(config, "config.json"), NullLog.Instance);
        store.Save(new DictumConfig
        {
            Stt = { ["local"] = new WhisperLocalSttConfig { Model = Model! } },
            Profiles = [new ProfileConfig { Id = "test", Stt = "local", Language = "en", Output = OutputMode.Clipboard }],
        });
        var log = new ListLog();
        var launcher = new DaemonProcessLauncher(CliExecutable(), ["daemon"], log, new Dictionary<string, string>
        {
            ["DICTUM_CONFIG_DIR"] = config,
            ["DICTUM_DATA_DIR"] = Path.Combine(_dir, "data"),
            ["DICTUM_SOCKET"] = socket,
            ["DICTUM_AUDIO_FILE"] = Path.Combine(AppContext.BaseDirectory, "fixtures", "en.wav"),
        });
        var events = new List<IpcEvent>();
        _supervisor = new DaemonSupervisor(socket, () => true, launcher, log, "dictum-gui.log");
        _supervisor.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        _supervisor.Start();
        await Eventually.WaitForAsync(() => _supervisor.Link.State == DaemonLinkState.Connected, "the daemon", TimeSpan.FromSeconds(30));

        await _supervisor.SendAsync(new IpcRequest("start", "test"), _ct);
        await Eventually.WaitForAsync(() => Has(events, e => e is { Event: "state", State: "Recording" }), "Recording");
        await _supervisor.SendAsync(new IpcRequest("cancel"), _ct);
        await Eventually.WaitForAsync(() => Has(events, e => e is { Event: "finished", Outcome: DictationOutcome.Cancelled }), "finished: cancelled");
        var test = await _supervisor.SendAsync(new IpcRequest("test-stt", Stt: "local", Sample: true), _ct, TimeSpan.FromMinutes(2));

        Assert.True(test.Ok, test.Error);
        Assert.False(string.IsNullOrWhiteSpace(test.Test!.Text));
    }

    private static bool Has(List<IpcEvent> events, Func<IpcEvent, bool> match)
    {
        lock (events)
        {
            return events.Any(match);
        }
    }

    /// <summary>The <c>dictum</c> apphost that the project reference built, in the same configuration as this test.</summary>
    private static string CliExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Dictum.slnx")))
        {
            root = root.Parent;
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var name = OperatingSystem.IsWindows() ? "dictum.exe" : "dictum";
        return Path.Combine(root!.FullName, "src", "Dictum.Cli", "bin", configuration, "net10.0", name);
    }
}
