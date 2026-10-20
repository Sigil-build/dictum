using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
using Dictum.Core.Stt;

namespace Dictum.Stt.OpenAi;

/// <summary>
/// Client for any OpenAI-compatible <c>/audio/transcriptions</c> endpoint:
/// whisper-server behind llama-swap, speaches, OpenAI, Groq.
/// </summary>
public sealed class OpenAiCompatibleStt(
    string id,
    OpenAiCompatibleSttConfig config,
    HttpClient http,
    ConfigValueResolver values,
    ILog log) : ISpeechToText
{
    private const string TranscriptionsPath = "audio/transcriptions";

    /// <summary>Half a second of silence, the clip the health check sends to a server without <c>/models</c>.</summary>
    private const int ProbeClipSamples = AudioClip.WhisperSampleRate / 2;

    private readonly OpenAiCompatibleServer _server = new(http, values, $"STT provider '{id}'", config.BaseUrl, config.ApiKey);

    public string Id => id;

    public async Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var answer = await _server.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, _server.Endpoint(TranscriptionsPath)) { Content = BuildForm(clip, request) },
            config.TimeoutMs,
            ct).ConfigureAwait(false);
        if (!answer.IsSuccess)
        {
            throw new ProviderUnavailableException(
                $"STT provider '{id}' answered {(int)answer.Status} {answer.ReasonPhrase}: {OpenAiCompatible.Snippet(answer.Body)}");
        }

        TranscriptionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(answer.Body, OpenAiSttJsonContext.Default.TranscriptionResponse);
        }
        catch (JsonException ex)
        {
            throw new ProviderUnavailableException($"STT provider '{id}' returned invalid JSON: {OpenAiCompatible.Snippet(answer.Body)}", ex);
        }

        log.Debug($"STT '{id}' answered in {watch.ElapsedMilliseconds} ms");
        return new Transcript(parsed?.Text ?? string.Empty, parsed?.Language ?? Language(request), watch.Elapsed, config.Model, config.Model);
    }

    public async Task<HealthStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _server.ProbeModelsAsync(ct).ConfigureAwait(false);
            if (OpenAiCompatible.RejectsKey(response.StatusCode))
            {
                return HealthStatus.Error($"{config.BaseUrl} rejected the API key ({(int)response.StatusCode})", "Check the apiKey reference in the config.");
            }

            if (response.IsSuccessStatusCode)
            {
                return HealthStatus.Ok($"{config.BaseUrl} reachable, model {config.Model}");
            }

            // Some servers (e.g. a bare whisper-server) have no /models; a short silent clip proves the endpoint works.
            await TranscribeAsync(new AudioClip(new float[ProbeClipSamples]), new SttRequest(null, null), ct).ConfigureAwait(false);
            return HealthStatus.Ok($"{config.BaseUrl} transcribes with model {config.Model}");
        }
        catch (Exception ex) when (ex is HttpRequestException or ProviderUnavailableException or ConfigException or OperationCanceledException)
        {
            return HealthStatus.Error($"{config.BaseUrl}: {ex.Message}", "Is the server running, and is baseUrl correct (including /v1)?");
        }
    }

    private MultipartFormDataContent BuildForm(AudioClip clip, SttRequest request)
    {
        var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(Wav.ToPcm16Bytes(clip));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(config.Model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (Language(request) is { Length: > 0 } language && language != LanguageList.Auto)
        {
            form.Add(new StringContent(language), "language");
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            form.Add(new StringContent(request.Prompt), "prompt");
        }

        return form;
    }

    /// <summary>
    /// The request's language, or the first of its candidates: the endpoint takes one language, and left to detect
    /// any, a server answered Ukrainian speech in Russian.
    /// </summary>
    private static string? Language(SttRequest request) =>
        request.Language ?? (request.Candidates is [var first, ..] ? first : null);
}

internal sealed record TranscriptionResponse(string? Text, string? Language);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TranscriptionResponse))]
internal sealed partial class OpenAiSttJsonContext : JsonSerializerContext;
