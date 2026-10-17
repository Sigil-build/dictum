using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Settings;

public sealed class SpeechProvidersPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly List<Option<string>> _choices = [new("ggml-large-v3-turbo-q8_0.bin", "Turbo"), new("ggml-small-q8_0.bin", "Small")];
    private readonly FakeChannel _channel = new();
    private readonly FakeSecrets _secrets = new();
    private readonly ListLog _log = new();
    private readonly List<(TimeSpan Wait, string? UrlOnDisk)> _waits = [];
    private FrameHub _frames = null!;

    public SpeechProvidersPageTests() => Directory.CreateDirectory(_dir);

    private ConfigStore Store => new(Path.Combine(_dir, "config.json"), NullLog.Instance);

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

    private (SpeechProvidersPage Page, SettingsSession Session) Open(DictumConfig? config = null)
    {
        Store.Save(config ?? ProfilesPageTests.Sample());
        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        _frames = new FrameHub(_clock);
        var services = new SpeechServices(_channel, _frames, _timers, _secrets,
            current => [.. _choices],
            model => model != "ggml-silero-v6.2.0.bin",
            wait =>
            {
                _waits.Add((wait, (Store.Load().Stt.GetValueOrDefault("remote") as OpenAiCompatibleSttConfig)?.BaseUrl));
                return Task.CompletedTask;
            },
            _clock, _log);
        return (new SpeechProvidersPage(session, services), session);
    }

    private static ProviderCard Card(SpeechProvidersPage page, string id) => page.Cards.Single(c => c.Id == id);

    private static T Saved<T>(SettingsSession session, string id) where T : SttConfig => (T)session.Config.Stt[id];

    [Fact]
    public void ThereIsOneCardPerProvider()
    {
        var (page, _) = Open();

        Assert.Equal(["Local Whisper · local", "Remote server · remote"], page.Cards.Select(c => c.Title));
        Assert.Equal(["Used by: Work, Home", Strings.SettingsProvidersUnused], page.Cards.Select(c => c.UsedBy));
        Assert.Equal([false, true], page.Cards.Select(c => c.CanRemove));
        Assert.True(Card(page, "local").IsLocal);
    }

    [Fact]
    public void LocalFieldsAreSaved()
    {
        var (page, session) = Open();
        var card = Card(page, "local");

        card.Model = card.Models[1];
        card.Runtime = card.Runtimes.Single(r => r.Value == WhisperRuntime.Vulkan);
        card.Threads = 4;
        card.Vad = true;

        var saved = Saved<WhisperLocalSttConfig>(session, "local");
        Assert.Equal(("ggml-small-q8_0.bin", WhisperRuntime.Vulkan, 4, true), (saved.Model, saved.Runtime, saved.Threads, saved.Vad));
        Assert.Same(card, Card(page, "local"));
    }

    [Fact]
    public void CudaIsOfferedOnlyWhenTheConfigNamesIt()
    {
        var config = ProfilesPageTests.Sample();
        var (page, _) = Open();
        Assert.DoesNotContain(Card(page, "local").Runtimes, r => r.Value == WhisperRuntime.Cuda);

        config.Stt["local"] = ((WhisperLocalSttConfig)config.Stt["local"]) with { Runtime = WhisperRuntime.Cuda };
        var (withCuda, _) = Open(config);
        Assert.Contains(Card(withCuda, "local").Runtimes, r => r.Value == WhisperRuntime.Cuda);
    }

    [Fact]
    public void AMissingVadModelIsNoted()
    {
        var (page, _) = Open();
        var card = Card(page, "local");

        card.Vad = true;

        Assert.Contains("ggml-silero-v6.2.0.bin", card.VadNote);
    }

    [Fact]
    public void RemoteFieldsAreSavedAndABadUrlIsShown()
    {
        var (page, session) = Open();
        var card = Card(page, "remote");

        card.ModelName = "large-v3";
        card.TimeoutSeconds = 90;
        Assert.Equal(("large-v3", 90_000), (Saved<OpenAiCompatibleSttConfig>(session, "remote").Model, Saved<OpenAiCompatibleSttConfig>(session, "remote").TimeoutMs));

        card.Url = "home-server";
        Assert.Equal("'home-server' is not an absolute http(s) URL", card.UrlError);
        Assert.True(session.HasErrors);
    }

    [Fact]
    public void AnEmptiedTimeoutSavesNothingItPassedThrough()
    {
        var (page, session) = Open();
        var card = Card(page, "remote");

        card.TimeoutSeconds = 6;
        card.TimeoutSeconds = null;
        session.Flush();

        Assert.Equal(60_000, ((OpenAiCompatibleSttConfig)Store.Load().Stt["remote"]).TimeoutMs);
        Assert.Equal(Strings.SettingsNumberRequired, card.TimeoutError);
    }

    // Review focus 2.
    [Fact]
    public async Task TheKeyFieldCommitsThroughTheStore()
    {
        var (page, session) = Open();
        var card = Card(page, "remote");

        card.KeyText = "sk-123";
        await card.CommitKeyCommand.ExecuteAsync(null);
        session.Flush();

        Assert.Equal("sk-123", _secrets.Values["dictum-stt-remote"]);
        Assert.Equal("secret:dictum-stt-remote", Saved<OpenAiCompatibleSttConfig>(session, "remote").ApiKey);
        Assert.Equal("", card.KeyText);
        Assert.Equal(Strings.SettingsProvidersKeyStored, card.KeyState);
        Assert.DoesNotContain("sk-123", File.ReadAllText(Store.Path));
        Assert.DoesNotContain(_log.Lines, l => l.Message.Contains("sk-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKeyTheStoreRefusesIsShownAndNotSaved()
    {
        _secrets.Fails = new ConfigException("The keyring prompt was dismissed.");
        var (page, session) = Open();
        var card = Card(page, "remote");

        card.KeyText = "sk-123";
        await card.CommitKeyCommand.ExecuteAsync(null);

        Assert.Equal("The key could not be stored: The keyring prompt was dismissed.", card.KeyError);
        Assert.Null(Saved<OpenAiCompatibleSttConfig>(session, "remote").ApiKey);
    }

    [Fact]
    public async Task RemovingTheKeyClearsTheConfigAndOurSecret()
    {
        var (page, session) = Open();
        var card = Card(page, "remote");
        card.KeyText = "sk-123";
        await card.CommitKeyCommand.ExecuteAsync(null);

        await card.RemoveKeyCommand.ExecuteAsync(null);

        Assert.Null(Saved<OpenAiCompatibleSttConfig>(session, "remote").ApiKey);
        Assert.Empty(_secrets.Values);
        Assert.Equal(Strings.SettingsProvidersNoKey, card.KeyState);
    }

    [Fact]
    public void AddedProvidersGetFreeIdsAndValidDefaults()
    {
        var (page, session) = Open();

        page.AddLocalCommand.Execute(null);
        page.AddRemoteCommand.Execute(null);

        Assert.Equal(["local", "remote", "local-2", "remote-2"], page.Cards.Select(c => c.Id));
        var local = Saved<WhisperLocalSttConfig>(session, "local-2");
        Assert.Equal(("ggml-large-v3-turbo-q8_0.bin", true), (local.Model, local.Vad));
        Assert.False(session.HasErrors);
    }

    [Fact]
    public async Task AnUnusedProviderCanBeRemovedWithItsSecret()
    {
        var (page, session) = Open();
        var remote = Card(page, "remote");
        remote.KeyText = "sk-123";
        await remote.CommitKeyCommand.ExecuteAsync(null);

        remote.RemoveCommand.Execute(null);

        Assert.False(session.Config.Stt.ContainsKey("remote"));
        Assert.Empty(_secrets.Values);
        Assert.Equal(["local"], page.Cards.Select(c => c.Id));
    }

    // Review focus 5.
    [Fact]
    public async Task ATestSavesFirstAndWaitsForTheReload()
    {
        var (page, _) = Open();
        var card = Card(page, "remote");
        card.Url = "http://gpu-box:9000/v1";

        await card.TestCommand.ExecuteAsync(null);

        Assert.Equal([(ProviderCard.ReloadWait, "http://gpu-box:9000/v1")], _waits);
        var test = _channel.Sent.Single(r => r.Command == "test-stt");
        Assert.Equal(("remote", (int?)5, (bool?)null), (test.Stt, test.Seconds, test.Sample));
        Assert.Equal(TimeSpan.FromSeconds(125), _channel.LastTimeout);
    }

    [Fact]
    public async Task ATestWithNothingToSaveStartsAtOnce()
    {
        var (page, _) = Open();

        await Card(page, "local").TestCommand.ExecuteAsync(null);

        Assert.Empty(_waits);
    }

    // Review focus 5.
    [Fact]
    public async Task ATestWaitsForErrorsToBeFixed()
    {
        var (page, _) = Open();
        var card = Card(page, "remote");
        card.Url = "home-server";

        Assert.False(card.CanTest);
        await card.TestSampleCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_channel.Commands, c => c == "test-stt");
        Assert.Equal((HealthLevel.Error, Strings.SettingsProvidersFixFirst), (card.Result?.Level, card.Result?.Status));
    }

    [Fact]
    public async Task ASuccessfulTestShowsTheTextAndTheLatency()
    {
        _channel.Answer = request => request.Command == "test-stt"
            ? new IpcResponse(true, Test: new SttTestResult("привіт, світ", "uk", 812, "Vulkan", "ggml-large-v3-turbo-q8_0.bin"))
            : new IpcResponse(true);
        var (page, _) = Open();

        await Card(page, "local").TestSampleCommand.ExecuteAsync(null);

        var result = Card(page, "local").Result;
        Assert.Equal((HealthLevel.Ok, "Recognised in 812 ms", "привіт, світ"), (result?.Level, result?.Status, result?.Detail));
        Assert.Equal((bool?)true, _channel.Sent.Single(r => r.Command == "test-stt").Sample);
    }

    [Fact]
    public async Task AFailedTestShowsTheDaemonsReason()
    {
        _channel.Answer = request => request.Command == "test-stt"
            ? IpcResponse.Failure("Dictum is busy; test the provider when the dictation or level meter has finished.")
            : new IpcResponse(true);
        var (page, _) = Open();

        await Card(page, "local").TestCommand.ExecuteAsync(null);

        Assert.Equal("Dictum is busy; test the provider when the dictation or level meter has finished.", Card(page, "local").Result?.Status);
    }

    [Fact]
    public async Task WithoutTheDaemonTheTestSaysSo()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        var (page, _) = Open();

        await Card(page, "local").TestCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SettingsProvidersNoDaemon, Card(page, "local").Result?.Status);
    }

    [Fact]
    public void TheMeterMovesWhileATestRecords()
    {
        var answer = new TaskCompletionSource<IpcResponse>();
        _channel.Held["test-stt"] = answer;
        var (page, _) = Open();
        var card = Card(page, "local");

        var run = card.TestCommand.ExecuteAsync(null);
        for (var i = 0; i < 10; i++)
        {
            _frames.Publish(new IpcEvent("frame", Rms: 0.05f));
            _clock.Advance(GeneralPage.MeterTick);
            _timers.Fire();
        }

        Assert.True(card.Testing);
        Assert.True(card.MeterLevel > 0.5);

        answer.SetResult(new IpcResponse(true, Test: new SttTestResult("", null, 300, null, null)));
        Assert.True(run.IsCompleted);
        Assert.False(card.Testing);
        Assert.Equal(0, card.MeterLevel);
        Assert.Equal(Strings.SettingsProvidersNothingHeard, card.Result?.Detail);
        Assert.Equal(0, _timers.Pending);
    }


    // Final review I1: a debounced save just before the click needs the rest of the reload time too.
    [Fact]
    public async Task ATestRightAfterAnAutomaticSaveWaitsTheRest()
    {
        var (page, _) = Open();
        var card = Card(page, "remote");
        card.Url = "http://gpu-box:9000/v1";
        _timers.Fire();
        _clock.Advance(TimeSpan.FromMilliseconds(500));

        await card.TestCommand.ExecuteAsync(null);

        Assert.Equal([(TimeSpan.FromSeconds(1), "http://gpu-box:9000/v1")], _waits);
    }

    [Fact]
    public async Task ATestLongAfterTheLastSaveStartsAtOnce()
    {
        var (page, _) = Open();
        var card = Card(page, "remote");
        card.Url = "http://gpu-box:9000/v1";
        _timers.Fire();
        _clock.Advance(TimeSpan.FromSeconds(2));

        await card.TestCommand.ExecuteAsync(null);

        Assert.Empty(_waits);
    }

    // Final review I1: a key still being stored (a keyring prompt) is part of the settings the test must use.
    [Fact]
    public async Task ATestWaitsForAKeyStillBeingStored()
    {
        string? keyAtTest = null;
        _channel.Answer = request =>
        {
            if (request.Command == "test-stt")
            {
                keyAtTest = ((OpenAiCompatibleSttConfig)Store.Load().Stt["remote"]).ApiKey;
            }

            return new IpcResponse(true);
        };
        var held = new TaskCompletionSource();
        _secrets.Held = held;
        var (page, _) = Open();
        var card = Card(page, "remote");
        card.KeyText = "sk-123";

        var commit = card.CommitKeyCommand.ExecuteAsync(null);
        var test = card.TestCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_channel.Commands, c => c == "test-stt");

        held.SetResult();
        await commit;
        await test;

        Assert.Equal("secret:dictum-stt-remote", keyAtTest);
    }

    // PR 4d: a model downloaded on Settings → Models appears here without a config change.
    [Fact]
    public void ShowingThePageListsModelsDownloadedSinceItOpened()
    {
        var (page, _) = Open();
        _choices.Add(new("ggml-medium-q8_0.bin", "Medium"));

        page.OnShown();

        Assert.Contains("Medium", Card(page, "local").Models.Select(m => m.Label));
    }
}
