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

    /// <summary>
    /// The server takes one language, and the first of a list has priority: left to detect any, a server answered
    /// Ukrainian speech in Russian.
    /// </summary>
    [Theory]
    [InlineData("uk, en", "uk")]
    [InlineData("en, uk", "en")]
    [InlineData("uk, en, pl", "uk")]
    [InlineData("uk", "uk")]
    [InlineData("auto", null)]
    [InlineData(null, null)]
    public async Task TheServerGetsTheProfilesLanguageOrTheFirstOfItsList(string? setting, string? sent)
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{ "text": "hi" }""");

        await Create(http).TranscribeAsync(Clips.Seconds(1), SttRequest.ForSetting(setting, null)!, _ct);

        Assert.Equal(sent, FormField(http.Requests[0].Body, "language"));
    }

    [Fact]
    public async Task AListIsTranscribedInItsFirstLanguageUnlessTheServerNamesAnother()
    {
        var http = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{ "text": "hi" }""")
            .Respond(HttpStatusCode.OK, """{ "text": "hi", "language": "en" }""");
        var stt = Create(http);
        var list = new SttRequest(null, null, ["uk", "en"]);

        Assert.Equal("uk", (await stt.TranscribeAsync(Clips.Seconds(1), list, _ct)).Language);
        Assert.Equal("en", (await stt.TranscribeAsync(Clips.Seconds(1), list, _ct)).Language);
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

    [Fact]
    public async Task FailuresNameTheProviderAndTheServer()
    {
        Assert.Equal("STT provider 'llama-swap' answered 502 Bad Gateway: { \"error\": \"model not loaded\" }",
            await FailureAsync(new FakeHttpHandler().Respond(HttpStatusCode.BadGateway, """{ "error": "model not loaded" }""")));
        Assert.Equal($"STT provider 'llama-swap' answered 500 Internal Server Error: {new string('x', 300)}…",
            await FailureAsync(new FakeHttpHandler().Respond(HttpStatusCode.InternalServerError, new string('x', 301))));
        Assert.Equal("STT provider 'llama-swap' returned invalid JSON: not json",
            await FailureAsync(new FakeHttpHandler().Respond(HttpStatusCode.OK, "not json")));
        Assert.Equal("STT provider 'llama-swap' is unreachable at http://localhost:8080/v1/: Connection refused",
            await FailureAsync(new FakeHttpHandler().Throw(new HttpRequestException("Connection refused"))));
        Assert.Equal("STT provider 'llama-swap' did not answer within 50 ms.",
            await FailureAsync(new FakeHttpHandler().Hang(), timeoutMs: 50));
    }

    [Fact]
    public async Task TheCheckAsksForTheModelsWithTheKey()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{ "data": [] }""");

        var status = await Create(http, apiKey: "sk-test").CheckAsync(_ct);

        Assert.Equal(HealthStatus.Ok("http://localhost:8080/v1/ reachable, model whisper-large-v3-turbo"), status);
        var (request, _) = Assert.Single(http.Requests);
        Assert.Equal(
            (HttpMethod.Get, "http://localhost:8080/v1/models", "Bearer sk-test"),
            (request.Method, request.RequestUri!.ToString(), request.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task TheCheckSaysWhatTheServerAnswered()
    {
        const string hint = "Is the server running, and is baseUrl correct (including /v1)?";

        Assert.Equal(
            HealthStatus.Error("http://localhost:8080/v1/ rejected the API key (401)", "Check the apiKey reference in the config."),
            await CheckAsync(new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, "{}")));
        Assert.Equal(
            HealthStatus.Error("http://localhost:8080/v1/ rejected the API key (403)", "Check the apiKey reference in the config."),
            await CheckAsync(new FakeHttpHandler().Respond(HttpStatusCode.Forbidden, "{}")));
        Assert.Equal(
            HealthStatus.Ok("http://localhost:8080/v1/ transcribes with model whisper-large-v3-turbo"),
            await CheckAsync(new FakeHttpHandler().Respond(HttpStatusCode.NotFound, "{}").Respond(HttpStatusCode.OK, """{ "text": "" }""")));
        Assert.Equal(
            HealthStatus.Error("http://localhost:8080/v1/: STT provider 'llama-swap' answered 500 Internal Server Error: {}", hint),
            await CheckAsync(new FakeHttpHandler().Respond(HttpStatusCode.NotFound, "{}").Respond(HttpStatusCode.InternalServerError, "{}")));
        Assert.Equal(
            HealthStatus.Error("http://localhost:8080/v1/: Connection refused", hint),
            await CheckAsync(new FakeHttpHandler().Throw(new HttpRequestException("Connection refused"))));
    }

    private async Task<string> FailureAsync(FakeHttpHandler http, int timeoutMs = OpenAiCompatibleSttConfig.DefaultTimeoutMs) =>
        (await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Create(http, timeoutMs: timeoutMs).TranscribeAsync(Clips.Seconds(1), new SttRequest(null, null), _ct))).Message;

    private Task<HealthStatus> CheckAsync(FakeHttpHandler http) => Create(http).CheckAsync(_ct);

    /// <summary>The value of a multipart form field, or null when the form has no such field.</summary>
    private static string? FormField(string body, string name)
    {
        var header = body.IndexOf($"name={name}\r\n", StringComparison.Ordinal);
        if (header < 0)
        {
            return null;
        }

        var start = body.IndexOf("\r\n\r\n", header, StringComparison.Ordinal) + 4;
        return body[start..body.IndexOf("\r\n", start, StringComparison.Ordinal)];
    }

    private static OpenAiCompatibleStt Create(FakeHttpHandler http, string? apiKey = null, int timeoutMs = OpenAiCompatibleSttConfig.DefaultTimeoutMs) =>
        new(
            "llama-swap",
            new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:8080/v1/", Model = "whisper-large-v3-turbo", ApiKey = apiKey, TimeoutMs = timeoutMs },
            http.Client(),
            new ConfigValueResolver(new UnavailableSecretStore()),
            NullLog.Instance);
}
