using System.Net.Sockets;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Host.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Host.Tests.Daemon;
using Dictum.Platform;
using Dictum.Processing;
using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Host.Tests.Diagnostics;

public sealed class DoctorChecksTests : IDisposable
{
    /// <summary>Short, so a provider or a daemon that never answers fails its row quickly.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AMissingCatalogModelIsAnErrorWithAPullHint()
    {
        var checks = await DoctorChecks.RunAsync(Inputs(Config("ggml-large-v3-turbo-q8_0.bin")), _ct);

        var models = checks.Single(c => c.Name == "models").Status;
        Assert.Equal(HealthLevel.Error, models.Level);
        Assert.Contains("dictum models pull whisper-large-v3-turbo-q8", models.Hint);
    }

    // An unplugged drive: the row names the folder and what to do, not a list of every file it no longer finds.
    [Fact]
    public async Task AMissingDownloadsFolderIsAnErrorBeforeTheFileList()
    {
        var gone = _dir.File("gone");
        var inputs = Inputs(Config("ggml-large-v3-turbo-q8_0.bin")) with { Paths = new AppPaths(_dir.Path, _dir.Path).WithDownloads(gone) };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthStatus.Error(DownloadsFolder.MissingError(gone), DownloadsFolder.MissingHint), checks.Single(c => c.Name == "models").Status);
    }

    [Fact]
    public async Task ConfigWarningsAndTheGpuGetTheirOwnRows()
    {
        var inputs = Inputs(Config("m.bin")) with
        {
            ConfigWarnings = [new ConfigIssue(ConfigIssueSeverity.Warning, "profiles[0].language", "sounds alike")],
            Hardware = new HardwareInfo("Intel(R) Iris(R) Xe Graphics", 0, 16_965_201_920, GpuIsIntegrated: true),
        };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthLevel.Warning, checks.Single(c => c.Name == "config warnings").Status.Level);
        Assert.Contains("15.8 GB shared memory", checks.Single(c => c.Name == "gpu").Status.Detail);
    }

    // "The LLM does nothing" is what doctor is for: a processor that no profile runs shows in the config warnings row.
    [Fact]
    public async Task AProcessorNoProfileRunsIsAConfigWarning()
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        var config = Config("m.bin");
        config.Processors["llm"] = new ReplaceProcessorConfig();
        store.Save(config);
        var (loaded, warnings) = store.LoadWithIssues();
        var inputs = Inputs(loaded) with { ConfigWarnings = warnings, GetProcessor = id => new ReplaceProcessor(id, new ReplaceProcessorConfig(), NullLog.Instance) };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        var row = checks.Single(c => c.Name == PlatformCheckNames.ConfigWarnings).Status;
        Assert.Equal(HealthLevel.Warning, row.Level);
        Assert.Equal("processors.llm: no profile's pipeline runs it, so dictation never uses it", row.Hint);
    }

    [Fact]
    public async Task AProviderThatNeverAnswersTimesOut()
    {
        var inputs = Inputs(Config("m.bin")) with { GetStt = _ => new SilentStt(), CheckTimeout = ShortTimeout };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Contains("No answer within", checks.Single(c => c.Name == "stt:s").Status.Detail);
    }

    // A plugin's check may have to start it, which takes far longer than asking a server. The row returns as soon as
    // the check does, so the long limit costs nothing and leaves a busy runner room.
    [Fact]
    public async Task APluginRowGetsItsStartTimeout()
    {
        var config = new DictumConfig
        {
            Stt =
            {
                ["p"] = new PluginSttConfig { Plugin = "test", Model = "m1" },
                ["w"] = new WhisperLocalSttConfig { Model = "m.bin" },
            },
        };
        var inputs = Inputs(config) with
        {
            GetStt = _ => new SlowStt(TimeSpan.FromMilliseconds(500)),
            CheckTimeout = TimeSpan.FromMilliseconds(100),
            PluginCheckTimeout = TimeSpan.FromSeconds(30),
        };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthLevel.Ok, checks.Single(c => c.Name == PlatformCheckNames.Stt("p")).Status.Level);
        Assert.Contains("No answer within", checks.Single(c => c.Name == PlatformCheckNames.Stt("w")).Status.Detail);
    }

    // Ledger 90: a plugin's row may wait out the plugin's whole start, and the System page waits 2 minutes for the whole
    // doctor. The plugin rows wait side by side, so plugins that hang take one timeout between them, not one each. Here
    // plugin a's check hangs until plugin b's has begun: checked one after the other, a would wait out its whole timeout.
    [Fact]
    public async Task TwoHangingPluginsTakeOneTimeout()
    {
        var bChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new DictumConfig
        {
            Stt =
            {
                ["a"] = new PluginSttConfig { Plugin = "a", Model = "m1" },
                ["b"] = new PluginSttConfig { Plugin = "b", Model = "m1" },
            },
        };
        var inputs = Inputs(config) with
        {
            GetStt = id => new CheckedStt(id == "a"
                ? async ct =>
                {
                    await bChecked.Task.WaitAsync(ct);
                    return HealthStatus.Ok("a answered");
                }
                : _ =>
                {
                    bChecked.TrySetResult();
                    return Task.FromResult(HealthStatus.Ok("b answered"));
                }),
            PluginCheckTimeout = TimeSpan.FromSeconds(30),
        };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthStatus.Ok("a answered"), checks.Single(c => c.Name == PlatformCheckNames.Stt("a")).Status);
        Assert.Equal(HealthStatus.Ok("b answered"), checks.Single(c => c.Name == PlatformCheckNames.Stt("b")).Status);
    }

    // A reload disposes the engine a check has just reached, and the next call reaches the new one: the row says what the
    // new engine finds, not that the old one is gone.
    [Fact]
    public async Task AnEngineThatAReloadDisposedUnderACheckIsCheckedAgainOnTheNewOne()
    {
        var asked = 0;
        var inputs = Inputs(Config("m.bin")) with
        {
            GetStt = _ => Interlocked.Increment(ref asked) == 1
                ? throw new ObjectDisposedException("Dictum.Host.DictumHost")
                : new CheckedStt(_ => Task.FromResult(HealthStatus.Ok("answered"))),
        };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthStatus.Ok("answered"), checks.Single(c => c.Name == PlatformCheckNames.Stt("s")).Status);
    }

    [Fact]
    public async Task AProviderThatAReloadDisposedDuringItsCheckIsCheckedAgainOnTheNewOne()
    {
        var asked = 0;
        var inputs = Inputs(Config("m.bin")) with
        {
            GetStt = _ => new CheckedStt(_ => Interlocked.Increment(ref asked) == 1
                ? throw new ObjectDisposedException("whisper")
                : Task.FromResult(HealthStatus.Ok("answered"))),
        };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthStatus.Ok("answered"), checks.Single(c => c.Name == PlatformCheckNames.Stt("s")).Status);
    }

    [Fact]
    public async Task AProcessorThatAReloadDisposedDuringItsCheckIsCheckedAgainOnTheNewOne()
    {
        var config = Config("m.bin");
        config.Processors["clean"] = new ReplaceProcessorConfig();
        var fresh = new CheckedProcessor(_ => Task.FromResult(HealthStatus.Ok("answered")));
        ITextProcessor current = fresh;
        current = new CheckedProcessor(_ =>
        {
            current = fresh;
            throw new ObjectDisposedException("llm");
        });
        var inputs = Inputs(config) with { GetProcessor = _ => current };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthStatus.Ok("answered"), checks.Single(c => c.Name == PlatformCheckNames.Processor("clean")).Status);
    }

    // Once more, not for as long as it takes: an engine that is gone again says so in its row, and the other rows follow.
    [Fact]
    public async Task AnEngineGoneTwiceIsAnErrorRow()
    {
        var asked = 0;
        var inputs = Inputs(Config("m.bin")) with
        {
            GetStt = _ =>
            {
                Interlocked.Increment(ref asked);
                throw new ObjectDisposedException("Dictum.Host.DictumHost");
            },
        };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthLevel.Error, checks.Single(c => c.Name == PlatformCheckNames.Stt("s")).Status.Level);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task ADaemonThatTakesTheConnectionButNeverAnswersIsAnErrorRow()
    {
        var socket = _dir.File("d.sock");
        await using var silent = new IpcServer(socket, NullLog.Instance);
        silent.Start(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return IpcResponse.Failure("never sent");
        });

        var row = await DoctorChecks.CheckDaemonAsync(socket, _ct, ShortTimeout);

        Assert.Equal(HealthLevel.Error, row.Level);
        Assert.Contains("not answering", row.Detail);
    }

    [Fact]
    public async Task ADaemonRowThatFailsIsAnErrorRowAndTheOtherRowsStay()
    {
        var inputs = Inputs(Config("m.bin")) with { Daemon = _ => throw new SocketException((int)SocketError.ConnectionReset) };

        var checks = await DoctorChecks.RunAsync(inputs, _ct);

        Assert.Equal(HealthLevel.Error, checks.Single(c => c.Name == PlatformCheckNames.Daemon).Status.Level);
        Assert.Contains(checks, c => c.Name == PlatformCheckNames.Stt("s"));
    }

    private DoctorInputs Inputs(DictumConfig config) => new(
        _dir.File("config.json"),
        config,
        null,
        [],
        new AppPaths(_dir.Path, _dir.Path),
        new PlatformServices("test", null, new FakeInjector(), new UnavailableSecretStore(), null, new FakeNotifier(), _ => Task.FromResult<IReadOnlyList<PlatformCheck>>([])),
        new HardwareInfo(null, long.MaxValue),
        () => new FakeAudioSource(),
        _ => new EchoStt("ok"),
        id => throw new ConfigException($"Unknown processor '{id}'."),
        _ => Task.FromResult(HealthStatus.Ok("running")));

    private static DictumConfig Config(string model) => new()
    {
        Stt = new() { ["s"] = new WhisperLocalSttConfig { Model = model } },
        Profiles = [new ProfileConfig { Id = "p", Stt = "s" }],
    };

    private sealed class SlowStt(TimeSpan delay) : ISpeechToText
    {
        public string Id => "slow";

        public Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct) => throw new NotSupportedException();

        public async Task<HealthStatus> CheckAsync(CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return HealthStatus.Ok("answered");
        }
    }

    /// <summary>A provider whose check is <paramref name="check"/>.</summary>
    private sealed class CheckedStt(Func<CancellationToken, Task<HealthStatus>> check) : ISpeechToText
    {
        public string Id => "checked";

        public Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<HealthStatus> CheckAsync(CancellationToken ct) => check(ct);
    }

    /// <summary>A processor whose check is <paramref name="check"/>.</summary>
    private sealed class CheckedProcessor(Func<CancellationToken, Task<HealthStatus>> check) : ITextProcessor, IHealthCheck
    {
        public string Id => "checked";

        public Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct) => throw new NotSupportedException();

        public Task<HealthStatus> CheckAsync(CancellationToken ct) => check(ct);
    }

    private sealed class SilentStt : ISpeechToText
    {
        public string Id => "silent";

        public Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct) => throw new NotSupportedException();

        public async Task<HealthStatus> CheckAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return HealthStatus.Ok("never");
        }
    }
}
