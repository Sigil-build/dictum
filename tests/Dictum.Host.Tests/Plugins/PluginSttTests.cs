using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
using Dictum.Core.Stt;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Host.Tests.Plugins;

/// <summary>
/// The plugin speech provider over a confirmed copy of the test plugin, through a real plugin host and resolver. The
/// test plugin takes its behaviour from the model, a <c>.json</c> file, and reports its id as the file's name.
/// </summary>
public sealed class PluginSttTests : IAsyncDisposable
{
    /// <summary>A tenth of a second of silence.</summary>
    private static readonly AudioClip Clip = new(new float[AudioClip.WhisperSampleRate / 10]);

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();
    private readonly PluginTrustStore _trust;
    private readonly PluginHost _host;
    private readonly List<PluginStt> _providers = [];

    public PluginSttTests()
    {
        _trust = new PluginTrustStore(_dir.File("plugin-trust.json"), _log);
        _host = new PluginHost(_trust, PlatformFactory.CreateProcessGroups(), _log);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        await _host.DisposeAsync();
        _dir.Dispose();
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task OneLanguageIsSentAsLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = await Provider(Echo()).TranscribeAsync(Clip, new SttRequest("uk", null), ct);

        Assert.StartsWith("language=uk;languages=;", transcript.Text);
        Assert.Equal("uk", transcript.Language);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AListIsSentAsLanguages()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = await Provider(Echo()).TranscribeAsync(Clip, new SttRequest(null, null, ["uk", "en"]), ct);

        Assert.StartsWith("language=;languages=uk,en;", transcript.Text);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AutoSendsNoLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = await Provider(Echo()).TranscribeAsync(Clip, new SttRequest(LanguageList.Auto, null), ct);

        Assert.StartsWith("language=;languages=;", transcript.Text);
        Assert.Null(transcript.Language);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task APromptIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = await Provider(Echo()).TranscribeAsync(Clip, new SttRequest("uk", "Dictum, Kyiv"), ct);

        Assert.Contains("prompt=Dictum, Kyiv;", transcript.Text);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheModelFieldIsTheConfiguredModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = Echo();

        var transcript = await Provider(config).TranscribeAsync(Clip, new SttRequest("uk", null), ct);

        Assert.Contains($";model={config.Model};", transcript.Text);
        Assert.EndsWith($";bytes={Wav.ToPcm16Bytes(Clip).Length}", transcript.Text);
    }

    /// <summary>Protocol 1's form asks for JSON, and sends the recording as a WAV file named audio.wav.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheFormAsksForJsonAndSendsAWavFile()
    {
        var ct = TestContext.Current.CancellationToken;

        var transcript = await Provider(Echo()).TranscribeAsync(Clip, new SttRequest("uk", null), ct);

        Assert.Contains(";response_format=json;", transcript.Text);
        Assert.Contains(";file=audio.wav;", transcript.Text);
        Assert.Contains(";type=audio/wav;", transcript.Text);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ThePluginsLanguageWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = await Provider(Config(new TestPluginBehaviour { Language = "de" })).TranscribeAsync(Clip, new SttRequest("uk", null), ct);

        Assert.Equal("de", transcript.Language);
        Assert.Equal("canned text", transcript.Text);
    }

    /// <summary>The model is the plugin's own name for it: the test plugin names it after the file.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheTranscriptNamesThePluginAndItsModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = await Provider(Config(new TestPluginBehaviour(), "whisper-small.json")).TranscribeAsync(Clip, new SttRequest("uk", null), ct);

        Assert.Equal("plugin test", transcript.Backend);
        Assert.Equal("whisper-small", transcript.Model);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnErrorAnswerIsProviderUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Config(new TestPluginBehaviour { TranscribeStatus = 500 }));

        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TranscribeAsync(Clip, new SttRequest("uk", null), ct));

        Assert.StartsWith("Plugin provider 'p' answered 500", error.Message);
    }

    /// <summary>
    /// Review focus 4: what the plugin answered is its own text, which <c>dictum transcribe</c> prints and IPC passes on,
    /// so the error spells it out, whether the answer is an error or not JSON.
    /// </summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData(500, @"Plugin provider 'p' answered 500 Internal Server Error: \u001b]0;PWNED\u0007")]
    [InlineData(null, @"Plugin provider 'p' returned invalid JSON: \u001b]0;PWNED\u0007")]
    public async Task WhatThePluginAnsweredIsSpeltOut(int? status, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Config(new TestPluginBehaviour { TranscribeStatus = status, TranscribeBody = "\u001b]0;PWNED\u0007" }));

        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TranscribeAsync(Clip, new SttRequest("uk", null), ct));

        Assert.Equal(message, error.Message);
    }

    /// <summary>The reason phrase of an error answer is the plugin's text too; .NET reads any byte past ASCII as a character.</summary>
    [Fact]
    public void TheReasonOfAnErrorAnswerIsSpeltOut()
    {
        var answer = new ServerAnswer(System.Net.HttpStatusCode.BadGateway, "Bad\u009b2J\u001b]0;PWNED\u0007", "{}");

        Assert.Equal(@"Plugin provider 'p' answered 502 Bad\u009b2J\u001b]0;PWNED\u0007: {}", PluginStt.ErrorAnswer("Plugin provider 'p'", answer));
    }

    /// <summary>The connection drops before any answer; the caller gets Dictum's provider error, not the HTTP client's.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task APluginThatDiesMidRequestIsProviderUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Config(new TestPluginBehaviour { ExitOnTranscribe = true }));

        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TranscribeAsync(Clip, new SttRequest("uk", null), ct));

        Assert.StartsWith("Plugin provider 'p' is unreachable at http://127.0.0.1:", error.Message);
    }

    /// <summary>A plugin that cannot start says why, from where it was asked to transcribe.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AStartFailureReachesTheCallerAsIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Config(new TestPluginBehaviour { ExitAtStart = 3 }));

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => provider.TranscribeAsync(Clip, new SttRequest("uk", null), ct));

        Assert.Equal("Plugin test exited with code 3 while starting.", error.Message);
        Assert.NotNull(error.Hint);
    }

    /// <summary>A dictation still holding a provider whose plugin Dictum has stopped, as at shutdown.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AStoppedPluginIsProviderUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Echo());
        Assert.Equal(HealthLevel.Ok, (await provider.CheckAsync(ct)).Level);

        await _host.DisposeAsync();
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.TranscribeAsync(Clip, new SttRequest("uk", null), ct));

        Assert.Equal("Plugin provider 'p' was stopped while in use.", error.Message);
        Assert.IsType<ObjectDisposedException>(error.InnerException);
    }

    /// <summary>
    /// A reload lets go of the provider that a check is about to ask. The check throws, as a disposed engine's does, so
    /// that doctor asks the provider that replaced it, rather than reporting one that no longer serves.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ACheckOnAProviderAReloadLetGoOfThrowsSoTheNextOneIsAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Echo());

        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.CheckAsync(ct));
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task CheckReportsTheRunningPlugin()
    {
        var ct = TestContext.Current.CancellationToken;
        var status = await Provider(Echo()).CheckAsync(ct);

        Assert.Equal(HealthLevel.Ok, status.Level);
        Assert.StartsWith("plugin Test 1.0.0, model", status.Detail);
        Assert.EndsWith($", pid {Assert.Single(_host.Instances).ProcessId}", status.Detail);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task CheckShowsWhyItCannotStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var status = await Provider(Config(new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["no CUDA here"] })).CheckAsync(ct);

        Assert.Equal(HealthLevel.Error, status.Level);
        Assert.Contains("exited with code 3", status.Detail);
        Assert.EndsWith(" while starting; last output: no CUDA here", status.Detail);
        Assert.NotNull(status.Hint);
    }

    /// <summary>A plugin that is not added cannot start either; the provider still builds, as a missing Whisper model does.</summary>
    [Fact]
    public async Task CheckShowsAnUnaddedPlugin()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new PluginSttConfig { Plugin = "absent", Model = _dir.File("model.json") };

        var status = await Provider(config).CheckAsync(ct);

        Assert.Equal(HealthStatus.Error("Plugin 'absent' is not added.", "Add it with 'dictum plugins add <folder>'."), status);
    }

    /// <summary>
    /// Whatever else goes wrong is the provider's error row too, never an exception out of the check: a warm-up goes
    /// on to the next provider. Here a registered folder that no path can name.
    /// </summary>
    [Fact]
    public async Task CheckReportsAnUnexpectedFailureAsAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new PluginResolver(
            new AppPaths(_dir.Path, _dir.Path),
            new Dictionary<string, PluginRegistration> { ["test"] = new() { Path = _dir.File("bad\0name") } });
        var provider = new PluginStt(
            "p", new PluginSttConfig { Plugin = "test", Model = _dir.File("model.json") }, resolver, _host, new ConfigValueResolver(new FakeSecrets()), _log);
        _providers.Add(provider);

        var status = await provider.CheckAsync(ct);

        Assert.Equal(HealthLevel.Error, status.Level);
        Assert.StartsWith("Plugin provider 'p' could not start: ", status.Detail);
        Assert.Contains("Null character in path", status.Detail);
        Assert.Contains(_log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("check failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A daemon's warm-up reports a status and throws nothing, so the log is where an unattended machine shows that a
    /// plugin cannot start: at Warning, whoever else reports it.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AWarmUpOfAPluginThatCannotStartLeavesAWarningInTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir);
        var model = TestPlugins.Model(_dir, new TestPluginBehaviour());
        var resolver = new PluginResolver(
            new AppPaths(_dir.Path, _dir.Path),
            new Dictionary<string, PluginRegistration> { ["test"] = new() { Path = folder } });
        var provider = new PluginStt(
            "p", new PluginSttConfig { Plugin = "test", Model = model }, resolver, _host, new ConfigValueResolver(new FakeSecrets()), _log);
        _providers.Add(provider);

        var status = await provider.CheckAsync(ct);

        Assert.Equal(HealthLevel.Error, status.Level);
        Assert.Contains(_log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("has not been confirmed", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task DisposingTheProviderReleasesItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = Provider(Echo());
        await provider.CheckAsync(ct);
        await _host.StopUnusedAsync();
        Assert.Single(_host.Instances);

        provider.Dispose();
        await _host.StopUnusedAsync();

        Assert.Empty(_host.Instances);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    private PluginSttConfig Echo() => Config(new TestPluginBehaviour { Echo = true });

    /// <summary>The test plugin's folder, confirmed, and a provider config whose model file holds the behaviour.</summary>
    private PluginSttConfig Config(TestPluginBehaviour behaviour, string modelName = "model.json") =>
        new() { Plugin = "test", Model = TestPlugins.Model(_dir, behaviour, modelName) };

    private PluginStt Provider(PluginSttConfig config)
    {
        var folder = TestPlugins.Folder(_dir);
        var launch = TestPlugins.Launch(folder);
        _trust.Confirm(launch.PluginId, folder, launch.Executable);
        var resolver = new PluginResolver(
            new AppPaths(_dir.Path, _dir.Path),
            new Dictionary<string, PluginRegistration> { ["test"] = new() { Path = folder } });
        var provider = new PluginStt("p", config, resolver, _host, new ConfigValueResolver(new FakeSecrets()), _log);
        _providers.Add(provider);
        return provider;
    }
}
