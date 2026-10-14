using System.Net;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Stt;
using Dictum.Stt.OpenAi;

namespace Dictum.Core.Tests.Stt;

public class OpenAiCompatibleSttTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendsMultipartRequestAndParsesText()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{ "text": " Привіт, світ. " }""");
        var stt = Create(http, apiKey: "sk-test");

        var transcript = await stt.TranscribeAsync(Clips.Seconds(1), new SttRequest("uk", "Avalonia, .NET"), _ct);

        Assert.Equal(" Привіт, світ. ", transcript.Text);
        Assert.Equal("uk", transcript.Language);
        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:8080/v1/audio/transcriptions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString());
        Assert.Contains("name=file; filename=audio.wav", body);
        Assert.Contains("RIFF", body);
        Assert.Contains("whisper-large-v3-turbo", body);
        Assert.Contains("name=language", body);
        Assert.Contains("Avalonia, .NET", body);
    }

    [Fact]
    public async Task AutoLanguageIsNotSent()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{ "text": "hi", "language": "en" }""");

        var transcript = await Create(http).TranscribeAsync(Clips.Seconds(1), new SttRequest("auto", null), _ct);

        Assert.DoesNotContain("name=language", http.Requests[0].Body);
        Assert.Null(http.Requests[0].Request.Headers.Authorization);
        Assert.Equal("en", transcript.Language);
    }

    [Fact]
    public async Task ALanguageListSendsNoLanguage()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{ "text": "hi", "language": "en" }""");

        var transcript = await Create(http).TranscribeAsync(Clips.Seconds(1), new SttRequest(null, null, ["uk", "en"]), _ct);

        Assert.DoesNotContain("name=language", http.Requests[0].Body);
        Assert.Equal("en", transcript.Language);
    }

    [Fact]
    public async Task HttpErrorBecomesProviderUnavailable()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.BadGateway, """{ "error": "model not loaded" }""");

        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Create(http).TranscribeAsync(Clips.Seconds(1), new SttRequest(null, null), _ct));

        Assert.Contains("502", ex.Message);
        Assert.Contains("model not loaded", ex.Message);
    }

    [Fact]
    public async Task ConnectionFailureBecomesProviderUnavailable()
    {
        var http = new FakeHttpHandler().Throw(new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Create(http).TranscribeAsync(Clips.Seconds(1), new SttRequest(null, null), _ct));

        Assert.Contains("unreachable", ex.Message);
    }

    [Fact]
    public async Task CheckUsesModelsEndpoint()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{ "data": [] }""");

        var status = await Create(http).CheckAsync(_ct);

        Assert.Equal(HealthLevel.Ok, status.Level);
        Assert.EndsWith("/v1/models", http.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task CheckFallsBackToATranscriptionWhenModelsIsMissing()
    {
        var http = new FakeHttpHandler()
            .Respond(HttpStatusCode.NotFound, "{}")
            .Respond(HttpStatusCode.OK, """{ "text": "" }""");

        var status = await Create(http).CheckAsync(_ct);

        Assert.Equal(HealthLevel.Ok, status.Level);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task CheckReportsRejectedKey()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, "{}");

        var status = await Create(http, apiKey: "sk-bad").CheckAsync(_ct);

        Assert.Equal(HealthLevel.Error, status.Level);
        Assert.NotNull(status.Hint);
    }

    private static OpenAiCompatibleStt Create(FakeHttpHandler http, string? apiKey = null) =>
        new(
            "llama-swap",
            new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:8080/v1/", Model = "whisper-large-v3-turbo", ApiKey = apiKey },
            http.Client(),
            new ConfigValueResolver(new UnavailableSecretStore()),
            NullLog.Instance);
}
