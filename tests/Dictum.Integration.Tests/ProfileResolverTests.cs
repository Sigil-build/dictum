using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Host;

namespace Dictum.Integration.Tests;

public class ProfileResolverTests
{
    private static readonly DictumConfig Config = new()
    {
        Stt = new()
        {
            ["local"] = new WhisperLocalSttConfig { Model = "ggml-base.bin" },
            ["remote"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:8080/v1", Model = "m" },
        },
        Processors = new()
        {
            ["terms"] = new ReplaceProcessorConfig(),
            ["cleanup"] = new ReplaceProcessorConfig(),
        },
        Profiles =
        [
            new ProfileConfig { Id = "uk", Stt = "local", Language = "uk", SttPrompt = "Avalonia", Pipeline = ["terms", "cleanup"] },
            new ProfileConfig { Id = "en", Stt = "remote", Language = "en" },
        ],
    };

    [Fact]
    public void DefaultsToTheFirstProfile()
    {
        var profile = Resolve(new ProfileOverrides());

        Assert.Equal("uk", profile.Id);
        Assert.Equal("local", profile.Stt.Id);
        Assert.Equal(new SttRequest("uk", "Avalonia"), profile.Request);
        Assert.Equal(["terms", "cleanup"], profile.Processors.Select(p => p.Id));
    }

    [Fact]
    public void OverridesWinOverTheProfile()
    {
        var profile = Resolve(new ProfileOverrides(Profile: "uk", Stt: "remote", Language: "auto", Pipeline: ["cleanup"]));

        Assert.Equal("remote", profile.Stt.Id);
        Assert.Equal("auto", profile.Request.Language);
        Assert.Equal(["cleanup"], profile.Processors.Select(p => p.Id));
    }

    [Fact]
    public void RawSkipsProcessing() =>
        Assert.Empty(Resolve(new ProfileOverrides(Raw: true)).Processors);

    [Fact]
    public void ModelCreatesAnAdHocWhisperProvider()
    {
        var profile = Resolve(new ProfileOverrides(Model: "/models/ggml-tiny.en.bin"));

        Assert.Equal("whisper:ggml-tiny.en.bin", profile.Stt.Id);
    }

    [Fact]
    public void WorksWithoutAnyConfig()
    {
        var profile = ProfileResolver.Resolve(new DictumConfig(), new ProfileOverrides(Model: "ggml-base.bin"), Stt, Processor, AdHoc);

        Assert.Equal(ProfileResolver.AdHocProfileId, profile.Id);
        Assert.Empty(profile.Processors);
    }

    [Theory]
    [InlineData("missing", null, null)]
    [InlineData(null, null, "Ukrainian")]
    public void InvalidOverridesAreConfigErrors(string? profile, string? stt, string? language) =>
        Assert.Throws<ConfigException>(() => Resolve(new ProfileOverrides(Profile: profile, Stt: stt, Language: language)));

    [Fact]
    public void NoProviderAtAllExplainsWhatToDo()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ProfileResolver.Resolve(new DictumConfig(), new ProfileOverrides(), Stt, Processor, AdHoc));

        Assert.Contains("--model", ex.Message);
    }

    private static Core.Pipeline.DictationProfile Resolve(ProfileOverrides overrides) =>
        ProfileResolver.Resolve(Config, overrides, Stt, Processor, AdHoc);

    private static ISpeechToText Stt(string id) => new NamedStt(id);

    private static ISpeechToText AdHoc(string id, SttConfig config) => new NamedStt(id);

    private static ITextProcessor Processor(string id) => new NamedProcessor(id);

    private sealed class NamedStt(string id) : ISpeechToText
    {
        public string Id => id;

        public SttCapabilities Capabilities { get; } = new(true, true, false);

        public Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HealthStatus> CheckAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NamedProcessor(string id) : ITextProcessor
    {
        public string Id => id;

        public Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct) =>
            Task.FromResult(text);
    }
}
