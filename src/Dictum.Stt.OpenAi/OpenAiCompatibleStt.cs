using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
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
    public string Id => id;

    public SttCapabilities Capabilities { get; } = new(LanguageDetection: true, Prompt: true, Streaming: false);

    public async Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);
        var watch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("audio/transcriptions"))
        {
            Content = BuildForm(clip, request),
        };
        await AuthorizeAsync(message, ct).ConfigureAwait(false);

        var body = await SendAsync(message, timeout, ct).ConfigureAwait(false);
        TranscriptionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, OpenAiSttJsonContext.Default.TranscriptionResponse);
        }
        catch (JsonException ex)
        {
            throw new ProviderUnavailableException($"STT provider '{id}' returned invalid JSON: {Snippet(body)}", ex);
        }

        log.Debug($"STT '{id}' answered in {watch.ElapsedMilliseconds} ms");
        return new Transcript(parsed?.Text ?? string.Empty, parsed?.Language ?? request.Language, watch.Elapsed, config.Model);
    }

    public async Task<HealthStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint("models"));
            await AuthorizeAsync(message, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return HealthStatus.Error($"{config.BaseUrl} rejected the API key ({(int)response.StatusCode})", "Check the apiKey reference in the config.");
            }

            if (response.IsSuccessStatusCode)
            {
                return HealthStatus.Ok($"{config.BaseUrl} reachable, model {config.Model}");
            }

            // Some servers (e.g. a bare whisper-server) have no /models; a short silent clip proves the endpoint works.
            await TranscribeAsync(new AudioClip(new float[AudioClip.WhisperSampleRate / 2]), new SttRequest(null, null), ct).ConfigureAwait(false);
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
        if (request.Language is { Length: > 0 } language && language != "auto")
        {
            form.Add(new StringContent(language), "language");
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            form.Add(new StringContent(request.Prompt), "prompt");
        }

        return form;
    }

    private async Task<string> SendAsync(HttpRequestMessage message, CancellationTokenSource timeout, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderUnavailableException(
                    $"STT provider '{id}' answered {(int)response.StatusCode} {response.ReasonPhrase}: {Snippet(body)}");
            }

            return body;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException($"STT provider '{id}' did not answer within {config.TimeoutMs} ms.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException($"STT provider '{id}' is unreachable at {config.BaseUrl}: {ex.Message}", ex);
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage message, CancellationToken ct)
    {
        if (await values.ResolveAsync(config.ApiKey, ct).ConfigureAwait(false) is { } key)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private Uri Endpoint(string path) => new($"{config.BaseUrl.TrimEnd('/')}/{path}");

    private static string Snippet(string body) => body.Length <= 300 ? body : body[..300] + "…";
}

internal sealed record TranscriptionResponse(string? Text, string? Language);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TranscriptionResponse))]
internal sealed partial class OpenAiSttJsonContext : JsonSerializerContext;
