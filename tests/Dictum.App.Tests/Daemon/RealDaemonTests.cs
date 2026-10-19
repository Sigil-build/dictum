using Dictum.App.Daemon;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.Tests.Daemon;

/// <summary>
/// The supervisor with the real <c>dictum daemon</c>, in temp folders, with the microphone replaced by a file.
/// Enabled with DICTUM_INTEGRATION=1 and DICTUM_TEST_MODEL=&lt;path to a ggml model&gt;. The profile has no hotkey,
/// so it never competes with a daemon already running on the machine, and nothing is pasted.
/// </summary>
public sealed class RealDaemonTests : IAsyncDisposable
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("DICTUM_TEST_MODEL");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TempDirectory _dir = new();
    private DaemonSupervisor? _supervisor;

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.QuitAsync();
            await _supervisor.DisposeAsync();
        }

        _dir.Dispose();
    }

    [Fact]
    public async Task EventsArriveAndTheSampleIsTranscribed()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1", "Set DICTUM_INTEGRATION=1 to run");
        Assert.SkipWhen(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set DICTUM_TEST_MODEL to a ggml model");

        var config = _dir.File("config");
        var socket = _dir.File("d.sock");
        var store = new ConfigStore(Path.Combine(config, "config.json"), NullLog.Instance);
        store.Save(new DictumConfig
        {
            Stt = { ["local"] = new WhisperLocalSttConfig { Model = Model! } },
            Profiles = [new ProfileConfig { Id = "test", Stt = "local", Language = "en", Output = OutputMode.Clipboard }],
        });
        var log = new ListLog();
        var launcher = new DaemonProcessLauncher(CliExecutable(), [DictumExecutables.DaemonVerb], log, new Dictionary<string, string>
        {
            [DictumEnvironment.ConfigDirectory] = config,
            [DictumEnvironment.DataDirectory] = _dir.File("data"),
            [DictumEnvironment.Socket] = socket,
            [DictumEnvironment.AudioFile] = Path.Combine(AppContext.BaseDirectory, "fixtures", "en.wav"),
        });
        var events = new List<IpcEvent>();
        _supervisor = new DaemonSupervisor(socket, () => true, launcher, log, AppPaths.GuiLogName);
        _supervisor.EventReceived += e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        _supervisor.Start();
        await Eventually.WaitForAsync(() => _supervisor.Link.State == DaemonLinkState.Connected, "the daemon", TimeSpan.FromSeconds(30));

        await _supervisor.SendAsync(new IpcRequest(IpcCommand.Start, "test"), _ct);
        await Eventually.WaitForAsync(() => Has(events, e => e is { Event: IpcEventKind.State, State: nameof(PipelineState.Recording) }), "Recording");
        await _supervisor.SendAsync(new IpcRequest(IpcCommand.Cancel), _ct);
        await Eventually.WaitForAsync(() => Has(events, e => e is { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Cancelled }), "finished: cancelled");
        var test = await _supervisor.SendAsync(new IpcRequest(IpcCommand.TestStt, Stt: "local", Sample: true), _ct, TimeSpan.FromMinutes(2));

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
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(Repository.Root, "src", "Dictum.Cli", "bin", configuration, "net10.0", DictumExecutables.FileName(DictumExecutables.Cli));
    }
}
